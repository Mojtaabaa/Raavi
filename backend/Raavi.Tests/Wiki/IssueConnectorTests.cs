using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Infrastructure.Tenancy;
using Raavi.Integrations.Connectors;
using Raavi.Integrations.Persistence;
using Raavi.Tests.Identity;
using Raavi.Tests.Infrastructure;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>Phase 13a: reference parsing and host policy — no SQL needed.</summary>
public sealed class IssueReferenceTests
{
    [Theory]
    [InlineData(IssueConnectorKind.GitHub, "acme/wiki#42", "repos/acme/wiki/issues/42")]
    [InlineData(IssueConnectorKind.GitHub, "Acme-Inc/my.repo_x#7", "repos/Acme-Inc/my.repo_x/issues/7")]
    [InlineData(IssueConnectorKind.GitLab, "group/sub/project#5", "api/v4/projects/group%2Fsub%2Fproject/issues/5")]
    [InlineData(IssueConnectorKind.Jira, "WIKI-123", "rest/api/2/issue/WIKI-123?fields=summary,status")]
    public void Valid_references_build_the_expected_api_path(IssueConnectorKind kind, string reference, string expected) =>
        Assert.Equal(expected, IssueReference.ApiPath(kind, reference));

    [Theory]
    [InlineData(IssueConnectorKind.GitHub, "../../etc#1")]
    [InlineData(IssueConnectorKind.GitHub, "acme/..#1")]
    [InlineData(IssueConnectorKind.GitHub, "acme/wiki#1?x=1")]
    [InlineData(IssueConnectorKind.GitHub, "acme/wiki#1/../../user")]
    [InlineData(IssueConnectorKind.GitHub, "acme/wiki")]
    [InlineData(IssueConnectorKind.GitHub, "acme/wiki#12345678901")]
    [InlineData(IssueConnectorKind.GitHub, "acme wiki#1")]
    [InlineData(IssueConnectorKind.GitLab, "project#1")]
    [InlineData(IssueConnectorKind.GitLab, "group/../x#1")]
    [InlineData(IssueConnectorKind.GitLab, "group/project#1%2F..")]
    [InlineData(IssueConnectorKind.Jira, "wiki-1")]
    [InlineData(IssueConnectorKind.Jira, "WIKI-1/../../rest")]
    [InlineData(IssueConnectorKind.Jira, "WIKI-1&jql=x")]
    [InlineData(IssueConnectorKind.Jira, "")]
    public void Hostile_or_malformed_references_are_refused(IssueConnectorKind kind, string reference) =>
        Assert.Null(IssueReference.ApiPath(kind, reference));

    [Fact]
    public void The_connector_host_policy_has_its_own_allow_list_and_codes()
    {
        var policy = new ConnectorHostPolicy(["git.example.test"]);
        Assert.NotNull(policy.TryValidate("https://git.example.test/api/v3", out _));
        Assert.Null(policy.TryValidate("https://hooks.example.test/", out var code));
        Assert.Equal("connector.host_not_allowed", code);
        Assert.False(new ConnectorHostPolicy([]).IsConfigured);
    }
}

