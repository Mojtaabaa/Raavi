using System.Net;
using System.Text.Json;
using Raavi.Tests.Infrastructure;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>EKOS-8: on-demand knowledge maintenance suggestions (stale / near-duplicate).</summary>
[Collection(SqlTestGroup.Name)]
public sealed class KnowledgeMaintenanceTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    [Fact]
    public async Task Tenant_admin_sees_review_due_and_near_duplicate_suggestions_members_are_forbidden()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;

        var page = Id(await CreatePageAsync(admin, key, "Security policy handbook"));
        await CreatePageAsync(admin, key, "Security policy handbook guide");
        await CreatePageAsync(admin, key, "Unrelated cooking notes");

        var due = DateTimeOffset.UtcNow.AddDays(-2);
        using (var put = await admin.PutAsync($"api/v1/content/{page}/governance", new { reviewAt = due }))
        {
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        using (var denied = await member.GetAsync("api/v1/admin/knowledge-maintenance"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        var scan = await admin.GetJsonAsync("api/v1/admin/knowledge-maintenance?take=50");
        Assert.Equal(JsonValueKind.Array, scan.ValueKind);
        var rows = scan.EnumerateArray().ToList();

        Assert.Contains(rows, r =>
            r.GetProperty("kind").GetString() == "review_due"
            && r.GetProperty("contentId").GetGuid() == page);

        Assert.Contains(rows, r =>
            r.GetProperty("kind").GetString() == "near_duplicate"
            && (r.GetProperty("contentId").GetGuid() == page
                || r.GetProperty("relatedContentId").GetGuid() == page));

        var dup = rows.First(r => r.GetProperty("kind").GetString() == "near_duplicate");
        Assert.True(dup.GetProperty("score").GetDouble() >= 0.75);
        Assert.Equal(key, dup.GetProperty("spaceKey").GetString());
    }
}
