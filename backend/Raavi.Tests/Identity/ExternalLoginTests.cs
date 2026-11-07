using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Raavi.Identity.Persistence;
using Raavi.Tests.Infrastructure;

namespace Raavi.Tests.Identity;

/// <summary>
/// OIDC / Jira / LDAP sign-in, provisioning policies, account linking, group sync and the system-host relay
/// (AUTHENTICATION_ARCHITECTURE.md §4–§7, ADR-007). External systems are in-process fakes that enforce PKCE.
/// </summary>
[Collection(SqlTestGroup.Name)]
public sealed class ExternalLoginTests(SqlServerFixture fx)
{
    private readonly IdentityTestKit _kit = new(fx);

    private sealed record Started(Dictionary<string, string> Query, Uri Url)
    {
        public string State => Query["state"];
        public string Challenge => Query["code_challenge"];
        public string RedirectUri => Query["redirect_uri"];
        public string Nonce => Query.GetValueOrDefault("nonce") ?? string.Empty;
    }

    private static Dictionary<string, string> ParseQuery(Uri url) =>
        url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));

    private static async Task SetPoliciesAsync(Browser admin, params object[] policies)
    {
        var all = new List<object> { new { providerId = "local", enabled = true, isDefault = true, displayOrder = 0, provisioning = "ExistingUsersOnly" } };
        all.AddRange(policies);
        using var r = await admin.PutAsync("api/v1/tenant/authentication-providers", all);
        await Browser.AssertStatusAsync(r, HttpStatusCode.NoContent);
    }

    private static object Policy(string id, string provisioning, string[]? groups = null, string[]? domains = null) =>
        new { providerId = id, enabled = true, isDefault = false, displayOrder = 1, provisioning, allowedExternalGroups = groups ?? [], allowedEmailDomains = domains ?? [] };

    private static async Task<Started> BeginAsync(Browser browser, string providerId, string returnPath = "/")
    {
        var body = await browser.PostOkAsync($"api/v1/auth/login/{providerId}/begin", new { returnPath });
        var url = new Uri(body.GetProperty("redirectUrl").GetString()!);
        return new Started(ParseQuery(url), url);
    }

    private static async Task<string> FollowAsync(Browser browser, string pathAndQuery)
    {
        using var r = await browser.GetAsync(pathAndQuery);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return r.Headers.Location!.ToString();
    }

    private async Task<string> OidcCallbackAsync(Browser browser, Started s, string subject, string? email, bool verified, string[]? groups = null, string? nonce = null, bool wrongKey = false)
    {
        var code = fx.External.IssueOidcCode(s.Challenge, s.RedirectUri, subject, email, verified, nonce ?? s.Nonce, groups, wrongKey);
        return await FollowAsync(browser, $"api/v1/auth/callback/oidc-test?code={code}&state={Uri.EscapeDataString(s.State)}");
    }

    private static string Unique(string p) => SqlServerFixture.Unique(p).ToLowerInvariant();

    [Fact]
    public async Task Oidc_sign_in_uses_pkce_nonce_and_provisions_according_to_policy()
    {
        var (tenant, admin, _) = await _kit.TenantWithAdminAsync();
        await SetPoliciesAsync(admin, Policy("oidc-test", "AutomaticCreation"));

        using var browser = new Browser(fx.Api, tenant.Host);
        await browser.PrimeAsync();
        var providers = await browser.GetJsonAsync("api/v1/auth/providers");
        Assert.Contains(providers.EnumerateArray(), p => p.GetProperty("id").GetString() == "oidc-test" && p.GetProperty("flow").GetString() == "redirect");

        var started = await BeginAsync(browser, "oidc-test", "/admin/members");
        Assert.Equal($"{FakeExternalIdentity.Issuer}/authorize", started.Url.GetLeftPart(UriPartial.Path));
        Assert.Equal($"https://{tenant.Host}/api/v1/auth/callback/oidc-test", started.RedirectUri);
        Assert.Equal("S256", started.Query["code_challenge_method"]);
        Assert.False(string.IsNullOrEmpty(started.Nonce));
        Assert.True(browser.Cookies.ContainsKey("__Host-raavi-login"), "browser-binding cookie must be set");

        var sub = Unique("sub");
        var location = await OidcCallbackAsync(browser, started, sub, $"{sub}@partner.example", verified: true);
        Assert.Equal("/admin/members", location);
        var me = await browser.GetJsonAsync("api/v1/auth/me");
        Assert.Equal("oidc-test", me.GetProperty("provider").GetString());
        Assert.False(me.GetProperty("isTenantAdministrator").GetBoolean());

        // The state is single-use.
        using var replay = new Browser(fx.Api, tenant.Host);
        replay.Cookies["__Host-raavi-login"] = "x";
        Assert.Equal("/login?error=auth.state_invalid", await FollowAsync(replay, $"api/v1/auth/callback/oidc-test?code=x&state={Uri.EscapeDataString(started.State)}"));

        // Same identity signs in again → same user (no duplicate account).
        using var second = new Browser(fx.Api, tenant.Host);
        await second.PrimeAsync();
        var s2 = await BeginAsync(second, "oidc-test");
        Assert.Equal("/", await OidcCallbackAsync(second, s2, sub, $"{sub}@partner.example", verified: true));
        Assert.Equal(me.GetProperty("id").GetString(), (await second.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetString());
    }

    [Fact]
    public async Task Forged_or_misbound_callbacks_are_rejected()
    {
        var (tenant, admin, _) = await _kit.TenantWithAdminAsync();
        await SetPoliciesAsync(admin, Policy("oidc-test", "AutomaticCreation"));
        var other = await _kit.TenantWithAdminAsync();
        await SetPoliciesAsync(other.Admin, Policy("oidc-test", "AutomaticCreation"));

        // 1. Callback in a different browser (no binding cookie) — login CSRF / state injection.
        using (var victim = new Browser(fx.Api, tenant.Host))
        {
            await victim.PrimeAsync();
            var s = await BeginAsync(victim, "oidc-test");
            using var attacker = new Browser(fx.Api, tenant.Host);
            var code = fx.External.IssueOidcCode(s.Challenge, s.RedirectUri, Unique("s"), null, false, s.Nonce);
            Assert.Equal("/login?error=auth.state_invalid", await FollowAsync(attacker, $"api/v1/auth/callback/oidc-test?code={code}&state={Uri.EscapeDataString(s.State)}"));
            Assert.Null(attacker.SessionCookie);
        }

        // 2. State created on tenant A replayed on tenant B's host.
        using (var b = new Browser(fx.Api, tenant.Host))
        {
            await b.PrimeAsync();
            var s = await BeginAsync(b, "oidc-test");
            using var onOther = new Browser(fx.Api, other.Tenant.Host);
            onOther.Cookies["__Host-raavi-login"] = b.Cookies["__Host-raavi-login"];
            var code = fx.External.IssueOidcCode(s.Challenge, s.RedirectUri, Unique("s"), null, false, s.Nonce);
            Assert.Equal("/login?error=auth.state_invalid", await FollowAsync(onOther, $"api/v1/auth/callback/oidc-test?code={code}&state={Uri.EscapeDataString(s.State)}"));
        }

        // 3. Wrong nonce, 4. token signed with an unknown key, 5. wrong PKCE verifier (code bound to another challenge).
        foreach (var attack in new[] { "nonce", "key", "pkce" })
        {
            using var b = new Browser(fx.Api, tenant.Host);
            await b.PrimeAsync();
            var s = await BeginAsync(b, "oidc-test");
            var code = attack switch
            {
                "nonce" => fx.External.IssueOidcCode(s.Challenge, s.RedirectUri, Unique("s"), null, false, "other-nonce"),
                "key" => fx.External.IssueOidcCode(s.Challenge, s.RedirectUri, Unique("s"), null, false, s.Nonce, wrongKey: true),
                _ => fx.External.IssueOidcCode("not-the-challenge", s.RedirectUri, Unique("s"), null, false, s.Nonce),
            };
            Assert.Equal("/login?error=auth.provider_error", await FollowAsync(b, $"api/v1/auth/callback/oidc-test?code={code}&state={Uri.EscapeDataString(s.State)}"));
            Assert.Null(b.SessionCookie);
        }

        // 6. Provider not enabled for the tenant cannot even be started.
        await SetPoliciesAsync(admin);
        using var disabled = new Browser(fx.Api, tenant.Host);
        await disabled.PrimeAsync();
        using var begin = await disabled.PostAsync("api/v1/auth/login/oidc-test/begin", new { returnPath = "/" });
        Assert.Equal(HttpStatusCode.NotFound, begin.StatusCode);
    }

    [Fact]
    public async Task Return_path_is_never_an_open_redirect()
    {
        var (tenant, admin, _) = await _kit.TenantWithAdminAsync();
        await SetPoliciesAsync(admin, Policy("oidc-test", "AutomaticCreation"));
        foreach (var evil in new[] { "https://evil.example/", "//evil.example", "/\\evil.example", "javascript:alert(1)" })
        {
            using var b = new Browser(fx.Api, tenant.Host);
            await b.PrimeAsync();
            var s = await BeginAsync(b, "oidc-test", evil);
            Assert.Equal("/", await OidcCallbackAsync(b, s, Unique("s"), null, false));
        }
    }

    [Fact]
    public async Task Provisioning_modes_email_collisions_and_gates()
    {
        var (tenant, admin, adminEmail) = await _kit.TenantWithAdminAsync();

        // ExistingUsersOnly: an unknown external identity is refused.
        await SetPoliciesAsync(admin, Policy("oidc-test", "ExistingUsersOnly"));
        using (var b = new Browser(fx.Api, tenant.Host))
        {
            await b.PrimeAsync();
            Assert.Equal("/login?error=auth.not_provisioned", await OidcCallbackAsync(b, await BeginAsync(b, "oidc-test"), Unique("s"), null, false));
        }

        // AutomaticCreation never merges into an existing account with the same (even verified) e-mail.
        await SetPoliciesAsync(admin, Policy("oidc-test", "AutomaticCreation"));
        using (var b = new Browser(fx.Api, tenant.Host))
        {
            await b.PrimeAsync();
            Assert.Equal("/login?error=auth.account_link_required", await OidcCallbackAsync(b, await BeginAsync(b, "oidc-test"), Unique("s"), adminEmail, verified: true));
        }

        // Domain gate trusts only verified e-mail.
        await SetPoliciesAsync(admin, Policy("oidc-test", "AutomaticCreation", domains: ["partner.example"]));
        using (var b = new Browser(fx.Api, tenant.Host))
        {
            await b.PrimeAsync();
            Assert.Equal("/login?error=auth.email_not_verified", await OidcCallbackAsync(b, await BeginAsync(b, "oidc-test"), Unique("s"), "x@partner.example", verified: false));
        }

        using (var b = new Browser(fx.Api, tenant.Host))
        {
            await b.PrimeAsync();
            Assert.Equal("/login?error=auth.domain_not_allowed", await OidcCallbackAsync(b, await BeginAsync(b, "oidc-test"), Unique("s"), "x@evil.example", verified: true));
        }

        // AdministratorApproval: pending until a tenant admin activates the membership.
        await SetPoliciesAsync(admin, Policy("oidc-test", "AdministratorApproval"));
        var sub = Unique("pending");
        using (var b = new Browser(fx.Api, tenant.Host))
        {
            await b.PrimeAsync();
            Assert.Equal("/login?error=auth.pending_approval", await OidcCallbackAsync(b, await BeginAsync(b, "oidc-test"), sub, null, false));
        }

        var members = await admin.GetJsonAsync("api/v1/tenant/members");
        var pending = members.EnumerateArray().Single(m => m.GetProperty("status").GetString() == "PendingApproval");
        await admin.PostOkAsync($"api/v1/tenant/members/{pending.GetProperty("userId").GetGuid()}/activate");
        using (var b = new Browser(fx.Api, tenant.Host))
        {
            await b.PrimeAsync();
            Assert.Equal("/", await OidcCallbackAsync(b, await BeginAsync(b, "oidc-test"), sub, null, false));
        }
    }

    [Fact]
    public async Task External_groups_gate_sign_in_and_are_mirrored_and_synchronized()
    {
        var (tenant, admin, _) = await _kit.TenantWithAdminAsync();
        await SetPoliciesAsync(admin, Policy("oidc-test", "AutomaticCreation", groups: ["engineering", "ops"]));
        var sub = Unique("g");

        using (var b = new Browser(fx.Api, tenant.Host))
        {
            await b.PrimeAsync();
            Assert.Equal("/login?error=auth.group_not_allowed", await OidcCallbackAsync(b, await BeginAsync(b, "oidc-test"), sub, null, false, ["sales"]));
        }

        using (var b = new Browser(fx.Api, tenant.Host))
        {
            await b.PrimeAsync();
            Assert.Equal("/", await OidcCallbackAsync(b, await BeginAsync(b, "oidc-test"), sub, null, false, ["engineering", "ops", "sales"]));
        }

        var groups = await admin.GetJsonAsync("api/v1/tenant/groups");
        Assert.Equal(1, groups.EnumerateArray().Single(g => g.GetProperty("name").GetString() == "engineering (oidc-test)").GetProperty("memberCount").GetInt32());
        Assert.Equal(1, groups.EnumerateArray().Single(g => g.GetProperty("name").GetString() == "ops (oidc-test)").GetProperty("memberCount").GetInt32());
        Assert.DoesNotContain(groups.EnumerateArray(), g => g.GetProperty("name").GetString()!.StartsWith("sales", StringComparison.Ordinal));

        // Next sign-in without "ops": removed from the mirrored ops group.
        using (var b = new Browser(fx.Api, tenant.Host))
        {
            await b.PrimeAsync();
            Assert.Equal("/", await OidcCallbackAsync(b, await BeginAsync(b, "oidc-test"), sub, null, false, ["engineering"]));
        }

        groups = await admin.GetJsonAsync("api/v1/tenant/groups");
        Assert.Equal(0, groups.EnumerateArray().Single(g => g.GetProperty("name").GetString() == "ops (oidc-test)").GetProperty("memberCount").GetInt32());
    }

    [Fact]
    public async Task Signed_in_user_links_an_external_identity_and_can_then_use_it()
    {
        var (tenant, admin, adminEmail) = await _kit.TenantWithAdminAsync();
        await SetPoliciesAsync(admin, Policy("oidc-test", "ExistingUsersOnly"));
        var adminId = (await admin.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetString();
        var sub = Unique("link");

        var body = await admin.PostOkAsync("api/v1/auth/me/identities/oidc-test/link");
        var url = new Uri(body.GetProperty("redirectUrl").GetString()!);
        var q = ParseQuery(url);
        var code = fx.External.IssueOidcCode(q["code_challenge"], q["redirect_uri"], sub, adminEmail, true, q["nonce"]);
        Assert.Equal("/me?linked=oidc-test", await FollowAsync(admin, $"api/v1/auth/callback/oidc-test?code={code}&state={Uri.EscapeDataString(q["state"])}"));

        var identities = await admin.GetJsonAsync("api/v1/auth/me/identities");
        Assert.Contains(identities.EnumerateArray(), i => i.GetProperty("providerId").GetString() == "oidc-test");

        // Now OIDC sign-in reaches the same account even under ExistingUsersOnly.
        using var b = new Browser(fx.Api, tenant.Host);
        await b.PrimeAsync();
        Assert.Equal("/", await OidcCallbackAsync(b, await BeginAsync(b, "oidc-test"), sub, null, false));
        Assert.Equal(adminId, (await b.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetString());

        // Another user cannot take over the same external identity.
        var (member, _, _) = await _kit.AddMemberAsync(tenant, admin);
        var mb = await member.PostOkAsync("api/v1/auth/me/identities/oidc-test/link");
        var mq = ParseQuery(new Uri(mb.GetProperty("redirectUrl").GetString()!));
        var mcode = fx.External.IssueOidcCode(mq["code_challenge"], mq["redirect_uri"], sub, null, false, mq["nonce"]);
        Assert.Equal("/me?error=auth.identity_in_use", await FollowAsync(member, $"api/v1/auth/callback/oidc-test?code={mcode}&state={Uri.EscapeDataString(mq["state"])}"));

        // Unlinking works while a password remains.
        var id = identities.EnumerateArray().Single(i => i.GetProperty("providerId").GetString() == "oidc-test").GetProperty("id").GetGuid();
        using var unlink = await admin.DeleteAsync($"api/v1/auth/me/identities/{id}");
        Assert.Equal(HttpStatusCode.NoContent, unlink.StatusCode);
    }

    [Fact]
    public async Task External_only_user_cannot_remove_their_last_sign_in_method()
    {
        var (tenant, admin, _) = await _kit.TenantWithAdminAsync();
        await SetPoliciesAsync(admin, Policy("oidc-test", "AutomaticCreation"));
        using var b = new Browser(fx.Api, tenant.Host);
        await b.PrimeAsync();
        await OidcCallbackAsync(b, await BeginAsync(b, "oidc-test"), Unique("only"), null, false);
        await b.PrimeAsync();
        var identity = (await b.GetJsonAsync("api/v1/auth/me/identities")).EnumerateArray().Single();
        using var r = await b.DeleteAsync($"api/v1/auth/me/identities/{identity.GetProperty("id").GetGuid()}");
        await Browser.AssertStatusAsync(r, HttpStatusCode.Conflict, "identity.last_sign_in_method");
    }

    [Fact]
    public async Task Jira_sign_in_goes_through_the_system_host_relay_with_key_as_subject_and_untrusted_email()
    {
        var (tenant, admin, _) = await _kit.TenantWithAdminAsync();
        await SetPoliciesAsync(admin, Policy("jira-test", "AutomaticCreation", groups: ["jira-software-users"]));

        using var browser = new Browser(fx.Api, tenant.Host);
        await browser.PrimeAsync();
        var s = await BeginAsync(browser, "jira-test", "/admin/groups");
        Assert.Equal($"{FakeExternalIdentity.JiraBase}/rest/oauth2/latest/authorize", s.Url.GetLeftPart(UriPartial.Path));
        Assert.Equal($"https://{SqlServerFixture.SystemHost}/api/v1/auth/relay/jira-test", s.RedirectUri);
        Assert.Equal("READ", s.Query["scope"]);

        var key = "JIRAUSER" + Random.Shared.Next(10000, 99999);
        var code = fx.External.IssueJiraCode(s.Challenge, s.RedirectUri, new FakeExternalIdentity.JiraUser(key, "sara", "sara@company.local", "Sara Ahmadi", ["jira-software-users"]));

        // Step 1: the provider redirects to the fixed callback on the system host (no tenant cookies there).
        using var systemSide = new Browser(fx.Api, SqlServerFixture.SystemHost);
        var handoff = new Uri(await FollowAsync(systemSide, $"api/v1/auth/relay/jira-test?code={code}&state={Uri.EscapeDataString(s.State)}"));
        Assert.Equal(tenant.Host, handoff.Host);
        Assert.Null(systemSide.SessionCookie);

        // Step 2: the tenant host completes it only for the browser that started the login.
        using (var stranger = new Browser(fx.Api, tenant.Host))
        {
            // (a stranger without the binding cookie consumes nothing useful: rejected)
            var h2 = await StartJiraHandoffAsync(tenant, key);
            Assert.Equal("/login?error=auth.state_invalid", await FollowAsync(stranger, h2.PathAndQuery));
        }

        Assert.Equal("/admin/groups", await FollowAsync(browser, handoff.PathAndQuery));
        var me = await browser.GetJsonAsync("api/v1/auth/me");
        Assert.Equal("jira-test", me.GetProperty("provider").GetString());

        // One-time hand-off code.
        Assert.Equal("/login?error=auth.state_invalid", await FollowAsync(browser, handoff.PathAndQuery));

        // Jira e-mail is untrusted: stored as contact, never as verified sign-in address.
        await using var scope = fx.UnscopedScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var link = await db.ExternalIdentities.AsNoTracking().SingleAsync(e => e.AuthenticationProviderId == "jira-test" && e.ExternalSubjectId == key);
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == link.UserId);
        Assert.False(user.EmailVerified);
        Assert.Null(user.NormalizedEmail);
        Assert.Equal("sara@company.local", user.Email);
    }

    private async Task<Uri> StartJiraHandoffAsync(IdentityTestKit.TestTenant tenant, string key)
    {
        using var b = new Browser(fx.Api, tenant.Host);
        await b.PrimeAsync();
        var s = await BeginAsync(b, "jira-test");
        var code = fx.External.IssueJiraCode(s.Challenge, s.RedirectUri, new FakeExternalIdentity.JiraUser(key, "sara", null, "Sara", ["jira-software-users"]));
        using var systemSide = new Browser(fx.Api, SqlServerFixture.SystemHost);
        return new Uri(await FollowAsync(systemSide, $"api/v1/auth/relay/jira-test?code={code}&state={Uri.EscapeDataString(s.State)}"));
    }

    [Fact]
    public async Task Jira_connection_test_reports_unsupported_versions()
    {
        var (_, admin, _) = await _kit.TenantWithAdminAsync();
        fx.External.JiraVersion = "9.12.5";
        var ok = await admin.PostOkAsync("api/v1/tenant/authentication-providers/jira-test/test");
        Assert.True(ok.GetProperty("isHealthy").GetBoolean());
        fx.External.JiraVersion = "8.13.1";
        var old = await admin.PostOkAsync("api/v1/tenant/authentication-providers/jira-test/test");
        Assert.False(old.GetProperty("isHealthy").GetBoolean());
        Assert.Equal("jira_version_unsupported", old.GetProperty("statusCode").GetString());
        fx.External.JiraVersion = "9.12.5";
    }

    [Fact]
    public async Task Active_directory_credentials_sign_in_with_escaped_filter_and_no_empty_password_bind()
    {
        var (tenant, admin, _) = await _kit.TenantWithAdminAsync();
        await SetPoliciesAsync(admin, Policy("corp-ad", "AutomaticCreation", groups: ["Engineering"]));
        var username = Unique("ad");
        var guid = fx.Ldap.AddUser(username, "Ad-Passw0rd!", $"{username}@corp.test", "CN=Engineering,OU=Groups,DC=test", "CN=Domain Users,CN=Users,DC=test");

        using var browser = new Browser(fx.Api, tenant.Host);
        await browser.PrimeAsync();
        using (var wrong = await browser.PostAsync("api/v1/auth/login/credentials/corp-ad", new { email = username, password = "nope" }))
        {
            await Browser.AssertStatusAsync(wrong, HttpStatusCode.Unauthorized, "auth.invalid_credentials");
        }

        var bindsBefore = fx.Ldap.Binds;
        using (var empty = await browser.PostAsync("api/v1/auth/login/credentials/corp-ad", new { email = username, password = "" }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, empty.StatusCode);
        }

        Assert.Equal(bindsBefore, fx.Ldap.Binds);

        await browser.PostOkAsync("api/v1/auth/login/credentials/corp-ad", new { email = username, password = "Ad-Passw0rd!" });
        Assert.Equal("corp-ad", (await browser.GetJsonAsync("api/v1/auth/me")).GetProperty("provider").GetString());

        await using var scope = fx.UnscopedScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        Assert.True(await db.ExternalIdentities.AnyAsync(e => e.AuthenticationProviderId == "corp-ad" && e.ExternalSubjectId == guid.ToString("D")));

        // Filter injection attempt is escaped (RFC 4515) and matches nothing.
        using var inject = await browser.PostAsync("api/v1/auth/login/credentials/corp-ad", new { email = "*)(objectClass=*", password = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, inject.StatusCode);
        Assert.Contains(fx.Ldap.Filters, f => f.Contains("\\2a\\29\\28objectClass=\\2a", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Workspace_list_shows_only_my_active_workspaces()
    {
        var a = await _kit.TenantWithAdminAsync();
        var b = await _kit.TenantWithAdminAsync();
        var token = await _kit.SystemInviteAsync(b.Tenant, a.Email, admin: false);
        using (var accept = new Browser(fx.Api, b.Tenant.Host))
        {
            await accept.PrimeAsync();
            await accept.PostOkAsync("api/v1/auth/invitations/accept", new { token, mode = "existing", password = IdentityTestKit.Password });
        }

        var list = await a.Admin.GetJsonAsync("api/v1/auth/me/workspaces");
        var slugs = list.EnumerateArray().Select(w => w.GetProperty("slug").GetString()).ToList();
        Assert.Contains(a.Tenant.Slug, slugs);
        Assert.Contains(b.Tenant.Slug, slugs);
        Assert.True(list.EnumerateArray().Single(w => w.GetProperty("slug").GetString() == a.Tenant.Slug).GetProperty("isCurrent").GetBoolean());
        Assert.Equal($"{b.Tenant.Slug}.{SqlServerFixture.BaseDomain}", new Uri(list.EnumerateArray().Single(w => w.GetProperty("slug").GetString() == b.Tenant.Slug).GetProperty("url").GetString()!).Host);

        // B's admin suspends A's admin in B → B disappears from A-admin's list. B's own list never contains A's other members' tenants.
        var memberId = (await a.Admin.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();
        await b.Admin.PostOkAsync($"api/v1/tenant/members/{memberId}/suspend");
        var after = await a.Admin.GetJsonAsync("api/v1/auth/me/workspaces");
        Assert.DoesNotContain(after.EnumerateArray(), w => w.GetProperty("slug").GetString() == b.Tenant.Slug);
        var bList = await b.Admin.GetJsonAsync("api/v1/auth/me/workspaces");
        Assert.DoesNotContain(bList.EnumerateArray(), w => w.GetProperty("slug").GetString() == a.Tenant.Slug);
    }
}
