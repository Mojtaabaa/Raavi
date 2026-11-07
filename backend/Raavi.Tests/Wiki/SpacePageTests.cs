using System.Net;
using Raavi.Tests.Infrastructure;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>Spaces and page lifecycle over HTTP: creation rights, CRUD with ETag/If-Match, tree, move, archive, trash.</summary>
[Collection(SqlTestGroup.Name)]
public sealed class SpacePageTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    [Fact]
    public async Task Only_permitted_principals_create_spaces_and_keys_are_unique()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);

        // Secure default: only tenant administrators create spaces.
        Assert.False((await member.GetJsonAsync("api/v1/spaces-capabilities")).GetProperty("canCreateSpaces").GetBoolean());
        using (var denied = await member.PostAsync("api/v1/spaces", new { key = UniqueKey(), name = "x", type = "Team", visibility = "Open" }))
        {
            await Browser.AssertStatusAsync(denied, HttpStatusCode.Forbidden, "permission.denied");
        }

        var key = UniqueKey();
        var space = await CreateSpaceAsync(admin, key.ToLowerInvariant());
        Assert.Equal(key, space.GetProperty("key").GetString()); // keys are normalised to upper case
        Assert.NotEqual(Guid.Empty, space.GetProperty("homePageId").GetGuid());

        using (var dup = await admin.PostAsync("api/v1/spaces", new { key, name = "again", type = "Team", visibility = "Open" }))
        {
            await Browser.AssertStatusAsync(dup, HttpStatusCode.Conflict, "space.key_taken");
        }

        using (var bad = await admin.PostAsync("api/v1/spaces", new { key = "9X", name = "bad", type = "Team", visibility = "Open" }))
        {
            await Browser.AssertStatusAsync(bad, HttpStatusCode.BadRequest, "space.key_invalid");
        }

        await AllowMembersToCreateSpacesAsync(admin);
        Assert.True((await member.GetJsonAsync("api/v1/spaces-capabilities")).GetProperty("canCreateSpaces").GetBoolean());
        await CreateSpaceAsync(member);
    }

    [Fact]
    public async Task Private_spaces_are_invisible_to_other_members_and_open_spaces_are_shared()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var open = await CreateSpaceAsync(admin, visibility: "Open", name: "Open space");
        var secret = await CreateSpaceAsync(admin, visibility: "Private", name: "Secret space");
        var secretKey = secret.GetProperty("key").GetString()!;
        var secretPage = await CreatePageAsync(admin, secretKey, "Salaries");

        var listed = (await member.GetJsonAsync("api/v1/spaces")).EnumerateArray().Select(s => s.GetProperty("key").GetString()).ToList();
        Assert.Contains(open.GetProperty("key").GetString(), listed);
        Assert.DoesNotContain(secretKey, listed);

        foreach (var path in new[] { $"api/v1/spaces/{secretKey}", $"api/v1/spaces/{secretKey}/tree", $"api/v1/content/{Id(secretPage)}", $"api/v1/spaces/{secretKey}/activity" })
        {
            using var r = await member.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode); // 404, never 403: existence is not disclosed
        }

        using (var write = await member.PostAsync($"api/v1/spaces/{secretKey}/content", new { title = "x", body = Doc("x") }))
        {
            await Browser.AssertStatusAsync(write, HttpStatusCode.NotFound);
        }

        // Members of an open space can create content there.
        var page = await CreatePageAsync(member, open.GetProperty("key").GetString()!, "Team notes");
        Assert.True(page.GetProperty("rights").GetProperty("canEdit").GetBoolean());
        Assert.False(page.GetProperty("rights").GetProperty("canDelete").GetBoolean()); // delete is an administrator right by default
    }

    [Fact]
    public async Task Page_updates_require_matching_if_match_and_create_versions()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var page = await CreatePageAsync(admin, key, "Runbook", body: Doc("v1"));
        var id = Id(page);
        var (_, etag) = await GetWithETagAsync(admin, $"api/v1/content/{id}");

        using (var missing = await UpdatePageAsync(admin, id, null, "Runbook", Doc("v2")))
        {
            await Browser.AssertStatusAsync(missing, (HttpStatusCode)428, "precondition.required");
        }

        using (var ok = await UpdatePageAsync(admin, id, etag, "Runbook v2", Doc("v2", "second paragraph")))
        {
            var updated = await ReadAsync(ok);
            Assert.Equal(2, updated.GetProperty("version").GetInt32());
            Assert.Equal("second paragraph", updated.GetProperty("body").GetProperty("doc").GetProperty("content")[1].GetProperty("content")[0].GetProperty("text").GetString());
            Assert.NotEqual(etag, ok.Headers.ETag!.Tag);
        }

        // A concurrent editor still holding the old ETag loses: 412, nothing overwritten.
        using (var stale = await UpdatePageAsync(admin, id, etag, "Lost update", Doc("lost")))
        {
            await Browser.AssertStatusAsync(stale, HttpStatusCode.PreconditionFailed, "precondition.failed");
        }

        var (current, etag2) = await GetWithETagAsync(admin, $"api/v1/content/{id}");
        Assert.Equal("Runbook v2", current.GetProperty("title").GetString());

        // Saving identical content is a no-op (no new version).
        using (var same = await UpdatePageAsync(admin, id, etag2, "Runbook v2", Doc("v2", "second paragraph")))
        {
            Assert.Equal(2, (await ReadAsync(same)).GetProperty("version").GetInt32());
        }

        var versions = await admin.GetJsonAsync($"api/v1/content/{id}/versions");
        Assert.Equal([2, 1], versions.EnumerateArray().Select(v => v.GetProperty("version").GetInt32()).ToArray());

        using var invalid = await UpdatePageAsync(admin, id, etag2, "x", new { schema = "raavi.doc", schemaVersion = 1, doc = new { type = "doc", content = new[] { new { type = "script" } } } });
        await Browser.AssertStatusAsync(invalid, HttpStatusCode.BadRequest, "content.body_invalid");
    }

    [Fact]
    public async Task Tree_breadcrumbs_move_reorder_and_cycles()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var space = await CreateSpaceAsync(admin);
        var key = space.GetProperty("key").GetString()!;
        var a = Id(await CreatePageAsync(admin, key, "A"));
        var b = Id(await CreatePageAsync(admin, key, "B", a));
        var c = Id(await CreatePageAsync(admin, key, "C", b));
        var folder = Id(await CreatePageAsync(admin, key, "Folder", a, contentType: "folder"));

        var crumbs = (await admin.GetJsonAsync($"api/v1/content/{c}")).GetProperty("breadcrumbs").EnumerateArray().Select(x => x.GetProperty("title").GetString()!).ToArray();
        Assert.Equal(["A", "B"], crumbs);
        Assert.Equal(["B", "Folder"], await TreeTitlesAsync(admin, key, a));
        var top = await admin.GetJsonAsync($"api/v1/spaces/{key}/tree");
        Assert.True(top.EnumerateArray().Single(n => n.GetProperty("title").GetString() == "A").GetProperty("hasChildren").GetBoolean());

        // Move C to the top of A's children; its breadcrumbs follow (closure table re-linked).
        using (var moved = await MoveAsync(admin, c, null, a, 0))
        {
            await ReadAsync(moved);
        }

        Assert.Equal(["C", "B", "Folder"], await TreeTitlesAsync(admin, key, a));
        Assert.Equal(["A"], (await admin.GetJsonAsync($"api/v1/content/{c}")).GetProperty("breadcrumbs").EnumerateArray().Select(x => x.GetProperty("title").GetString()!).ToArray());
        Assert.Empty(await TreeTitlesAsync(admin, key, b));

        // Reorder within the same parent.
        using (var reorder = await MoveAsync(admin, c, null, a, 5))
        {
            await ReadAsync(reorder);
        }

        Assert.Equal(["B", "Folder", "C"], await TreeTitlesAsync(admin, key, a));

        // Moving a node below its own descendant is refused.
        using (var cycle = await MoveAsync(admin, a, null, b, null))
        {
            await Browser.AssertStatusAsync(cycle, HttpStatusCode.BadRequest, "content.move_into_descendant");
        }

        // Folders hold children; their tree flag is driven by real children.
        var inFolder = Id(await CreatePageAsync(admin, key, "In folder", folder));
        Assert.Equal(["In folder"], await TreeTitlesAsync(admin, key, folder));
        Assert.Equal(["A", "Folder"], (await admin.GetJsonAsync($"api/v1/content/{inFolder}")).GetProperty("breadcrumbs").EnumerateArray().Select(x => x.GetProperty("title").GetString()!).ToArray());
    }

    [Fact]
    public async Task Moving_to_another_space_takes_the_subtree_along()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var from = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var to = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var parent = Id(await CreatePageAsync(admin, from, "Parent"));
        var child = Id(await CreatePageAsync(admin, from, "Child", parent));

        using (var moved = await MoveAsync(admin, parent, to, null, null))
        {
            Assert.Equal(to, (await ReadAsync(moved)).GetProperty("spaceKey").GetString());
        }

        Assert.Equal(to, (await admin.GetJsonAsync($"api/v1/content/{child}")).GetProperty("spaceKey").GetString());
        Assert.DoesNotContain("Parent", await TreeTitlesAsync(admin, from));
        Assert.Contains("Parent", await TreeTitlesAsync(admin, to));

        // The home page cannot leave its space.
        var home = (await admin.GetJsonAsync($"api/v1/spaces/{from}")).GetProperty("homePageId").GetGuid();
        using var homeMove = await MoveAsync(admin, home, to, null, null);
        await Browser.AssertStatusAsync(homeMove, HttpStatusCode.Conflict, "content.is_home_page");
    }

    [Fact]
    public async Task Archive_and_trash_apply_to_the_subtree()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var space = await CreateSpaceAsync(admin);
        var key = space.GetProperty("key").GetString()!;
        var parent = Id(await CreatePageAsync(admin, key, "Old project"));
        var child = Id(await CreatePageAsync(admin, key, "Old child", parent));

        using (var archived = await ConditionalAsync(admin, HttpMethod.Post, $"api/v1/content/{parent}/archive"))
        {
            Assert.Equal("Archived", (await ReadAsync(archived)).GetProperty("status").GetString());
        }

        Assert.DoesNotContain("Old project", await TreeTitlesAsync(admin, key));
        var archivedChild = await admin.GetJsonAsync($"api/v1/content/{child}");
        Assert.Equal("Archived", archivedChild.GetProperty("status").GetString());
        Assert.False(archivedChild.GetProperty("rights").GetProperty("canEdit").GetBoolean()); // archived content is read-only

        using (var restored = await ConditionalAsync(admin, HttpMethod.Post, $"api/v1/content/{parent}/unarchive"))
        {
            await ReadAsync(restored);
        }

        Assert.Equal("Current", (await admin.GetJsonAsync($"api/v1/content/{child}")).GetProperty("status").GetString());

        using (var trashed = await ConditionalAsync(admin, HttpMethod.Delete, $"api/v1/content/{parent}"))
        {
            await Browser.AssertStatusAsync(trashed, HttpStatusCode.NoContent);
        }

        foreach (var id in new[] { parent, child })
        {
            using var gone = await admin.GetAsync($"api/v1/content/{id}");
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }

        var home = space.GetProperty("homePageId").GetGuid();
        using var homeDelete = await ConditionalAsync(admin, HttpMethod.Delete, $"api/v1/content/{home}");
        await Browser.AssertStatusAsync(homeDelete, HttpStatusCode.Conflict, "content.is_home_page");
    }

    [Fact]
    public async Task Archived_spaces_are_read_only_until_restored()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        using (var archive = await ConditionalAsync(admin, HttpMethod.Post, $"api/v1/spaces/{key}/archive"))
        {
            Assert.Equal("Archived", (await ReadAsync(archive)).GetProperty("status").GetString());
        }

        using (var write = await admin.PostAsync($"api/v1/spaces/{key}/content", new { title = "x", body = Doc("x") }))
        {
            await Browser.AssertStatusAsync(write, HttpStatusCode.Conflict, "space.archived");
        }

        Assert.DoesNotContain(key, (await admin.GetJsonAsync("api/v1/spaces")).EnumerateArray().Select(s => s.GetProperty("key").GetString()));
        Assert.Contains(key, (await admin.GetJsonAsync("api/v1/spaces?includeArchived=true")).EnumerateArray().Select(s => s.GetProperty("key").GetString()));

        using (var restore = await ConditionalAsync(admin, HttpMethod.Post, $"api/v1/spaces/{key}/restore"))
        {
            await ReadAsync(restore);
        }

        await CreatePageAsync(admin, key, "Works again");
    }

    [Fact]
    public async Task Labels_templates_quick_find_and_activity()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var page = Id(await CreatePageAsync(admin, key, "Deployment checklist"));

        using (var add = await admin.PostAsync($"api/v1/content/{page}/labels", new { name = "Ops" }))
        {
            Assert.Equal(["ops"], (await ReadAsync(add)).EnumerateArray().Select(l => l.GetString()!).ToArray());
        }

        using (var bad = await admin.PostAsync($"api/v1/content/{page}/labels", new { name = "two words" }))
        {
            await Browser.AssertStatusAsync(bad, HttpStatusCode.BadRequest, "label.invalid");
        }

        Assert.Contains("ops", (await member.GetJsonAsync("api/v1/labels?prefix=o")).EnumerateArray().Select(l => l.GetString()));
        Assert.Equal(page, Id((await member.GetJsonAsync("api/v1/labels/ops/content"))[0]));

        // Templates: space administrators manage them; members use them.
        using (var denied = await member.PostAsync($"api/v1/spaces/{key}/templates", new { name = "Meeting", body = Doc("Agenda") }))
        {
            await Browser.AssertStatusAsync(denied, HttpStatusCode.Forbidden);
        }

        using var created = await admin.PostAsync($"api/v1/spaces/{key}/templates", new { name = "Meeting notes", body = Doc("Agenda", "Decisions") });
        var template = Id(await ReadAsync(created));
        var fromTemplate = await CreatePageAsync(member, key, "Weekly sync", templateId: template);
        Assert.Equal("Decisions", fromTemplate.GetProperty("body").GetProperty("doc").GetProperty("content")[1].GetProperty("content")[0].GetProperty("text").GetString());

        var found = await member.GetJsonAsync("api/v1/quick-find?q=checklist");
        Assert.Equal(page, Id(found.GetProperty("content")[0]));

        // Activity is projected from the transactional outbox.
        await _kit.DispatchOutboxAsync();
        var activity = (await member.GetJsonAsync($"api/v1/spaces/{key}/activity")).EnumerateArray().Select(a => a.GetProperty("kind").GetString()).ToList();
        Assert.Contains("space.created", activity);
        Assert.Contains("content.created", activity);
        using (var remove = await admin.DeleteAsync($"api/v1/content/{page}/labels/ops"))
        {
            Assert.Empty((await ReadAsync(remove)).EnumerateArray());
        }
    }
}
