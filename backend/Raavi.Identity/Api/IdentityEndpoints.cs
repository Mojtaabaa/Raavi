using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Application.Abstractions.Identity;
using Raavi.Identity.Application;
using Raavi.Identity.ExternalLogin;
using Raavi.Identity.Providers;
using Raavi.Identity.Persistence;
using Raavi.Identity.Security;
using Raavi.Infrastructure.Http;

namespace Raavi.Identity.Api;

public static class IdentityEndpoints
{
    public const string LoginRateLimitPolicy = "raavi.login";

    public static void MapIdentityEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var auth = api.MapGroup("/auth").AllowAnonymous();

        // ── Any host ───────────────────────────────────────────────────────────────────────────
        auth.MapGet("/antiforgery", (HttpContext http, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(http);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { token = tokens.RequestToken, headerName = AntiforgeryProtection.HeaderName });
        });

        auth.MapPost("/logout", async (HttpContext http, SessionService sessions, CancellationToken ct) =>
        {
            if (http.Request.Cookies.TryGetValue(sessions.CookieName, out var token) && !string.IsNullOrEmpty(token))
            {
                await sessions.RevokeTokenAsync(token, "logout", ct).ConfigureAwait(false);
            }

            sessions.ClearCookie(http);
            return Results.NoContent();
        });

        api.MapGet("/auth/me", async (HttpContext http, ICurrentUser current, IdentityDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == current.UserId, ct).ConfigureAwait(false);
            return Results.Ok(AuthService.Me(user, http.GetHostKind() == RaaviHostKind.System ? "system" : "tenant",
                http.User.IsInRole(RaaviPolicies.TenantAdministratorRole), http.User.IsInRole(RaaviPolicies.SystemAdministratorRole),
                current.AuthenticationProviderId ?? "local"));
        }).RequireAuthorization(RaaviPolicies.AuthenticatedUser);

        api.MapPost("/auth/me/identities/{providerId}/link", async (HttpContext http, string providerId, ExternalLoginFlow flow, CancellationToken ct) =>
            (await flow.BeginAsync(http, providerId, ExternalLoginMode.Link, "/me", ct).ConfigureAwait(false)).ToHttp(url => Results.Ok(new { redirectUrl = url })))
            .RequireHostKind(RaaviHostKind.Tenant)
            .RequireAuthorization(RaaviPolicies.AuthenticatedUser);

        api.MapGet("/auth/me/identities", async (AccountService svc, CancellationToken ct) => Results.Ok(await svc.ListIdentitiesAsync(ct).ConfigureAwait(false)))
            .RequireAuthorization(RaaviPolicies.AuthenticatedUser);

        api.MapDelete("/auth/me/identities/{id:guid}", async (Guid id, AccountService svc, CancellationToken ct) => (await svc.UnlinkAsync(id, ct).ConfigureAwait(false)).ToHttp())
            .RequireAuthorization(RaaviPolicies.AuthenticatedUser);

        api.MapGet("/auth/me/workspaces", async (AccountService svc, CancellationToken ct) => Results.Ok(await svc.ListWorkspacesAsync(ct).ConfigureAwait(false)))
            .RequireAuthorization(RaaviPolicies.AuthenticatedUser);

        api.MapPut("/auth/me/preferences", async (PreferencesRequest body, ICurrentUser current, IdentityDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.FirstAsync(u => u.Id == current.UserId, ct).ConfigureAwait(false);
            user.SetPreferredLocale(body.Locale);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Results.NoContent();
        }).RequireAuthorization(RaaviPolicies.AuthenticatedUser);

        // Users and groups of the current tenant, for permission pickers (any member; results are tenant-scoped).
        api.MapGet("/directory", async (string? q, IPrincipalDirectory directory, CancellationToken ct) =>
            Results.Ok(await directory.SearchAsync(q ?? string.Empty, 20, ct).ConfigureAwait(false)))
            .RequireHostKind(RaaviHostKind.Tenant)
            .RequireAuthorization(RaaviPolicies.AuthenticatedUser);

        // ── Tenant host ────────────────────────────────────────────────────────────────────────
        var tenantAuth = auth.MapGroup("").RequireHostKind(RaaviHostKind.Tenant);

        tenantAuth.MapGet("/providers", async (AuthService svc, CancellationToken ct) => Results.Ok(await svc.ListProvidersAsync(ct).ConfigureAwait(false)));

        tenantAuth.MapPost("/login/local", async (HttpContext http, LocalLoginRequest body, AuthService svc, CancellationToken ct) =>
            (await svc.LoginLocalAsync(http, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok))
            .RequireRateLimiting(LoginRateLimitPolicy);

        tenantAuth.MapPost("/login/credentials/{providerId}", async (HttpContext http, string providerId, LocalLoginRequest body, AuthService svc, CancellationToken ct) =>
            (await svc.LoginWithCredentialsAsync(http, providerId, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok))
            .RequireRateLimiting(LoginRateLimitPolicy);

        // Redirect providers (OIDC, Jira). Begin is a POST (antiforgery), the callback is a top-level GET protected by
        // the single-use state, PKCE and the host-only browser-binding cookie.
        tenantAuth.MapPost("/login/{providerId}/begin", async (HttpContext http, string providerId, BeginLoginRequest body, ExternalLoginFlow flow, CancellationToken ct) =>
            (await flow.BeginAsync(http, providerId, ExternalLoginMode.SignIn, body.ReturnPath, ct).ConfigureAwait(false)).ToHttp(url => Results.Ok(new { redirectUrl = url })))
            .RequireRateLimiting(LoginRateLimitPolicy);

        tenantAuth.MapGet("/callback/{providerId}", async (HttpContext http, string providerId, ExternalLoginFlow flow, CancellationToken ct) =>
            Results.Redirect(await flow.CompleteOnTenantHostAsync(http, providerId, ct).ConfigureAwait(false)))
            .RequireRateLimiting(LoginRateLimitPolicy);

        tenantAuth.MapGet("/relay/complete", async (HttpContext http, ExternalLoginFlow flow, CancellationToken ct) =>
            Results.Redirect(await flow.CompleteRelayOnTenantHostAsync(http, ct).ConfigureAwait(false)))
            .RequireRateLimiting(LoginRateLimitPolicy);

        auth.MapGet("/relay/{providerId}", async (HttpContext http, string providerId, ExternalLoginFlow flow, CancellationToken ct) =>
            Results.Redirect((await flow.RelayFromSystemHostAsync(http, providerId, ct).ConfigureAwait(false)).ToString()))
            .RequireHostKind(RaaviHostKind.System)
            .RequireRateLimiting(LoginRateLimitPolicy);

        tenantAuth.MapPost("/invitations/inspect", async (SystemSetupBeginRequest body, AuthService svc, CancellationToken ct) =>
            (await svc.InspectInvitationAsync(body.Token, ct).ConfigureAwait(false)).ToHttp(Results.Ok))
            .RequireRateLimiting(LoginRateLimitPolicy);

        tenantAuth.MapPost("/invitations/accept", async (HttpContext http, AcceptInvitationRequest body, AuthService svc, CancellationToken ct) =>
            (await svc.AcceptInvitationAsync(http, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok))
            .RequireRateLimiting(LoginRateLimitPolicy);

        // ── System host ────────────────────────────────────────────────────────────────────────
        var systemAuth = auth.MapGroup("/system").RequireHostKind(RaaviHostKind.System);

        systemAuth.MapPost("/login", async (HttpContext http, SystemLoginRequest body, SystemIdentityService svc, CancellationToken ct) =>
            (await svc.LoginAsync(http, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok))
            .RequireRateLimiting(LoginRateLimitPolicy);

        systemAuth.MapPost("/first-login", async (HttpContext http, SystemFirstLoginRequest body, SystemIdentityService svc, CancellationToken ct) =>
            (await svc.FirstLoginAsync(body, BaseUrl(http), ct).ConfigureAwait(false)).ToHttp(Results.Ok))
            .RequireRateLimiting(LoginRateLimitPolicy);

        systemAuth.MapPost("/setup/inspect", async (SystemSetupBeginRequest body, SystemIdentityService svc, CancellationToken ct) =>
            (await svc.InspectSetupAsync(body.Token, ct).ConfigureAwait(false)).ToHttp(Results.Ok))
            .RequireRateLimiting(LoginRateLimitPolicy);

        systemAuth.MapPost("/setup/begin", async (SystemSetupBeginRequest body, SystemIdentityService svc, CancellationToken ct) =>
            (await svc.BeginSetupAsync(body, ct).ConfigureAwait(false)).ToHttp(Results.Ok))
            .RequireRateLimiting(LoginRateLimitPolicy);

        systemAuth.MapPost("/setup/complete", async (HttpContext http, SystemSetupCompleteRequest body, SystemIdentityService svc, CancellationToken ct) =>
            (await svc.CompleteSetupAsync(http, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok))
            .RequireRateLimiting(LoginRateLimitPolicy);

        // ── Tenant administration (tenant host) ────────────────────────────────────────────────
        var admin = api.MapGroup("/tenant").RequireHostKind(RaaviHostKind.Tenant).RequireAuthorization(RaaviPolicies.TenantAdministrator);

        admin.MapGet("/members", async (TenantIdentityAdminService svc, CancellationToken ct) => Results.Ok(await svc.ListMembersAsync(ct).ConfigureAwait(false)));
        admin.MapPost("/members/{userId:guid}/suspend", async (Guid userId, TenantIdentityAdminService svc, CancellationToken ct) => (await svc.SetMemberStatusAsync(userId, false, ct).ConfigureAwait(false)).ToHttp());
        admin.MapPost("/members/{userId:guid}/activate", async (Guid userId, TenantIdentityAdminService svc, CancellationToken ct) => (await svc.SetMemberStatusAsync(userId, true, ct).ConfigureAwait(false)).ToHttp());
        admin.MapDelete("/members/{userId:guid}", async (Guid userId, TenantIdentityAdminService svc, CancellationToken ct) => (await svc.RemoveMemberAsync(userId, ct).ConfigureAwait(false)).ToHttp());
        admin.MapPut("/members/{userId:guid}/roles", async (Guid userId, SetRolesRequest body, TenantIdentityAdminService svc, CancellationToken ct) => (await svc.SetRolesAsync(userId, body, ct).ConfigureAwait(false)).ToHttp());

        admin.MapGet("/invitations", async (TenantIdentityAdminService svc, CancellationToken ct) => Results.Ok(await svc.ListInvitationsAsync(ct).ConfigureAwait(false)));
        admin.MapPost("/invitations", async (HttpContext http, CreateInvitationRequest body, TenantIdentityAdminService svc, CancellationToken ct) =>
            (await svc.InviteAsync(body, BaseUrl(http), ct).ConfigureAwait(false)).ToHttp(i => Results.Created($"/api/v1/tenant/invitations/{i.Id}", i)));
        admin.MapDelete("/invitations/{id:guid}", async (Guid id, TenantIdentityAdminService svc, CancellationToken ct) => (await svc.RevokeInvitationAsync(id, ct).ConfigureAwait(false)).ToHttp());

        admin.MapGet("/groups", async (TenantIdentityAdminService svc, CancellationToken ct) => Results.Ok(await svc.ListGroupsAsync(ct).ConfigureAwait(false)));
        admin.MapPost("/groups", async (GroupRequest body, TenantIdentityAdminService svc, CancellationToken ct) =>
            (await svc.CreateGroupAsync(body, ct).ConfigureAwait(false)).ToHttp(g => Results.Created($"/api/v1/tenant/groups/{g.Id}", g)));
        admin.MapPut("/groups/{id:guid}", async (Guid id, GroupRequest body, TenantIdentityAdminService svc, CancellationToken ct) => (await svc.UpdateGroupAsync(id, body, ct).ConfigureAwait(false)).ToHttp());
        admin.MapDelete("/groups/{id:guid}", async (Guid id, TenantIdentityAdminService svc, CancellationToken ct) => (await svc.DeleteGroupAsync(id, ct).ConfigureAwait(false)).ToHttp());
        admin.MapGet("/groups/{id:guid}/members", async (Guid id, TenantIdentityAdminService svc, CancellationToken ct) => (await svc.ListGroupMembersAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        admin.MapPost("/groups/{id:guid}/members/{userId:guid}", async (Guid id, Guid userId, TenantIdentityAdminService svc, CancellationToken ct) => (await svc.AddGroupMemberAsync(id, userId, ct).ConfigureAwait(false)).ToHttp());
        admin.MapDelete("/groups/{id:guid}/members/{userId:guid}", async (Guid id, Guid userId, TenantIdentityAdminService svc, CancellationToken ct) => (await svc.RemoveGroupMemberAsync(id, userId, ct).ConfigureAwait(false)).ToHttp());

        admin.MapGet("/authentication-providers", async (TenantIdentityAdminService svc, CancellationToken ct) => Results.Ok(await svc.GetProviderPoliciesAsync(ct).ConfigureAwait(false)));
        admin.MapPut("/authentication-providers", async (List<ProviderPolicyRequest> body, TenantIdentityAdminService svc, CancellationToken ct) => (await svc.SetProviderPoliciesAsync(body, ct).ConfigureAwait(false)).ToHttp());
        admin.MapPost("/authentication-providers/{providerId}/test", async (string providerId, AuthenticationProviderRegistry registry, CancellationToken ct) =>
            registry.TryGet(providerId, out var provider) && provider is not null
                ? Results.Ok(await provider.TestConnectionAsync(ct).ConfigureAwait(false))
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not Found"));

        admin.MapGet("/security-events", async (int? take, TenantIdentityAdminService svc, CancellationToken ct) => Results.Ok(await svc.ListSecurityEventsAsync(take ?? 100, ct).ConfigureAwait(false)));

        // ── System administration (system host) ────────────────────────────────────────────────
        var system = api.MapGroup("/system").RequireHostKind(RaaviHostKind.System).RequireAuthorization(RaaviPolicies.SystemAdministrator);

        system.MapGet("/administrators", async (SystemIdentityService svc, CancellationToken ct) => Results.Ok(await svc.ListAdministratorsAsync(ct).ConfigureAwait(false)));
        system.MapPost("/administrators/invitations", async (HttpContext http, CreateInvitationRequest body, SystemIdentityService svc, CancellationToken ct) =>
            (await svc.InviteAdministratorAsync(body.Email, BaseUrl(http), ct).ConfigureAwait(false)).ToHttp(i => Results.Created($"/api/v1/system/administrators/invitations/{i.Id}", i)));
        system.MapPost("/tenants/{tenantId:guid}/invitations", async (Guid tenantId, CreateInvitationRequest body, SystemIdentityService svc, CancellationToken ct) =>
            (await svc.InviteIntoTenantAsync(tenantId, body, ct).ConfigureAwait(false)).ToHttp(i => Results.Created($"/api/v1/system/tenants/{tenantId}/invitations/{i.Id}", i)));
    }

    /// <summary>Public base URL of the current (already validated) host.</summary>
    private static Uri BaseUrl(HttpContext http) => new($"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}/");

    /// <summary>Claims helper used by tests and other modules.</summary>
    public static Guid? UserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
