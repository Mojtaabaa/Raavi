using System.Net;
using System.Text.Json;
using Raavi.Tests.Infrastructure;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>Prompt Hub phase 1 (ADR-052): ownership, visibility, tenant isolation, the category tree and version rules.</summary>
[Collection(SqlTestGroup.Name)]
public sealed class PromptHubTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    private static async Task<JsonElement> CreateAsync(Browser b, string title, string text = "Summarise {{page.title}}", Guid? category = null, string[]? tags = null)
    {
        using var r = await b.PostAsync("api/v1/prompts", new { title, description = "d", categoryId = category, tags = tags ?? ["review"], language = "en", contexts = new[] { "page" }, modes = 7, content = Doc(text) });
        return await ReadAsync(r);
    }

    private static Guid PromptId(JsonElement detail) => detail.GetProperty("prompt").GetProperty("id").GetGuid();

    private static async Task<JsonElement> PublishPublicAsync(Browser b, Guid id)
    {
        using (var pub = await b.PostAsync($"api/v1/prompts/{id}/publish", new { changeNote = "first" })) { await ReadAsync(pub); }
        using var vis = await b.PostAsync($"api/v1/prompts/{id}/visibility", new { visibility = "Tenant" });
        return await ReadAsync(vis);
    }

    private static async Task<JsonElement> ListAsync(Browser b, string query) => await b.GetJsonAsync($"api/v1/prompts?{query}");

    private static IEnumerable<string> Titles(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()!);

    [Fact]
    public async Task A_new_prompt_is_personal_a_draft_and_invisible_to_everybody_else()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var created = await CreateAsync(member, "My secret prompt");
        var prompt = created.GetProperty("prompt");
        Assert.Equal("Personal", prompt.GetProperty("visibility").GetString());
        Assert.Equal("Draft", prompt.GetProperty("status").GetString());
        Assert.True(prompt.GetProperty("isOwner").GetBoolean());
        Assert.Contains("Summarise {{page.title}}", created.GetProperty("contentText").GetString(), StringComparison.Ordinal);

        Assert.Contains("My secret prompt", Titles(await ListAsync(member, "scope=mine")));
        Assert.Empty(Titles(await ListAsync(member, "scope=public")));
        using (var other = await admin.GetAsync($"api/v1/prompts/{PromptId(created)}")) { Assert.Equal(HttpStatusCode.NotFound, other.StatusCode); } // not even the administrator can read it
        Assert.Empty(Titles(await ListAsync(admin, "scope=mine")));
        using var anonymous = fx.Api.ForHost($"{tenant.Slug}.{SqlServerFixture.BaseDomain}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(new Uri("api/v1/prompts", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Publishing_makes_a_version_immutable_and_sharing_needs_a_published_version()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var (reader, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var created = await CreateAsync(member, "Risk review", "Find risks in {{page.content}}");
        var id = PromptId(created);

        // nothing published yet: sharing is refused
        using (var early = await member.PostAsync($"api/v1/prompts/{id}/visibility", new { visibility = "Tenant" })) { Assert.Equal(HttpStatusCode.Conflict, early.StatusCode); }

        var shared = await PublishPublicAsync(member, id);
        Assert.Equal("Tenant", shared.GetProperty("prompt").GetProperty("visibility").GetString());
        Assert.Equal("Published", shared.GetProperty("prompt").GetProperty("status").GetString());
        Assert.Contains("Risk review", Titles(await ListAsync(reader, "scope=public")));
        var seen = await reader.GetJsonAsync($"api/v1/prompts/{id}");
        Assert.False(seen.GetProperty("prompt").GetProperty("canEdit").GetBoolean());
        Assert.Contains("Find risks in {{page.content}}", seen.GetProperty("contentText").GetString(), StringComparison.Ordinal);

        // editing a published prompt starts a new draft: the public text does not change until the draft is published
        var revision = shared.GetProperty("prompt").GetProperty("revision").GetInt32();
        using (var edit = await member.PutAsync($"api/v1/prompts/{id}", new { revision, content = Doc("A better prompt for {{page.title}}"), changeNote = "reword" }))
        {
            var edited = await ReadAsync(edit);
            Assert.True(edited.GetProperty("prompt").GetProperty("hasDraft").GetBoolean());
            Assert.Equal(2, edited.GetProperty("draft").GetProperty("number").GetInt32());
            Assert.Equal(1, edited.GetProperty("published").GetProperty("number").GetInt32());
        }

        Assert.Contains("Find risks", (await reader.GetJsonAsync($"api/v1/prompts/{id}")).GetProperty("contentText").GetString(), StringComparison.Ordinal);
        Assert.Contains("A better prompt", (await member.GetJsonAsync($"api/v1/prompts/{id}")).GetProperty("contentText").GetString(), StringComparison.Ordinal); // the owner sees the draft
        using (var stale = await member.PutAsync($"api/v1/prompts/{id}", new { revision, title = "Old revision" })) { Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); }
        using (var strangerEdit = await reader.PutAsync($"api/v1/prompts/{id}", new { title = "Mine now" })) { Assert.Equal(HttpStatusCode.Forbidden, strangerEdit.StatusCode); }

        var republished = await PublishPublicAsync(member, id);
        Assert.Equal(2, republished.GetProperty("published").GetProperty("number").GetInt32());
        Assert.Contains("A better prompt", (await reader.GetJsonAsync($"api/v1/prompts/{id}")).GetProperty("contentText").GetString(), StringComparison.Ordinal);
        using (var nothing = await member.PostAsync($"api/v1/prompts/{id}/publish", new { })) { Assert.Equal(HttpStatusCode.Conflict, nothing.StatusCode); }
    }

    [Fact]
    public async Task Public_prompts_never_cross_workspaces()
    {
        var (t1, a1, _) = await _kit.Identity.TenantWithAdminAsync();
        var (t2, a2, _) = await _kit.Identity.TenantWithAdminAsync();
        var created = await CreateAsync(a1, "Only for tenant one");
        await PublishPublicAsync(a1, PromptId(created));
        Assert.NotEqual(t1.Id, t2.Id);

        Assert.Empty(Titles(await ListAsync(a2, "scope=public")));
        using (var r = await a2.GetAsync($"api/v1/prompts/{PromptId(created)}")) { Assert.Equal(HttpStatusCode.NotFound, r.StatusCode); }
        using (var r = await a2.PostAsync($"api/v1/prompts/{PromptId(created)}/visibility", new { visibility = "Personal" })) { Assert.Equal(HttpStatusCode.NotFound, r.StatusCode); }
        using (var r = await a2.PostAsync($"api/v1/prompts/{PromptId(created)}/status", new { status = "Archived" })) { Assert.Equal(HttpStatusCode.NotFound, r.StatusCode); }
    }

    [Fact]
    public async Task Search_filters_and_categories_find_the_right_prompts_and_only_owners_move_their_own()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        using var catResponse = await admin.PostAsync("api/v1/prompt-categories", new { name = "Engineering" });
        var eng = (await ReadAsync(catResponse)).GetProperty("id").GetGuid();
        using var subResponse = await admin.PostAsync("api/v1/prompt-categories", new { name = "Reviews", parentId = eng });
        var sub = (await ReadAsync(subResponse)).GetProperty("id").GetGuid();

        var a = await CreateAsync(member, "Code review", "Review the code carefully", sub, ["code", "quality"]);
        var b = await CreateAsync(member, "Meeting notes", "Summarise the meeting", null, ["notes"]);
        Assert.Equal(["Code review"], Titles(await ListAsync(member, "scope=mine&q=carefully")).ToArray()); // matches body text
        Assert.Equal(["Code review"], Titles(await ListAsync(member, $"scope=mine&categoryId={eng}")).ToArray()); // sub-categories are included
        Assert.Equal(["Meeting notes"], Titles(await ListAsync(member, "scope=mine&tag=notes")).ToArray());
        Assert.Equal(2, (await ListAsync(member, "scope=mine&context=page")).GetProperty("total").GetInt32());

        // move: only your own prompts
        using (var moved = await member.PostAsync("api/v1/prompts/move", new { ids = new[] { PromptId(b) }, categoryId = eng }))
        {
            Assert.Equal(1, (await ReadAsync(moved)).GetProperty("moved").GetInt32());
        }

        using (var theirs = await admin.PostAsync("api/v1/prompts/move", new { ids = new[] { PromptId(a) }, categoryId = (Guid?)null }))
        {
            Assert.Equal(0, (await ReadAsync(theirs)).GetProperty("moved").GetInt32());
        }

        var cats = await member.GetJsonAsync("api/v1/prompt-categories");
        Assert.Equal(1, cats.EnumerateArray().First(c => c.GetProperty("id").GetGuid() == eng).GetProperty("minePrompts").GetInt32());
    }

    [Fact]
    public async Task The_category_tree_refuses_cycles_orders_siblings_and_never_deletes_prompts_with_a_category()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        async Task<Guid> Cat(string name, Guid? parent = null)
        {
            using var r = await admin.PostAsync("api/v1/prompt-categories", new { name, parentId = parent });
            return (await ReadAsync(r)).GetProperty("id").GetGuid();
        }

        var a = await Cat("A");
        var b = await Cat("B", a);
        var c = await Cat("C", b);
        var d = await Cat("D");

        // members do not manage categories by default
        using (var denied = await member.PostAsync("api/v1/prompt-categories", new { name = "Mine" })) { Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        // cycles: A below its own grandchild, and below itself
        using (var cycle = await admin.PostAsync($"api/v1/prompt-categories/{a}/move", new { parentId = c })) { Assert.Equal(HttpStatusCode.BadRequest, cycle.StatusCode); }
        using (var self = await admin.PostAsync($"api/v1/prompt-categories/{a}/move", new { parentId = a })) { Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode); }
        // a valid move with a position
        using (var ok = await admin.PostAsync($"api/v1/prompt-categories/{d}/move", new { parentId = a, index = 0 }))
        {
            var tree = (await ReadAsync(ok)).EnumerateArray().Where(x => x.TryGetProperty("parentId", out var p) && p.ValueKind != JsonValueKind.Null && p.GetGuid() == a).OrderBy(x => x.GetProperty("sortOrder").GetInt32()).Select(x => x.GetProperty("name").GetString()!).ToArray();
            Assert.Equal(["D", "B"], tree);
        }

        var prompt = await CreateAsync(member, "Keeps living", "text", c);
        // a strategy is mandatory
        using (var none = await admin.PostAsync($"api/v1/prompt-categories/{c}/delete", new { })) { Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode); }
        using (var del = await admin.PostAsync($"api/v1/prompt-categories/{b}/delete", new { strategy = "parent" })) { Assert.Equal(HttpStatusCode.OK, del.StatusCode); }
        // B is gone; its child C moved up to A and the prompt in C is untouched
        var after = await member.GetJsonAsync("api/v1/prompt-categories");
        Assert.DoesNotContain(after.EnumerateArray(), x => x.GetProperty("id").GetGuid() == b);
        Assert.Equal(a, after.EnumerateArray().First(x => x.GetProperty("id").GetGuid() == c).GetProperty("parentId").GetGuid());
        Assert.Contains("Keeps living", Titles(await ListAsync(member, $"scope=mine&categoryId={c}")));

        // delete C and send its prompts to the uncategorized section
        using (var del = await admin.PostAsync($"api/v1/prompt-categories/{c}/delete", new { strategy = "uncategorized" })) { Assert.Equal(HttpStatusCode.OK, del.StatusCode); }
        var detail = await member.GetJsonAsync($"api/v1/prompts/{PromptId(prompt)}");
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("prompt").GetProperty("categoryId").ValueKind);

        // delete D moving its prompts into another category
        var inD = await CreateAsync(member, "In D", "x", d);
        using (var del = await admin.PostAsync($"api/v1/prompt-categories/{d}/delete", new { strategy = "category", targetCategoryId = a })) { Assert.Equal(HttpStatusCode.OK, del.StatusCode); }
        Assert.Equal(a, (await member.GetJsonAsync($"api/v1/prompts/{PromptId(inD)}")).GetProperty("prompt").GetProperty("categoryId").GetGuid());
    }

    [Fact]
    public async Task Archiving_removes_a_prompt_from_the_public_library_and_administrators_can_moderate_but_not_edit()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var created = await CreateAsync(member, "Shared tool");
        var id = PromptId(created);
        await PublishPublicAsync(member, id);

        using (var edit = await admin.PutAsync($"api/v1/prompts/{id}", new { title = "Hijacked" })) { Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode); }
        using (var del = await admin.DeleteAsync($"api/v1/prompts/{id}")) { Assert.Equal(HttpStatusCode.Conflict, del.StatusCode); } // archive first
        using (var dep = await admin.PostAsync($"api/v1/prompts/{id}/status", new { status = "Deprecated" }))
        {
            Assert.Equal("Deprecated", (await ReadAsync(dep)).GetProperty("prompt").GetProperty("status").GetString());
        }

        Assert.Contains("Shared tool", Titles(await ListAsync(member, "scope=public"))); // deprecated prompts stay visible, flagged
        using (var arch = await admin.PostAsync($"api/v1/prompts/{id}/status", new { status = "Archived" })) { Assert.Equal(HttpStatusCode.OK, arch.StatusCode); }
        Assert.Empty(Titles(await ListAsync(member, "scope=public")));
        Assert.Empty(Titles(await ListAsync(member, "scope=mine"))); // archived prompts are hidden unless asked for
        Assert.Contains("Shared tool", Titles(await ListAsync(member, "scope=mine&status=Archived")));

        using (var restore = await member.PostAsync($"api/v1/prompts/{id}/status", new { status = "Published" })) { Assert.Equal("Published", (await ReadAsync(restore)).GetProperty("prompt").GetProperty("status").GetString()); }
        using (var arch2 = await member.PostAsync($"api/v1/prompts/{id}/status", new { status = "Archived" })) { Assert.Equal(HttpStatusCode.OK, arch2.StatusCode); }
        using (var del = await member.DeleteAsync($"api/v1/prompts/{id}")) { Assert.Equal(HttpStatusCode.NoContent, del.StatusCode); }
        using (var gone = await member.GetAsync($"api/v1/prompts/{id}")) { Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode); }
    }
}