[Collection(SqlTestGroup.Name)]
public sealed class IssueConnectorTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private const string GitHubIssue = """{"title":"Fix the login","state":"open","html_url":"https://github.com/acme/wiki/issues/42","body":"private text"}""";

    private static async Task<JsonElement> CreateAsync(Browser admin, string name, string kind, string baseUrl, string? token = null)
    {
        using var r = await admin.PostAsync("api/v1/integrations/connectors", new { name, kind, baseUrl, token });
        return await ReadAsync(r);
    }

    [Fact]
    public async Task Connectors_are_managed_by_administrators_and_the_token_is_never_returned_or_stored_readable()
    {
        fx.Tracker.Reset();
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);

        Assert.True((await admin.GetJsonAsync("api/v1/integrations/connectors/catalog")).GetProperty("hostPolicyConfigured").GetBoolean());
        var created = await CreateAsync(admin, "GitHub Enterprise", "GitHub", "https://git.example.test/api/v3/", "ghp_supersecrettoken");
        Assert.True(created.GetProperty("hasToken").GetBoolean());
        Assert.Equal("https://git.example.test/api/v3", created.GetProperty("baseUrl").GetString()); // normalised
        Assert.DoesNotContain("ghp_supersecrettoken", created.GetRawText());
        var id = created.GetProperty("id").GetGuid();

        Assert.DoesNotContain("ghp_supersecrettoken", (await admin.GetJsonAsync("api/v1/integrations/connectors")).GetRawText());

        // Not readable in the database, readable by the right tenant's protector only.
        await using (var scope = fx.Services.CreateAsyncScope())
        {
            var t = new ResolvedTenant(tenant.Id, tenant.Code, tenant.Slug, TenantResolutionSource.BackgroundJob, null);
            await using var tenantScope = fx.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(t);
            var db = tenantScope.ServiceProvider.GetRequiredService<IntegrationsDbContext>();
            var stored = await db.IssueConnectors.AsNoTracking().SingleAsync(c => c.Id == id);
            Assert.NotNull(stored.ProtectedToken);
            Assert.DoesNotContain("ghp_supersecrettoken", stored.ProtectedToken);
        }

        // Members cannot administer.
        using (var denied = await member.PostAsync("api/v1/integrations/connectors", new { name = "x", kind = "Jira", baseUrl = "https://jira.example.test" }))
        {
            await Browser.AssertStatusAsync(denied, HttpStatusCode.Forbidden);
        }

        using (var deniedList = await member.GetAsync("api/v1/integrations/connectors"))
        {
            await Browser.AssertStatusAsync(deniedList, HttpStatusCode.Forbidden);
        }

        // Validation: unknown host, token over http, bad kind, duplicate name, query string.
        foreach (var (name, kind, url, token, code) in new[]
        {
            ("Evil", "GitHub", "https://evil.example.test/", (string?)null, "connector.host_not_allowed"),
            ("Insecure", "GitLab", "http://git.example.test/", "glpat", "connector.https_required"),
            ("Kind", "Bitbucket", "https://git.example.test/", null, "connector.kind_invalid"),
            ("Kind2", "1", "https://git.example.test/", null, "connector.kind_invalid"),
            ("Query", "GitLab", "https://git.example.test/?x=1", null, "connector.url_invalid"),
            ("GitHub Enterprise", "GitLab", "https://git.example.test/", null, "connector.name_taken"),
        })
        {
            using var bad = await admin.PostAsync("api/v1/integrations/connectors", new { name, kind, baseUrl = url, token });
            await Browser.AssertStatusAsync(bad, bad.StatusCode is HttpStatusCode.Conflict ? HttpStatusCode.Conflict : HttpStatusCode.BadRequest);
            Assert.Equal(code, Json(await bad.Content.ReadAsStringAsync()).GetProperty("code").GetString());
        }

        // Update keeps the token unless one is sent; an empty token removes it.
        var (current, etag) = await GetWithETagAsync(admin, $"api/v1/integrations/connectors/{id}");
        using (var keep = await admin.SendAsync(HttpMethod.Put, $"api/v1/integrations/connectors/{id}", new { name = "GHE", kind = "GitHub", baseUrl = "https://git.example.test/api/v3", enabled = true }, withXsrf: true, ifMatch: etag))
        {
            var updated = await ReadAsync(keep);
            Assert.True(updated.GetProperty("hasToken").GetBoolean());
            Assert.Equal(2, updated.GetProperty("revision").GetInt32());
        }

        using (var stale = await admin.SendAsync(HttpMethod.Put, $"api/v1/integrations/connectors/{id}", new { name = "GHE", kind = "GitHub", baseUrl = "https://git.example.test/api/v3" }, withXsrf: true, ifMatch: etag))
        {
            await Browser.AssertStatusAsync(stale, HttpStatusCode.PreconditionFailed);
        }

        var (_, etag2) = await GetWithETagAsync(admin, $"api/v1/integrations/connectors/{id}");
        using (var remove = await admin.SendAsync(HttpMethod.Put, $"api/v1/integrations/connectors/{id}", new { name = "GHE", kind = "GitHub", baseUrl = "https://git.example.test/api/v3", token = "" }, withXsrf: true, ifMatch: etag2))
        {
            Assert.False((await ReadAsync(remove)).GetProperty("hasToken").GetBoolean());
        }

        using (var del = await admin.DeleteAsync($"api/v1/integrations/connectors/{id}"))
        {
            await Browser.AssertStatusAsync(del, HttpStatusCode.NoContent);
        }

        Assert.Equal(0, (await admin.GetJsonAsync("api/v1/integrations/connectors")).GetArrayLength());
        _ = current;
    }

    [Fact]
    public async Task Members_can_look_up_an_issue_and_the_request_to_the_tracker_is_exactly_what_the_api_documents()
    {
        fx.Tracker.Reset();
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var gh = (await CreateAsync(admin, "GH", "GitHub", "https://git.example.test/api/v3", "ghp_tok")).GetProperty("id").GetGuid();
        var gl = (await CreateAsync(admin, "GL", "GitLab", "https://git.example.test/gitlab", "glpat_tok")).GetProperty("id").GetGuid();
        var jira = (await CreateAsync(admin, "Jira", "Jira", "https://jira.example.test/jira", "jira_pat")).GetProperty("id").GetGuid();

        fx.Tracker.Respond = r => r.Url.AbsolutePath switch
        {
            "/api/v3/repos/acme/wiki/issues/42" => (HttpStatusCode.OK, GitHubIssue),
            "/api/v3/repos/acme/wiki/issues/43" => (HttpStatusCode.OK, """{"title":"Add dark mode","state":"closed","html_url":"https://github.com/acme/wiki/pull/43","pull_request":{"merged_at":"2026-11-01T10:00:00Z"}}"""),
            _ when r.Url.OriginalString.Contains("projects/group%2Fproject/issues/5", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"title":"Broken\u0007 pipeline","state":"opened","web_url":"https://git.example.test/gitlab/group/project/-/issues/5"}"""),
            "/jira/rest/api/2/issue/WIKI-9" => (HttpStatusCode.OK, """{"key":"WIKI-9","fields":{"summary":"Plan release","status":{"name":"In Progress"}}}"""),
            _ => (HttpStatusCode.NotFound, "{}"),
        };

        var one = await member.GetJsonAsync($"api/v1/integrations/issues/lookup?connector={gh}&reference=acme%2Fwiki%2342");
        Assert.Equal(("Fix the login", "open", "issue", "https://github.com/acme/wiki/issues/42"), (one.GetProperty("title").GetString(), one.GetProperty("status").GetString(), one.GetProperty("type").GetString(), one.GetProperty("url").GetString()));
        Assert.DoesNotContain("private text", one.GetRawText()); // only title/status/link leave the tracker response
        var sent = Assert.Single(fx.Tracker.Received);
        Assert.Equal("Bearer ghp_tok", sent.Headers["Authorization"]);
        Assert.Equal("application/vnd.github+json", sent.Headers["Accept"]);
        Assert.Equal("2022-11-28", sent.Headers["X-GitHub-Api-Version"]);

        var pr = await member.GetJsonAsync($"api/v1/integrations/issues/lookup?connector={gh}&reference=acme%2Fwiki%2343");
        Assert.Equal(("merged", "pull_request"), (pr.GetProperty("status").GetString(), pr.GetProperty("type").GetString()));

        var lab = await member.GetJsonAsync($"api/v1/integrations/issues/lookup?connector={gl}&reference=group%2Fproject%235");
        Assert.Equal("Broken pipeline", lab.GetProperty("title").GetString()); // control characters removed
        Assert.Equal("glpat_tok", fx.Tracker.Received[^1].Headers["PRIVATE-TOKEN"]);
        Assert.False(fx.Tracker.Received[^1].Headers.ContainsKey("Authorization"));

        var j = await member.GetJsonAsync($"api/v1/integrations/issues/lookup?connector={jira}&reference=WIKI-9");
        Assert.Equal(("Plan release", "In Progress", "https://jira.example.test/jira/browse/WIKI-9"), (j.GetProperty("title").GetString(), j.GetProperty("status").GetString(), j.GetProperty("url").GetString()));

        // Members see the enabled connectors by name only.
        var listed = await member.GetJsonAsync("api/v1/integrations/issues/connectors");
        Assert.Equal(3, listed.GetArrayLength());
        Assert.DoesNotContain("baseUrl", listed.GetRawText());
        Assert.DoesNotContain("git.example.test", listed.GetRawText());
    }

    [Fact]
    public async Task Failures_are_mapped_to_stable_codes_and_upstream_text_never_leaks()
    {
        fx.Tracker.Reset();
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var gh = (await CreateAsync(admin, "GH", "GitHub", "https://git.example.test/api/v3", "tok")).GetProperty("id").GetGuid();
        string Url(string reference) => $"api/v1/integrations/issues/lookup?connector={gh}&reference={Uri.EscapeDataString(reference)}";

        async Task<(HttpStatusCode Status, string Code, string Raw)> Fail(string reference)
        {
            using var r = await member.GetAsync(Url(reference));
            var j = Json(await r.Content.ReadAsStringAsync());
            return (r.StatusCode, j.GetProperty("code").GetString()!, j.GetRawText());
        }

        var invalid = await Fail("../../x#1");
        Assert.Equal((HttpStatusCode.BadRequest, "issue.reference_invalid"), (invalid.Status, invalid.Code));
        Assert.Empty(fx.Tracker.Received); // nothing is sent for an invalid reference

        fx.Tracker.Respond = _ => (HttpStatusCode.NotFound, """{"message":"internal secret detail"}""");
        var nf = await Fail("acme/wiki#1");
        Assert.Equal((HttpStatusCode.NotFound, "issue.not_found"), (nf.Status, nf.Code));
        Assert.DoesNotContain("internal secret detail", nf.Raw);

        fx.Tracker.Respond = _ => (HttpStatusCode.Unauthorized, """{"message":"Bad credentials: ghp_leak"}""");
        var denied = await Fail("acme/wiki#2");
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "issue.upstream_denied"), (denied.Status, denied.Code));
        Assert.DoesNotContain("ghp_leak", denied.Raw);

        fx.Tracker.Respond = _ => (HttpStatusCode.InternalServerError, "boom");
        Assert.Equal("issue.upstream_unreachable", (await Fail("acme/wiki#3")).Code);

        fx.Tracker.Respond = _ => (HttpStatusCode.OK, "<html>not json</html>");
        Assert.Equal("issue.upstream_invalid", (await Fail("acme/wiki#4")).Code);

        fx.Tracker.Respond = _ => (HttpStatusCode.OK, """{"title":"x","state":"open","html_url":"javascript:alert(1)"}""");
        Assert.Equal("issue.upstream_invalid", (await Fail("acme/wiki#5")).Code); // only http(s) links are passed on

        fx.Tracker.Respond = _ => (HttpStatusCode.OK, "{\"title\":\"" + new string('a', IssueLookupService.MaxResponseBytes) + "\",\"state\":\"open\",\"html_url\":\"https://x.test/1\"}");
        Assert.Equal("issue.upstream_invalid", (await Fail("acme/wiki#6")).Code); // oversized answer

        fx.Tracker.Unreachable = true;
        Assert.Equal("issue.upstream_unreachable", (await Fail("acme/wiki#7")).Code);
    }

    [Fact]
    public async Task Results_are_cached_disabled_connectors_refuse_and_tenants_are_isolated()
    {
        fx.Tracker.Reset();
        var (tenantA, adminA, _) = await _kit.Identity.TenantWithAdminAsync();
        var (memberA, _, _) = await _kit.Identity.AddMemberAsync(tenantA, adminA);
        var (_, adminB, _) = await _kit.Identity.TenantWithAdminAsync();
        var created = await CreateAsync(adminA, "GH", "GitHub", "https://git.example.test/api/v3");
        var id = created.GetProperty("id").GetGuid();
        fx.Tracker.Respond = _ => (HttpStatusCode.OK, GitHubIssue);

        string Url() => $"api/v1/integrations/issues/lookup?connector={id}&reference=acme%2Fwiki%2342";
        await memberA.GetJsonAsync(Url());
        await memberA.GetJsonAsync(Url());
        Assert.Single(fx.Tracker.Received); // second answer from the cache
        Assert.False(fx.Tracker.Received[0].Headers.ContainsKey("Authorization")); // no token configured -> none sent

        // Another workspace cannot see or use it.
        using (var other = await adminB.GetAsync(Url()))
        {
            await Browser.AssertStatusAsync(other, HttpStatusCode.NotFound);
        }

        Assert.Equal(0, (await adminB.GetJsonAsync("api/v1/integrations/connectors")).GetArrayLength());
        using (var del = await adminB.DeleteAsync($"api/v1/integrations/connectors/{id}"))
        {
            await Browser.AssertStatusAsync(del, HttpStatusCode.NotFound);
        }

        // Disabling stops lookups (and the cache is bypassed because the revision changed).
        var (_, etag) = await GetWithETagAsync(adminA, $"api/v1/integrations/connectors/{id}");
        using (var off = await adminA.SendAsync(HttpMethod.Put, $"api/v1/integrations/connectors/{id}", new { name = "GH", kind = "GitHub", baseUrl = "https://git.example.test/api/v3", enabled = false }, withXsrf: true, ifMatch: etag))
        {
            Assert.False((await ReadAsync(off)).GetProperty("enabled").GetBoolean());
        }

        using (var refused = await memberA.GetAsync(Url()))
        {
            await Browser.AssertStatusAsync(refused, HttpStatusCode.Conflict);
            Assert.Equal("connector.disabled", Json(await refused.Content.ReadAsStringAsync()).GetProperty("code").GetString());
        }

        Assert.Equal(0, (await memberA.GetJsonAsync("api/v1/integrations/issues/connectors")).GetArrayLength());
    }
}
