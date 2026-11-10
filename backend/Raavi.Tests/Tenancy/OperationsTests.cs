using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Raavi.Tests.Identity;
using Raavi.Tests.Infrastructure;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Tenancy;

/// <summary>ADR-051: worker status, accounts without a workspace, and moving a single space between workspaces.</summary>
[Collection(SqlTestGroup.Name)]
public sealed class OperationsTests(SqlServerFixture fx)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Raavi.Tests.Wiki.WikiTestKit _kit = new(fx);

    [Fact]
    public async Task Worker_status_is_for_administrators_and_says_whether_a_worker_has_reported()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);

        var status = await admin.GetJsonAsync("api/v1/tenant/workers");
        Assert.Contains(status.GetProperty("state").GetString(), new[] { "Running", "Stopped", "NeverStarted", "Unknown" });
        Assert.True(status.TryGetProperty("servers", out _));
        Assert.True(status.TryGetProperty("jobs", out _));
        using (var denied = await member.GetAsync("api/v1/tenant/workers"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        using var system = fx.Api.ForSystemAdmin();
        var sys = await system.GetFromJsonAsync<JsonElement>("api/v1/system/workers", Json);
        Assert.Equal(JsonValueKind.String, sys.GetProperty("state").ValueKind);
        using var anonymous = fx.Api.ForHost(SqlServerFixture.SystemHost);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(new Uri("api/v1/system/workers", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Accounts_without_a_workspace_are_listed_and_can_be_deleted_but_never_members_or_system_administrators()
    {
        var (gone, _, goneEmail) = await _kit.Identity.TenantWithAdminAsync();
        var (_, _, keptEmail) = await _kit.Identity.TenantWithAdminAsync();
        using var system = fx.Api.ForSystemAdmin();

        // delete the first workspace for good: its administrator's account stays behind with no workspace
        using (var r = await system.PostAsJsonAsync($"api/v1/system/tenants/{gone.Id}/delete-request", new { reason = "test", confirmCode = gone.Code }))
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }

        using (var r = await system.PostAsJsonAsync($"api/v1/system/tenants/{gone.Id}/purge", new { confirmCode = gone.Code }))
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }

        var all = await system.GetFromJsonAsync<JsonElement>($"api/v1/system/users?q={Uri.EscapeDataString(goneEmail)}", Json);
        var orphan = all.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(0, orphan.GetProperty("workspaces").GetInt32());
        var keptRow = (await system.GetFromJsonAsync<JsonElement>($"api/v1/system/users?q={Uri.EscapeDataString(keptEmail)}", Json)).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(1, keptRow.GetProperty("workspaces").GetInt32());

        var onlyOrphans = await system.GetFromJsonAsync<JsonElement>("api/v1/system/users?withoutWorkspace=true&take=200", Json);
        Assert.True(onlyOrphans.GetProperty("withoutWorkspace").GetInt32() >= 1);
        Assert.All(onlyOrphans.GetProperty("items").EnumerateArray(), i => Assert.Equal(0, i.GetProperty("workspaces").GetInt32()));

        // a member of a workspace is refused even when asked for by id
        using (var r = await system.PostAsJsonAsync("api/v1/system/users/delete", new { ids = new[] { keptRow.GetProperty("id").GetGuid() } }))
        {
            var result = await r.Content.ReadFromJsonAsync<JsonElement>(Json);
            Assert.Equal(0, result.GetProperty("deleted").GetInt32());
            Assert.Equal(1, result.GetProperty("skipped").GetInt32());
        }

        using (var r = await system.PostAsJsonAsync("api/v1/system/users/delete", new { ids = Array.Empty<Guid>() }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }

        using (var r = await system.PostAsJsonAsync("api/v1/system/users/delete", new { ids = new[] { orphan.GetProperty("id").GetGuid() } }))
        {
            var result = await r.Content.ReadFromJsonAsync<JsonElement>(Json);
            Assert.Equal(1, result.GetProperty("deleted").GetInt32());
        }

        Assert.Empty((await system.GetFromJsonAsync<JsonElement>($"api/v1/system/users?q={Uri.EscapeDataString(goneEmail)}", Json)).GetProperty("items").EnumerateArray());

        // "all without a workspace" removes the rest of them and still leaves members and system administrators alone
        using (var r = await system.PostAsJsonAsync("api/v1/system/users/delete", new { allWithoutWorkspace = true }))
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }

        Assert.Single((await system.GetFromJsonAsync<JsonElement>($"api/v1/system/users?q={Uri.EscapeDataString(keptEmail)}", Json)).GetProperty("items").EnumerateArray());
        Assert.Equal(0, (await system.GetFromJsonAsync<JsonElement>("api/v1/system/users?withoutWorkspace=true", Json)).GetProperty("withoutWorkspace").GetInt32());
    }

    private static object Linked(string text, string href) => new
    {
        schema = "raavi.doc",
        schemaVersion = 2,
        doc = new { type = "doc", content = new object[] { new { type = "paragraph", content = new object[] { new { type = "text", text, marks = new object[] { new { type = "link", attrs = new { href } } } } } } } },
    };

    private static MultipartFormDataContent Upload(byte[] zip, string? key = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(zip);
        file.Headers.ContentType = new("application/zip");
        form.Add(file, "file", "space.zip");
        if (key is not null)
        {
            form.Add(new StringContent(key), "key");
        }

        return form;
    }

    [Fact]
    public async Task A_space_can_be_exported_and_imported_again_with_its_tree_labels_attachments_and_links()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var source = (await CreateSpaceAsync(admin, name: "Handbook")).GetProperty("key").GetString()!;
        var parent = await CreatePageAsync(admin, source, "Welcome");
        var child = await CreatePageAsync(admin, source, "Setup", Id(parent), Linked("see welcome", $"/spaces/{source}/pages/{Id(parent)}"));
        using (var file = new MultipartFormDataContent())
        {
            file.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("hello file")), "file", "notes.txt");
            using var uploaded = await admin.SendAsync(HttpMethod.Post, $"api/v1/content/{Id(child)}/attachments", file, withXsrf: true);
            Assert.True(uploaded.IsSuccessStatusCode, await uploaded.Content.ReadAsStringAsync());
        }

        // a member who is not a space administrator cannot export it
        using (var denied = await member.GetAsync($"api/v1/spaces/{source}/export"))
        {
            Assert.True(denied.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden);
        }

        using var export = await admin.GetAsync($"api/v1/spaces/{source}/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.StartsWith("raavi-space-", export.Content.Headers.ContentDisposition!.FileNameStar ?? export.Content.Headers.ContentDisposition.FileName, StringComparison.Ordinal);
        var bytes = await export.Content.ReadAsByteArrayAsync();
        using (var zip = new ZipArchive(new MemoryStream(bytes)))
        {
            Assert.Single(zip.Entries, e => e.FullName.EndsWith("/space.json", StringComparison.Ordinal)); // only this space
        }

        // the same key is taken; a file that is not an archive is refused
        using (var taken = await admin.SendAsync(HttpMethod.Post, "api/v1/spaces/import", Upload(bytes), withXsrf: true))
        {
            Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        }

        using (var junk = await admin.SendAsync(HttpMethod.Post, "api/v1/spaces/import", Upload(Encoding.UTF8.GetBytes("not a zip"), UniqueKey()), withXsrf: true))
        {
            Assert.Equal(HttpStatusCode.BadRequest, junk.StatusCode);
        }

        var target = UniqueKey();
        using var imported = await admin.SendAsync(HttpMethod.Post, "api/v1/spaces/import", Upload(bytes, target), withXsrf: true);
        var text = await imported.Content.ReadAsStringAsync();
        Assert.True(imported.IsSuccessStatusCode, text);
        var result = JsonDocument.Parse(text).RootElement;
        Assert.Equal(target, result.GetProperty("key").GetString());
        Assert.Equal(3, result.GetProperty("pages").GetInt32()); // the space's own home page + the two
        Assert.Equal(1, result.GetProperty("attachments").GetInt32());

        var roots = await admin.GetJsonAsync($"api/v1/spaces/{target}/tree");
        var welcome = roots.EnumerateArray().First(n => n.GetProperty("title").GetString() == "Welcome");
        var newWelcome = welcome.GetProperty("id").GetGuid();
        var kids = await admin.GetJsonAsync($"api/v1/spaces/{target}/tree?parentId={newWelcome}");
        var setup = kids.EnumerateArray().Single(n => n.GetProperty("title").GetString() == "Setup");
        var setupId = setup.GetProperty("id").GetGuid();
        var body = (await admin.GetJsonAsync($"api/v1/content/{setupId}")).GetRawText();
        Assert.Contains($"/spaces/{target}/pages/{newWelcome}", body, StringComparison.OrdinalIgnoreCase); // link rewritten to the new space and page
        Assert.DoesNotContain(Id(parent).ToString(), body, StringComparison.OrdinalIgnoreCase);
        var attachments = await admin.GetJsonAsync($"api/v1/content/{setupId}/attachments");
        Assert.Equal("notes.txt", attachments.EnumerateArray().Single().GetProperty("fileName").GetString());

        // the original is untouched
        Assert.Contains("Welcome", await TreeTitlesAsync(admin, source));
    }
}
