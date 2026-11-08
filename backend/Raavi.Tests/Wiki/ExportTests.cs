using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Raavi.Tests.Infrastructure;
using Raavi.Wiki.Application;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>Phase 12a: the workspace export — what goes in, what stays out, and who may ask.</summary>
public sealed class ExportUnitTests
{
    [Fact]
    public void File_names_are_safe_inside_an_archive()
    {
        Assert.Equal("report.pdf", ExportService.Safe("report.pdf"));
        foreach (var bad in new[] { "..\\..\\evil.exe", "a/b/c.txt", "C:\\x\\y.txt", "n\0ul.txt", "con*?.txt", "   ", "...", new string('x', 500) })
        {
            var safe = ExportService.Safe(bad);
            Assert.DoesNotContain('/', safe);
            Assert.DoesNotContain('\\', safe);
            Assert.DoesNotContain(':', safe);
            Assert.DoesNotContain("..", safe, StringComparison.Ordinal);
            Assert.True(safe.Length is > 0 and <= 120, safe);
        }
    }
}

[Collection(SqlTestGroup.Name)]
public sealed class ExportTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];

    private static object CommentBody(string text) => new
    {
        schema = "raavi.doc",
        schemaVersion = 2,
        doc = new { type = "doc", content = new object[] { new { type = "paragraph", content = new object[] { new { type = "text", text } } } } },
    };

    private static async Task<ZipArchive> ExportAsync(Browser b)
    {
        using var response = await b.GetAsync("api/v1/tenant/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.StartsWith("raavi-export-", response.Content.Headers.ContentDisposition!.FileNameStar ?? response.Content.Headers.ContentDisposition.FileName, StringComparison.Ordinal);
        return new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
    }

    private static JsonElement Read(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"missing entry {name}; have: {string.Join(", ", zip.Entries.Select(e => e.FullName))}");
        using var stream = entry.Open();
        return JsonDocument.Parse(stream).RootElement.Clone();
    }

    [Fact]
    public async Task The_export_contains_what_the_administrator_may_view_and_nothing_else()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, memberId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var (_, otherAdmin, _) = await _kit.Identity.TenantWithAdminAsync();
        var adminId = (await admin.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();

        // A space with a page (labels, comment, attachment), a restricted page, a task, an event, a database, a deck and a board.
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var open = Id(await CreatePageAsync(admin, key, "Runbook", body: Doc("restart the queue")));
        var secret = Id(await CreatePageAsync(admin, key, "Salaries", body: Doc("confidential numbers")));
        using (var label = await admin.PostAsync($"api/v1/content/{open}/labels", new { name = "Ops" }))
        {
            await ReadAsync(label);
        }

        using (var comment = await admin.PostAsync($"api/v1/content/{open}/comments", new { body = CommentBody("please review"), kind = "Page" }))
        {
            await ReadAsync(comment);
        }

        using (var form = new MultipartFormDataContent())
        {
            var file = new ByteArrayContent(Png);
            file.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
            form.Add(file, "file", "../../diagram.png");
            using var upload = await admin.SendAsync(HttpMethod.Post, $"api/v1/content/{open}/attachments", form, withXsrf: true);
            Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        }

        using (var task = await admin.PostAsync($"api/v1/spaces/{key}/tasks", new { title = "Rotate keys" }))
        {
            await ReadAsync(task);
        }

        using (var ev = await admin.PostAsync($"api/v1/spaces/{key}/calendar/events", new { title = "Release", allDay = true, startDate = "2026-12-01" }))
        {
            await ReadAsync(ev);
        }

        using (var db = await admin.PostAsync($"api/v1/spaces/{key}/databases", new { name = "Servers", columns = new object[] { new { name = "Host", type = "Text" } } }))
        {
            await ReadAsync(db);
        }

        using (var deck = await admin.PostAsync($"api/v1/spaces/{key}/slides", new { title = "Kickoff", direction = "Ltr", slides = new object[] { new { layout = "Title", title = "Hello", body = "", notes = "" } } }))
        {
            await ReadAsync(deck);
        }

        using (var board = await admin.PostAsync($"api/v1/spaces/{key}/whiteboards", new { title = "Ideas", elements = new object[] { new { id = "r1", type = "Rect", x = 0, y = 0, w = 10, h = 10 } } }))
        {
            await ReadAsync(board);
        }

        // Restrict one page to the admin only, add a private space the admin has no access to, and a draft.
        using (var restrict = await admin.PutAsync($"api/v1/content/{secret}/restrictions", new { view = new[] { new { subjectType = "User", subjectId = memberId } }, edit = Array.Empty<object>() }))
        {
            await ReadAsync(restrict); // the admin is not on the list: they keep recovery rights but the export must follow View
        }

        // A private space the administrator has no part in: created by the administrator, then handed over to the member alone.
        var hidden = (await CreateSpaceAsync(admin, visibility: "Private")).GetProperty("key").GetString()!;
        Id(await CreatePageAsync(admin, hidden, "Member only"));
        var allActions = new[] { "View", "ManagePermissions", "Edit", "Create", "Publish", "Move", "Archive", "Delete", "Comment", "ManageTemplates" };
        using (var handOver = await admin.PutAsync($"api/v1/spaces/{hidden}/permissions", new { grants = allActions.Select(a => (object)new { subjectType = "User", subjectId = (Guid?)memberId, action = a, effect = "Allow" }).ToList() }))
        {
            await ReadAsync(handOver);
        }
        var foreign = (await CreateSpaceAsync(otherAdmin)).GetProperty("key").GetString()!;
        Id(await CreatePageAsync(otherAdmin, foreign, "Other tenant page"));

        using var zip = await ExportAsync(admin);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        var manifest = Read(zip, "manifest.json");
        Assert.Equal((1, tenant.Slug), (manifest.GetProperty("formatVersion").GetInt32(), manifest.GetProperty("tenant").GetString()));
        Assert.Contains("Everything the exporting administrator may view", manifest.GetProperty("scope").GetString(), StringComparison.Ordinal);

        // Contents of the visible space.
        Assert.Contains($"spaces/{key}/space.json", names);
        var page = Read(zip, $"spaces/{key}/pages/{open:N}.json");
        Assert.Equal(("Runbook", 1), (page.GetProperty("title").GetString(), page.GetProperty("version").GetInt32()));
        Assert.Equal(["ops"], page.GetProperty("labels").EnumerateArray().Select(l => l.GetString()!));
        Assert.Contains("restart the queue", page.GetProperty("body").GetRawText(), StringComparison.Ordinal);
        Assert.Contains("please review", page.GetProperty("comments").GetRawText(), StringComparison.Ordinal);
        var attachment = page.GetProperty("attachments")[0];
        Assert.True(attachment.GetProperty("included").GetBoolean());
        var attachmentPath = attachment.GetProperty("path").GetString()!;
        Assert.DoesNotContain("..", attachmentPath, StringComparison.Ordinal); // a hostile file name cannot escape its folder
        using (var stream = zip.GetEntry(attachmentPath)!.Open())
        using (var copy = new MemoryStream())
        {
            await stream.CopyToAsync(copy);
            Assert.True(Png.AsSpan().SequenceEqual(copy.ToArray()));
        }

        Assert.Equal("Rotate keys", Read(zip, $"spaces/{key}/tasks.json")[0].GetProperty("title").GetString());
        Assert.Equal("Release", Read(zip, $"spaces/{key}/events.json")[0].GetProperty("title").GetString());
        Assert.Contains(names, n => n.StartsWith($"spaces/{key}/databases/", StringComparison.Ordinal));
        Assert.Contains(names, n => n.StartsWith($"spaces/{key}/slides/", StringComparison.Ordinal));
        Assert.Contains(names, n => n.StartsWith($"spaces/{key}/whiteboards/", StringComparison.Ordinal) && n.EndsWith(".svg", StringComparison.Ordinal));

        // What the administrator cannot view, and other tenants' data, are simply absent — also from the counts and the text of every file.
        Assert.DoesNotContain(names, n => n.Contains(secret.ToString("N"), StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.StartsWith($"spaces/{hidden}/", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.StartsWith($"spaces/{foreign}/", StringComparison.Ordinal));
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(entry.Open());
            var text = await reader.ReadToEndAsync();
            Assert.DoesNotContain("confidential numbers", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Member only", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Other tenant page", text, StringComparison.Ordinal);
        }

        var counts = manifest.GetProperty("counts");
        Assert.Equal(1, counts.GetProperty("pagesOmittedNoAccess").GetInt32());
        Assert.Equal((1, 1, 1, 1), (counts.GetProperty("attachments").GetInt32(), counts.GetProperty("comments").GetInt32(), counts.GetProperty("tasks").GetInt32(), counts.GetProperty("databases").GetInt32()));
        Assert.Contains(Read(zip, "users.json").EnumerateArray(), u => u.GetProperty("id").GetGuid() == adminId);
    }

    [Fact]
    public async Task Only_administrators_can_export_and_only_one_export_runs_at_a_time()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        using (var forbidden = await member.GetAsync("api/v1/tenant/export"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        for (var i = 0; i < 5; i++)
        {
            Id(await CreatePageAsync(admin, key, $"Page {i}", body: Doc(new string('x', 2000))));
        }

        // Two simultaneous requests: both end well (one may be refused while the other runs); never an error, and afterwards a new export works.
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            using var r = await admin.GetAsync("api/v1/tenant/export");
            if (r.StatusCode == HttpStatusCode.OK)
            {
                Assert.NotEmpty(await r.Content.ReadAsByteArrayAsync());
            }

            return r.StatusCode;
        }));
        Assert.All(results, s => Assert.True(s is HttpStatusCode.OK or HttpStatusCode.Conflict, s.ToString()));
        Assert.Contains(HttpStatusCode.OK, results);
        using var again = await ExportAsync(admin);
        Assert.Equal(5, Read(again, "manifest.json").GetProperty("counts").GetProperty("pages").GetInt32() - 1); // plus the space home page
    }
}
