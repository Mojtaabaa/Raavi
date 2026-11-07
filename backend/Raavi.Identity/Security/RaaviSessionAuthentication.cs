using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Identity.Domain;
using Raavi.Identity.Persistence;
using Raavi.Infrastructure.Http;

namespace Raavi.Identity.Security;

public static class RaaviClaims
{
    public const string SessionId = "raavi:sid";
    public const string Provider = "raavi:provider";
    public const string Mfa = "raavi:mfa";
}

/// <summary>
/// Default authentication scheme for browsers. A session is honoured only on the host scope it was issued for:
///   - tenant host: session.TenantId == resolved tenant AND the user is an active member of that tenant;
///   - system host: session.TenantId is null; the system-admin role additionally requires an MFA session.
/// Anything else is treated as anonymous (NoResult) — a cookie from another tenant can never authenticate here.
/// </summary>
public sealed class RaaviSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SessionService sessions,
    ITenantContext tenantContext)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "RaaviSession";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(sessions.CookieName, out var token) || string.IsNullOrEmpty(token))
        {
            return AuthenticateResult.NoResult();
        }

        // Bearer requests are exempt from CSRF validation, so the ambient session cookie must never authenticate them.
        if (Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        var hostKind = Context.GetHostKind();
        if (hostKind is null)
        {
            return AuthenticateResult.NoResult();
        }

        var session = await sessions.ValidateAsync(token, Context.RequestAborted).ConfigureAwait(false);
        if (session is null)
        {
            return AuthenticateResult.NoResult();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, session.UserId.ToString()),
            new(ClaimTypes.Name, session.DisplayName),
            new(RaaviPolicies.ActorTypeClaim, "user"),
            new(RaaviClaims.SessionId, session.SessionId.ToString()),
            new(RaaviClaims.Provider, session.ProviderId),
            new(RaaviClaims.Mfa, session.MfaSatisfied ? "true" : "false"),
        };

        if (hostKind == RaaviHostKind.Tenant)
        {
            if (!tenantContext.IsResolved || session.TenantId != tenantContext.Tenant.TenantId)
            {
                return AuthenticateResult.NoResult();
            }

            var member = await Context.RequestServices.GetRequiredService<MembershipService>().GetAsync(session.UserId, Context.RequestAborted).ConfigureAwait(false);
            if (!member.IsActiveMember)
            {
                return AuthenticateResult.NoResult();
            }

            if (member.IsTenantAdministrator)
            {
                claims.Add(new Claim(ClaimTypes.Role, RaaviPolicies.TenantAdministratorRole));
            }
        }
        else
        {
            if (session.TenantId is not null)
            {
                return AuthenticateResult.NoResult();
            }

            var db = Context.RequestServices.GetRequiredService<IdentityDbContext>();
            var isSystemAdmin = session.MfaSatisfied
                && await db.SystemRoles.AsNoTracking().AnyAsync(r => r.UserId == session.UserId && r.Role == SystemRole.SystemAdministrator, Context.RequestAborted).ConfigureAwait(false);
            if (isSystemAdmin)
            {
                claims.Add(new Claim(ClaimTypes.Role, RaaviPolicies.SystemAdministratorRole));
            }
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) => AuthProblems.ChallengeAsync(Context);

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) => AuthProblems.ForbidAsync(Context);
}

/// <summary>
/// Problem responses for authentication challenges. A policy with several schemes challenges each of them; only the
/// first may write the response, later ones must not touch it.
/// </summary>
public static class AuthProblems
{
    public static Task ChallengeAsync(HttpContext context) =>
        context?.Response.HasStarted != false ? Task.CompletedTask
            : Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication required", extensions: new Dictionary<string, object?> { ["code"] = "auth.required" }).ExecuteAsync(context);

    public static Task ForbidAsync(HttpContext context) =>
        context?.Response.HasStarted != false ? Task.CompletedTask
            : Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden", extensions: new Dictionary<string, object?> { ["code"] = "auth.forbidden" }).ExecuteAsync(context);
}

/// <summary><see cref="ICurrentUser"/> backed by the authenticated principal.</summary>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true && Principal.FindFirstValue(RaaviPolicies.ActorTypeClaim) == "user";

    public Guid UserId => IsAuthenticated && Guid.TryParse(Principal!.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? id
        : throw new InvalidOperationException("No authenticated user.");

    public string? AuthenticationProviderId => Principal?.FindFirstValue(RaaviClaims.Provider);
}
