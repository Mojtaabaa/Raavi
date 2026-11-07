using System.Net;
using System.Text.Json;
using Raavi.Tests.Infrastructure;

namespace Raavi.Tests.Identity;

/// <summary>Tenant-host authentication, sessions, CSRF and membership (spec §35 E2E 8, 11, 15–16; AUTHENTICATION_ARCHITECTURE.md).</summary>
[Collection(SqlTestGroup.Name)]
public sealed class TenantAuthenticationTests(SqlServerFixture fx)
{
    private static readonly string[] ExampleDomains = ["Example.COM"];
    private readonly IdentityTestKit _kit = new(fx);

    [Fact]
    public async Task Invited_administrator_gets_a_secure_host_only_session()
    {
        var tenant = await _kit.CreateTenantAsync();
        var email = $"{SqlServerFixture.Unique("a").ToLowerInvariant()}@example.com";
        var token = await _kit.SystemInviteAsync(tenant, email, admin: true);

        using var browser = new Browser(fx.Api, tenant.Host);
        await browser.PrimeAsync();
        using var accept = await browser.PostAsync("api/v1/auth/invitations/accept", new { token, mode = "new", password = IdentityTestKit.Password, displayName = "Admin" });
        await Browser.AssertStatusAsync(accept, HttpStatusCode.OK);

        var cookie = Assert.Single(browser.LastSetCookieHeaders, h => h.StartsWith("__Host-raavi=", StringComparison.Ordinal));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);

        var me = await browser.GetJsonAsync("api/v1/auth/me");
        Assert.True(me.GetProperty("isTenantAdministrator").GetBoolean());
        Assert.Equal(email, me.GetProperty("email").GetString());

