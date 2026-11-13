using System.Net;
using System.Text.Json;
using Raavi.Domain.Common;
using Raavi.Tests.Infrastructure;
using Raavi.Wiki.Domain;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

public sealed class LabelDomainTests
{
    [Fact]
    public void Display_names_fold_persian_letters_and_slugify()
    {
        var now = DateTimeOffset.UtcNow;
        var label = Label.CreateFromDisplay("Backend ی", null, "#336699", null, 0, Guid.NewGuid(), now);
        Assert.Equal("Backend ی", label.DisplayName);
        Assert.Contains("ی", label.NormalizedName, StringComparison.Ordinal);
        Assert.DoesNotContain('ي', label.NormalizedName);
        Assert.Equal(label.Slug, label.Name);
        Assert.DoesNotContain(' ', label.Slug);

        Assert.Equal("label.display_invalid", Assert.Throws<DomainException>(() => Label.CreateFromDisplay("  ", null, null, null, 0, Guid.NewGuid(), now)).Code);
        Assert.Equal(Label.FoldDisplay("كتاب"), Label.FoldDisplay("کتاب"));
    }

    [Fact]
    public void Confluence_normalize_still_rejects_spaces()
    {
        Assert.Equal("label.invalid", Assert.Throws<DomainException>(() => Label.Normalize("two words")).Code);
        Assert.Equal("ops", Label.Normalize("Ops"));
    }
}

[Collection(SqlTestGroup.Name)]
public sealed class LabelTaxonomyTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    [Fact]
    public async Task Admin_can_create_hierarchy_merge_and_search_all()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var pageA = Id(await CreatePageAsync(admin, key, "Alpha"));
        var pageB = Id(await CreatePageAsync(admin, key, "Beta"));

        var parent = await ReadAsync(await admin.PostAsync("api/v1/admin/labels", new { displayName = "Development", color = "#111111" }));
        var parentId = parent.GetProperty("id").GetGuid();
        var child = await ReadAsync(await admin.PostAsync("api/v1/admin/labels", new
        {
            displayName = "Backend",
            parentId,
            aliases = new[] { "BE", "back-end" },
        }));
        var childId = child.GetProperty("id").GetGuid();
        Assert.Equal(parentId, child.GetProperty("parentId").GetGuid());
        Assert.Contains(child.GetProperty("aliases").EnumerateArray().Select(a => a.GetString()), a => a == "BE");

        var leaf = await ReadAsync(await admin.PostAsync("api/v1/admin/labels", new { displayName = "DotNet", parentId = childId }));
        var leafSlug = leaf.GetProperty("slug").GetString()!;

        using (var add = await admin.PostAsync($"api/v1/content/{pageA}/labels", new { name = leafSlug }))
        {
            await ReadAsync(add);
        }

        using (var add = await admin.PostAsync($"api/v1/content/{pageB}/labels", new { name = child.GetProperty("slug").GetString() }))
        {
            await ReadAsync(add);
        }

        var detail = await admin.GetJsonAsync($"api/v1/labels/{leafSlug}");
        Assert.Equal("DotNet", detail.GetProperty("displayName").GetString());
        Assert.True(detail.GetProperty("usageCount").GetInt32() >= 1);

        var any = await admin.GetJsonAsync($"api/v1/labels/search?tags={leafSlug},{child.GetProperty("slug").GetString()}&tagMode=any");
        Assert.True(any.GetProperty("total").GetInt32() >= 2);

        var all = await admin.GetJsonAsync($"api/v1/labels/search?tags={leafSlug},{child.GetProperty("slug").GetString()}&tagMode=all");
        Assert.Equal(0, all.GetProperty("total").GetInt32());

        var withDesc = await admin.GetJsonAsync($"api/v1/labels/search?tags={child.GetProperty("slug").GetString()}&includeDescendants=true&tagMode=any");
        Assert.True(withDesc.GetProperty("total").GetInt32() >= 2);

        var other = await ReadAsync(await admin.PostAsync("api/v1/admin/labels", new { displayName = "MergeMe" }));
        using (var add = await admin.PostAsync($"api/v1/content/{pageB}/labels", new { name = other.GetProperty("slug").GetString() }))
        {
            await ReadAsync(add);
        }

        var preview = await ReadAsync(await admin.PostAsync("api/v1/admin/labels/merge/preview", new { sourceId = other.GetProperty("id").GetGuid(), destinationId = childId }));
        Assert.True(preview.GetProperty("pageLinks").GetInt32() >= 1);

        await ReadAsync(await admin.PostAsync("api/v1/admin/labels/merge", new { sourceId = other.GetProperty("id").GetGuid(), destinationId = childId }));
        using (var gone = await admin.GetAsync($"api/v1/labels/{other.GetProperty("slug").GetString()}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }

        var analytics = await admin.GetJsonAsync("api/v1/admin/labels/analytics");
        Assert.True(analytics.GetProperty("totalActive").GetInt32() >= 2);

        using (var denied = await admin.PostAsync("api/v1/admin/labels", new { displayName = "Development" }))
        {
            await Browser.AssertStatusAsync(denied, HttpStatusCode.Conflict, "label.duplicate");
        }
    }

    [Fact]
    public async Task Member_cannot_manage_labels_but_can_suggest_and_view()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var page = Id(await CreatePageAsync(admin, key, "Guide"));
        await ReadAsync(await admin.PostAsync("api/v1/admin/labels", new { displayName = "Docs", aliases = new[] { "documentation" } }));
        using (var add = await admin.PostAsync($"api/v1/content/{page}/labels", new { name = "docs" }))
        {
            await ReadAsync(add);
        }

        using (var denied = await member.PostAsync("api/v1/admin/labels", new { displayName = "Secret" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        Assert.Contains("docs", (await member.GetJsonAsync("api/v1/labels?prefix=d")).EnumerateArray().Select(l => l.GetString()));
        var byAlias = await member.GetJsonAsync("api/v1/labels/docs");
        Assert.Equal("Docs", byAlias.GetProperty("displayName").GetString());

        var suggestions = await admin.GetJsonAsync($"api/v1/content/{page}/label-suggestions");
        Assert.Equal(JsonValueKind.Array, suggestions.ValueKind);
    }
}
