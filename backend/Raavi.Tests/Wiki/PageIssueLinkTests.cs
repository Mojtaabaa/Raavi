using System.Net;
using System.Text.Json;
using Raavi.Tests.Infrastructure;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>Page↔Jira linking: create/link/unlink with FakeIssueTracker; Raavi owns relationships.</summary>
[Collection(SqlTestGroup.Name)]
public sealed class PageIssueLinkTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    [Fact]
    public async Task Create_link_unlink_keeps_relationship_authority_in_raavi()
    {
        fx.Tracker.Reset();
        fx.Tracker.Respond = req =>
        {
            var path = req.Url.AbsolutePath;
            if (req.Method == "POST" && path.EndsWith("/rest/api/2/issue", StringComparison.Ordinal))
            {
                return (HttpStatusCode.Created, """{"id":"10001","key":"WIKI-9"}""");
            }

            if (path.Contains("/remotelink", StringComparison.Ordinal))
            {
                if (req.Method == "GET")
                {
                    return (HttpStatusCode.OK, "[]");
                }

                if (req.Method is "POST" or "PUT")
                {
                    return (HttpStatusCode.Created, """{"id":"9001"}""");
                }

                if (req.Method == "DELETE")
                {
                    return (HttpStatusCode.NoContent, "");
                }
            }

            if (path.Contains("/issue/10001", StringComparison.Ordinal) || path.Contains("/issue/WIKI-9", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, """{"id":"10001","key":"WIKI-9","fields":{"summary":"From Raavi","status":{"name":"Open"},"issuetype":{"name":"Task"},"project":{"key":"WIKI"},"priority":{"name":"Medium"},"assignee":null,"created":"2026-11-01T00:00:00.000+0000","updated":"2026-11-01T00:00:00.000+0000"}}""");
            }

            if (path.EndsWith("/rest/api/2/project", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, """[{"id":"10000","key":"WIKI","name":"Wiki"}]""");
            }

            return (HttpStatusCode.NotFound, "{}");
        };

        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        using (var c = await admin.PostAsync("api/v1/integrations/connectors", new { name = "Jira DC", kind = "Jira", baseUrl = "https://jira.example.test", token = "jira-token" }))
        {
            await ReadAsync(c);
        }

        var connectorId = (await admin.GetJsonAsync("api/v1/integrations/jira/connectors")).EnumerateArray().First().GetProperty("id").GetGuid();
        var space = await CreateSpaceAsync(admin);
        var key = space.GetProperty("key").GetString()!;
        var page = await CreatePageAsync(admin, key, "Spec");
        var pageId = Id(page);
        using (var pub = await admin.PostAsync($"api/v1/content/{pageId}/publish", new { }))
        {
            _ = pub.StatusCode;
        }

        var created = await ReadAsync(await admin.PostAsync($"api/v1/pages/{pageId}/jira-links/create", new
        {
            connectorId,
            projectKey = "WIKI",
            issueTypeId = "10000",
            summary = "From selection",
            description = "Body",
            quote = new { exact = "Selected requirement text", prefix = "pre ", suffix = " post" },
        }));

        Assert.Equal("WIKI-9", created.GetProperty("issueKey").GetString());
        Assert.Equal("CreatedFromRaavi", created.GetProperty("source").GetString());
        var linkId = created.GetProperty("id").GetGuid();
        Assert.Contains(created.GetProperty("contexts").EnumerateArray(), x => x.GetProperty("exact").GetString() == "Selected requirement text");
        Assert.Single((await admin.GetJsonAsync($"api/v1/pages/{pageId}/jira-links")).EnumerateArray());

        var linked = await ReadAsync(await admin.PostAsync($"api/v1/pages/{pageId}/jira-links", new { connectorId, issueKeyOrId = "WIKI-9" }));
        Assert.Equal(linkId, linked.GetProperty("id").GetGuid());

        using (var del = await admin.DeleteAsync($"api/v1/pages/{pageId}/jira-links/{linkId}"))
        {
            await Browser.AssertStatusAsync(del, HttpStatusCode.NoContent);
        }

        Assert.Empty((await admin.GetJsonAsync($"api/v1/pages/{pageId}/jira-links")).EnumerateArray());
        Assert.Contains(fx.Tracker.Received, r => r.Method == "POST" && r.Url.AbsolutePath.EndsWith("/rest/api/2/issue", StringComparison.Ordinal));
    }
}
