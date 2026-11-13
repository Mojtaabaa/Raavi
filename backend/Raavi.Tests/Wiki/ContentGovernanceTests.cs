using System.Net;
using System.Text.Json;
using Raavi.Tests.Infrastructure;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>EKOS B1: content governance side table — owner, review, classification, lifecycle (not ACL).</summary>
[Collection(SqlTestGroup.Name)]
public sealed class ContentGovernanceTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    private static async Task<Guid> MeAsync(Browser b) => (await b.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();

    [Fact]
    public async Task New_pages_and_home_pages_receive_default_governance()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var adminId = await MeAsync(admin);
        var space = await CreateSpaceAsync(admin);
        var key = space.GetProperty("key").GetString()!;
        var homeId = space.GetProperty("homePageId").GetGuid();
        var home = await admin.GetJsonAsync($"api/v1/content/{homeId}");
        AssertGovernance(home, classification: "Internal", lifecycle: "Current");
        Assert.Equal(adminId, home.GetProperty("governance").GetProperty("ownerUserId").GetGuid());

        var page = await CreatePageAsync(admin, key, "Gov page");
        AssertGovernance(page, classification: "Internal", lifecycle: "Current");
        Assert.Equal(adminId, page.GetProperty("governance").GetProperty("ownerUserId").GetGuid());
        Assert.False(page.GetProperty("governance").GetProperty("reviewDue").GetBoolean());
    }

    [Fact]
    public async Task Owner_change_does_not_grant_edit_and_review_due_is_listed()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, memberId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var space = await CreateSpaceAsync(admin, visibility: "Open");
        var key = space.GetProperty("key").GetString()!;
        var page = await CreatePageAsync(admin, key, "Needs review");
        var id = Id(page);

        var due = DateTimeOffset.UtcNow.AddDays(-1);
        using (var put = await admin.PutAsync($"api/v1/content/{id}/governance", new
        {
            ownerUserId = memberId,
            reviewOwnerUserId = memberId,
            reviewAt = due,
            classification = "Confidential",
            lifecycleStatus = "InReview",
        }))
        {
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            var body = await ReadAsync(put);
            var g = body.GetProperty("governance");
            Assert.Equal(memberId, g.GetProperty("ownerUserId").GetGuid());
            Assert.Equal("Confidential", g.GetProperty("classification").GetString());
            Assert.Equal("InReview", g.GetProperty("lifecycleStatus").GetString());
            Assert.True(g.GetProperty("reviewDue").GetBoolean());
        }

        // Ownership on a private space does not grant visibility or edit.
        var secret = await CreateSpaceAsync(admin, visibility: "Private", name: "Secret gov");
        var secretKey = secret.GetProperty("key").GetString()!;
        var secretPage = await CreatePageAsync(admin, secretKey, "Secret");
        var secretId = Id(secretPage);
        using (var put = await admin.PutAsync($"api/v1/content/{secretId}/governance", new { ownerUserId = memberId }))
        {
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        using (var denied = await member.PutAsync($"api/v1/content/{secretId}/governance", new { classification = "Public" }))
        {
            await Browser.AssertStatusAsync(denied, HttpStatusCode.NotFound);
        }

        var dueList = await admin.GetJsonAsync($"api/v1/content/review-due?ownerUserId={memberId}");
        Assert.Contains(dueList.EnumerateArray(), i => i.GetProperty("id").GetGuid() == id);

        using (var mark = await admin.PutAsync($"api/v1/content/{id}/governance", new { markReviewed = true, clearReviewAt = true, lifecycleStatus = "Current" }))
        {
            var body = await ReadAsync(mark);
            var g = body.GetProperty("governance");
            Assert.NotEqual(JsonValueKind.Null, g.GetProperty("lastReviewedAt").ValueKind);
            Assert.Equal("Current", g.GetProperty("lifecycleStatus").GetString());
            Assert.False(g.GetProperty("reviewDue").GetBoolean());
        }
    }

    [Fact]
    public async Task Classification_validation_rejects_junk_and_accepts_builtin()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var space = await CreateSpaceAsync(admin);
        var page = await CreatePageAsync(admin, space.GetProperty("key").GetString()!, "Class");
        var id = Id(page);

        using (var bad = await admin.PutAsync($"api/v1/content/{id}/governance", new { classification = "!!!bad!!!" }))
        {
            await Browser.AssertStatusAsync(bad, HttpStatusCode.BadRequest, "governance.classification_invalid");
        }

        using (var ok = await admin.PutAsync($"api/v1/content/{id}/governance", new { classification = "Restricted" }))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal("Restricted", (await ReadAsync(ok)).GetProperty("governance").GetProperty("classification").GetString());
        }
    }

    private static void AssertGovernance(JsonElement page, string classification, string lifecycle)
    {
        Assert.True(page.TryGetProperty("governance", out var g));
        Assert.Equal(classification, g.GetProperty("classification").GetString());
        Assert.Equal(lifecycle, g.GetProperty("lifecycleStatus").GetString());
        Assert.NotEqual(Guid.Empty, g.GetProperty("ownerUserId").GetGuid());
    }
}
