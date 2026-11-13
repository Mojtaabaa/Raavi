using System.Net;
using Raavi.Tests.Infrastructure;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>EKOS B2: knowledge relationship edges (SQL), ACL on both ends, Manual-only delete.</summary>
[Collection(SqlTestGroup.Name)]
public sealed class KnowledgeRelationshipTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    [Fact]
    public async Task Manual_content_links_create_list_and_remove_without_deleting_pages()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var space = await CreateSpaceAsync(admin);
        var key = space.GetProperty("key").GetString()!;
        var a = await CreatePageAsync(admin, key, "Decision A");
        var b = await CreatePageAsync(admin, key, "Incident B");
        var aId = Id(a);
        var bId = Id(b);

        using (var created = await admin.PostAsync($"api/v1/content/{aId}/relationships", new { relationshipType = "References", targetContentId = bId }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var body = await ReadAsync(created);
            Assert.Equal("References", body.GetProperty("relationshipType").GetString());
            Assert.Equal("Outgoing", body.GetProperty("direction").GetString());
            Assert.True(body.GetProperty("canRemove").GetBoolean());
        }

        using (var dup = await admin.PostAsync($"api/v1/content/{aId}/relationships", new { relationshipType = "References", targetContentId = bId }))
        {
            await Browser.AssertStatusAsync(dup, HttpStatusCode.Conflict, "relationship.duplicate");
        }

        var fromA = await admin.GetJsonAsync($"api/v1/content/{aId}/relationships");
        Assert.Contains(fromA.EnumerateArray(), r => r.GetProperty("targetContentId").GetGuid() == bId);

        var fromB = await admin.GetJsonAsync($"api/v1/content/{bId}/relationships");
        Assert.Contains(fromB.EnumerateArray(), r => r.GetProperty("direction").GetString() == "Incoming");

        var relId = fromA.EnumerateArray().First(r => r.GetProperty("targetContentId").GetGuid() == bId).GetProperty("id").GetGuid();
        using (var del = await admin.DeleteAsync($"api/v1/content/{aId}/relationships/{relId}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        }

        Assert.Empty((await admin.GetJsonAsync($"api/v1/content/{aId}/relationships")).EnumerateArray());
        Assert.Equal("Incident B", (await admin.GetJsonAsync($"api/v1/content/{bId}")).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Invisible_target_cannot_be_linked_and_self_link_is_rejected()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var open = await CreateSpaceAsync(admin, visibility: "Open");
        var secret = await CreateSpaceAsync(admin, visibility: "Private", name: "Secret");
        var openPage = await CreatePageAsync(member, open.GetProperty("key").GetString()!, "Open notes");
        var secretPage = await CreatePageAsync(admin, secret.GetProperty("key").GetString()!, "Secret notes");
        var openId = Id(openPage);
        var secretId = Id(secretPage);

        using (var denied = await member.PostAsync($"api/v1/content/{openId}/relationships", new { relationshipType = "RelatedTo", targetContentId = secretId }))
        {
            await Browser.AssertStatusAsync(denied, HttpStatusCode.NotFound);
        }

        using (var self = await admin.PostAsync($"api/v1/content/{openId}/relationships", new { relationshipType = "RelatedTo", targetContentId = openId }))
        {
            await Browser.AssertStatusAsync(self, HttpStatusCode.BadRequest, "relationship.self");
        }
    }
}