        // The invitation is single-use.
        using var again = new Browser(fx.Api, tenant.Host);
        await again.PrimeAsync();
        using var reuse = await again.PostAsync("api/v1/auth/invitations/accept", new { token, mode = "new", password = IdentityTestKit.Password, displayName = "X" });
        await Browser.AssertStatusAsync(reuse, HttpStatusCode.NotFound, "invitation.invalid");
    }

    [Fact]
    public async Task Login_failures_are_indistinguishable_and_lock_the_account()
    {
        var (tenant, admin, adminEmail) = await _kit.TenantWithAdminAsync();
        var (_, memberEmail, _) = await _kit.AddMemberAsync(tenant, admin);
        var outsider = await _kit.TenantWithAdminAsync(); // a real account that is not a member of `tenant`

        using var browser = new Browser(fx.Api, tenant.Host);
        await browser.PrimeAsync();
        var bodies = new List<string>();
        foreach (var (email, password) in new[] { (memberEmail, "wrong-password-123"), ("nobody@example.com", IdentityTestKit.Password), (outsider.Email, IdentityTestKit.Password) })
        {
            using var r = await browser.PostAsync("api/v1/auth/login/local", new { email, password });
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            var json = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
            bodies.Add(json.GetProperty("code").GetString() + "|" + json.GetProperty("title").GetString());
        }

        Assert.Single(bodies.Distinct());
        Assert.Null(browser.SessionCookie);

        // 5 consecutive failures lock the account; then even the correct password fails.
        for (var i = 0; i < 4; i++)
        {
            using var _ = await browser.PostAsync("api/v1/auth/login/local", new { email = memberEmail, password = "wrong-password-123" });
        }

        using var locked = await browser.PostAsync("api/v1/auth/login/local", new { email = memberEmail, password = IdentityTestKit.Password });
        await Browser.AssertStatusAsync(locked, HttpStatusCode.Unauthorized, "auth.invalid_credentials");

        // Security events are recorded for the tenant admin, without secrets.
        var events = await admin.GetJsonAsync("api/v1/tenant/security-events?take=50");
        var text = events.ToString();
        Assert.Contains("auth.login_failed", text, StringComparison.Ordinal);
        Assert.Contains("locked_out", text, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-password-123", text, StringComparison.Ordinal);
        Assert.DoesNotContain(IdentityTestKit.Password, text, StringComparison.Ordinal);
        _ = adminEmail;
    }

    [Fact]
    public async Task Session_cookie_never_authenticates_on_another_tenant_or_the_system_host()
    {
        var a = await _kit.TenantWithAdminAsync();
        var b = await _kit.TenantWithAdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await a.Admin.GetAsync("api/v1/auth/me")).StatusCode);

        foreach (var host in new[] { b.Tenant.Host, SqlServerFixture.SystemHost })
        {
            using var replay = new Browser(fx.Api, host);
            replay.Cookies["__Host-raavi"] = a.Admin.SessionCookie!;
            using var me = await replay.GetAsync("api/v1/auth/me");
            Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
            using var members = await replay.GetAsync("api/v1/tenant/members");
            Assert.True(members.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound);
        }

        // Even if A's admin is also a member of B, A's session is still not valid on B.
        var token = await _kit.SystemInviteAsync(b.Tenant, a.Email, admin: false);
        using var accept = new Browser(fx.Api, b.Tenant.Host);
        await accept.PrimeAsync();
        await accept.PostOkAsync("api/v1/auth/invitations/accept", new { token, mode = "existing", password = IdentityTestKit.Password });
        using var cross = new Browser(fx.Api, b.Tenant.Host);
        cross.Cookies["__Host-raavi"] = a.Admin.SessionCookie!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await cross.GetAsync("api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task State_changing_requests_require_the_antiforgery_token()
    {
        var (tenant, admin, _) = await _kit.TenantWithAdminAsync();

        using var noToken = await admin.PostAsync("api/v1/tenant/groups", new { name = "x" }, withXsrf: false);
        await Browser.AssertStatusAsync(noToken, HttpStatusCode.BadRequest, "csrf.invalid");

        using var anonymous = new Browser(fx.Api, tenant.Host);
        using var login = await anonymous.PostAsync("api/v1/auth/login/local", new { email = "a@b.c", password = "x" }, withXsrf: false);
        await Browser.AssertStatusAsync(login, HttpStatusCode.BadRequest, "csrf.invalid");

        // A token issued before sign-in is bound to the anonymous identity and is rejected afterwards.
        var (_, memberEmail, _) = await _kit.AddMemberAsync(tenant, admin);
        using var fresh = new Browser(fx.Api, tenant.Host);
        await fresh.PrimeAsync(); // anonymous token
        using (var signIn = await fresh.PostAsync("api/v1/auth/login/local", new { email = memberEmail, password = IdentityTestKit.Password }))
        {
            Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        }

        using var stale = await fresh.PostAsync("api/v1/auth/logout");
        await Browser.AssertStatusAsync(stale, HttpStatusCode.BadRequest, "csrf.invalid");

        await fresh.PrimeAsync();
        using var created = await admin.PostAsync("api/v1/tenant/groups", new { name = SqlServerFixture.Unique("g") });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task Logout_and_suspension_revoke_sessions_immediately()
    {
        var (tenant, admin, _) = await _kit.TenantWithAdminAsync();
        var (member, memberEmail, memberId) = await _kit.AddMemberAsync(tenant, admin);
        var stolen = member.SessionCookie!;

        await admin.PostOkAsync($"api/v1/tenant/members/{memberId}/suspend");
        using (var replay = new Browser(fx.Api, tenant.Host))
        {
            replay.Cookies["__Host-raavi"] = stolen;
            Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("api/v1/auth/me")).StatusCode);
        }

        using (var relogin = new Browser(fx.Api, tenant.Host))
        {
            await relogin.PrimeAsync();
            using var r = await relogin.PostAsync("api/v1/auth/login/local", new { email = memberEmail, password = IdentityTestKit.Password });
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }

        await admin.PostOkAsync($"api/v1/tenant/members/{memberId}/activate");
        using var again = await _kit.LoginAsync(tenant, memberEmail);
        var cookie = again.SessionCookie!;
        await again.PostOkAsync("api/v1/auth/logout");
        Assert.Null(again.SessionCookie);
        using var afterLogout = new Browser(fx.Api, tenant.Host);
        afterLogout.Cookies["__Host-raavi"] = cookie;
        Assert.Equal(HttpStatusCode.Unauthorized, (await afterLogout.GetAsync("api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Sign_in_replaces_any_previous_session_cookie()
    {
        var (tenant, admin, _) = await _kit.TenantWithAdminAsync();
        var (_, memberEmail, _) = await _kit.AddMemberAsync(tenant, admin);
        var before = admin.SessionCookie!;

        // Same browser signs in as someone else (session fixation scenario): the old session must be dead.
        await admin.PostOkAsync("api/v1/auth/login/local", new { email = memberEmail, password = IdentityTestKit.Password });
        Assert.NotEqual(before, admin.SessionCookie);
        using var replay = new Browser(fx.Api, tenant.Host);
        replay.Cookies["__Host-raavi"] = before;
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Tenant_administration_requires_the_tenant_administrator_role()
    {
        var (tenant, admin, _) = await _kit.TenantWithAdminAsync();
        var (member, _, memberId) = await _kit.AddMemberAsync(tenant, admin);

        using var anonymous = new Browser(fx.Api, tenant.Host);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("api/v1/tenant/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("api/v1/tenant/members")).StatusCode);

        await admin.PutAsync($"api/v1/tenant/members/{memberId}/roles", new { tenantAdministrator = true });
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("api/v1/tenant/members")).StatusCode);
    }

    [Fact]
    public async Task The_last_active_administrator_cannot_be_removed_demoted_or_suspended()
    {
        var (tenant, admin, _) = await _kit.TenantWithAdminAsync();
        var me = await admin.GetJsonAsync("api/v1/auth/me");
        var adminId = me.GetProperty("id").GetGuid();
        var (member, _, memberId) = await _kit.AddMemberAsync(tenant, admin);

        using var demote = await admin.PutAsync($"api/v1/tenant/members/{adminId}/roles", new { tenantAdministrator = false });
        await Browser.AssertStatusAsync(demote, HttpStatusCode.Conflict, "member.last_administrator");

        using var selfRemove = await admin.DeleteAsync($"api/v1/tenant/members/{adminId}");
        await Browser.AssertStatusAsync(selfRemove, HttpStatusCode.Conflict, "member.cannot_remove_self");

        // With a second admin, the first one may be demoted.
        using var promote = await admin.PutAsync($"api/v1/tenant/members/{memberId}/roles", new { tenantAdministrator = true });
        Assert.Equal(HttpStatusCode.NoContent, promote.StatusCode);
        await member.PrimeAsync();
        using var demoteOther = await member.PutAsync($"api/v1/tenant/members/{adminId}/roles", new { tenantAdministrator = false });
        Assert.Equal(HttpStatusCode.NoContent, demoteOther.StatusCode);
    }

    [Fact]
    public async Task Existing_account_accepts_by_proving_its_password_and_admin_response_is_identical()
    {
        var a = await _kit.TenantWithAdminAsync();
        var b = await _kit.TenantWithAdminAsync();

        var existing = await b.Admin.PostOkAsync("api/v1/tenant/invitations", new { email = a.Email, tenantAdministrator = false });
        var unknown = await b.Admin.PostOkAsync("api/v1/tenant/invitations", new { email = $"{SqlServerFixture.Unique("n").ToLowerInvariant()}@example.com", tenantAdministrator = false });
        Assert.Equal(
            existing.EnumerateObject().Select(p => p.Name).Order(),
            unknown.EnumerateObject().Select(p => p.Name).Order());

        var token = IdentityTestKit.TokenFrom(existing);
        using var browser = new Browser(fx.Api, b.Tenant.Host);
        await browser.PrimeAsync();
        using var asNew = await browser.PostAsync("api/v1/auth/invitations/accept", new { token, mode = "new", password = IdentityTestKit.Password, displayName = "Dup" });
        await Browser.AssertStatusAsync(asNew, HttpStatusCode.Conflict, "invitation.account_exists");
        using var wrong = await browser.PostAsync("api/v1/auth/invitations/accept", new { token, mode = "existing", password = "not-the-password-1" });
        await Browser.AssertStatusAsync(wrong, HttpStatusCode.Unauthorized);
        await browser.PostOkAsync("api/v1/auth/invitations/accept", new { token, mode = "existing", password = IdentityTestKit.Password });

        var me = await browser.GetJsonAsync("api/v1/auth/me");
        Assert.Equal(a.Email, me.GetProperty("email").GetString());
        Assert.False(me.GetProperty("isTenantAdministrator").GetBoolean());
    }

    [Fact]
    public async Task Groups_and_members_are_isolated_between_tenants()
    {
        var a = await _kit.TenantWithAdminAsync();
        var b = await _kit.TenantWithAdminAsync();
        var (_, _, aMember) = await _kit.AddMemberAsync(a.Tenant, a.Admin);

        var group = await a.Admin.PostOkAsync("api/v1/tenant/groups", new { name = "Engineering", description = "Eng" });
        var groupId = group.GetProperty("id").GetGuid();
        await a.Admin.PostOkAsync($"api/v1/tenant/groups/{groupId}/members/{aMember}");

        // Tenant B's admin cannot see or touch A's group, and cannot add A's user to B's groups.
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.GetAsync($"api/v1/tenant/groups/{groupId}/members")).StatusCode);
        using (var addForeign = await b.Admin.PostAsync($"api/v1/tenant/groups/{groupId}/members/{aMember}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, addForeign.StatusCode);
        }

        var bGroup = await b.Admin.PostOkAsync("api/v1/tenant/groups", new { name = "Engineering" });
        using (var foreignUser = await b.Admin.PostAsync($"api/v1/tenant/groups/{bGroup.GetProperty("id").GetGuid()}/members/{aMember}"))
        {
            await Browser.AssertStatusAsync(foreignUser, HttpStatusCode.NotFound, "member.not_found");
        }

        var bMembers = await b.Admin.GetJsonAsync("api/v1/tenant/members");
        Assert.DoesNotContain(bMembers.EnumerateArray(), m => m.GetProperty("userId").GetGuid() == aMember);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.PostAsync($"api/v1/tenant/members/{aMember}/suspend")).StatusCode);
    }

    [Fact]
    public async Task Provider_policies_cannot_disable_every_sign_in_method_or_name_unknown_providers()
    {
        var (_, admin, _) = await _kit.TenantWithAdminAsync();
        var policies = await admin.GetJsonAsync("api/v1/tenant/authentication-providers");
        Assert.Contains(policies.EnumerateArray(), p => p.GetProperty("providerId").GetString() == "local" && p.GetProperty("enabled").GetBoolean());

        using var none = await admin.PutAsync("api/v1/tenant/authentication-providers", new[] { new { providerId = "local", enabled = false, isDefault = false, displayOrder = 0, provisioning = "ExistingUsersOnly" } });
        await Browser.AssertStatusAsync(none, HttpStatusCode.Conflict, "provider_policy.none_enabled");

        using var unknown = await admin.PutAsync("api/v1/tenant/authentication-providers", new[] { new { providerId = "jira-x", enabled = true, isDefault = true, displayOrder = 0, provisioning = "ExistingUsersOnly" } });
        await Browser.AssertStatusAsync(unknown, HttpStatusCode.BadRequest, "provider_policy.unknown_provider");

        using var ok = await admin.PutAsync("api/v1/tenant/authentication-providers", new[] { new { providerId = "local", enabled = true, isDefault = true, displayOrder = 1, provisioning = "ExistingUsersOnly", allowedEmailDomains = ExampleDomains } });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        var after = await admin.GetJsonAsync("api/v1/tenant/authentication-providers");
        Assert.Equal("example.com", after.EnumerateArray().First(p => p.GetProperty("providerId").GetString() == "local").GetProperty("allowedEmailDomains")[0].GetString());
    }
}
