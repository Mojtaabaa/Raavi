using System.Net;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Raavi.Domain.Common;
using Raavi.Tests.Identity;
using Raavi.Tests.Infrastructure;
using Raavi.Wiki.Domain;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>Phase 9d: deck and slide validation — no SQL needed.</summary>
public sealed class SlideDomainTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static SlideInput S(string title, string body = "", string notes = "", SlideLayout layout = SlideLayout.Bullets, string? id = null) => new(id, layout, title, body, notes);

    private static string Code(Action a) => Assert.Throws<DomainException>(a).Code;

    [Fact]
    public void Slides_get_stable_ids_keep_order_and_normalise_text()
    {
        var deck = SlideDeck.Create(Guid.NewGuid(), " Roadmap ", TextDirection.Ltr, [S("One", "a\r\nb\r\n\r\n"), S("Two")], Guid.NewGuid(), Now);
        Assert.Equal("Roadmap", deck.Title);
        Assert.Equal(["One", "Two"], deck.Slides.Select(s => s.Title));
        Assert.Equal("a\nb", deck.Slides[0].Body);
        Assert.All(deck.Slides, s => Assert.StartsWith("s", s.Id, StringComparison.Ordinal));

        // Reordering keeps ids; a forged id is refused; the revision grows.
        var (first, second) = (deck.Slides[0], deck.Slides[1]);
        deck.Update("Roadmap", TextDirection.Rtl, [S("Two", id: second.Id), S("One", id: first.Id), S("New")], Now);
        Assert.Equal((2, TextDirection.Rtl), (deck.Revision, deck.Direction));
        Assert.Equal([second.Id, first.Id], deck.Slides.Take(2).Select(s => s.Id));
        Assert.Equal("slides.slide_invalid", Code(() => deck.Update("R", TextDirection.Ltr, [S("x", id: "sforged00")], Now)));
        Assert.Equal("slides.slide_invalid", Code(() => deck.Update("R", TextDirection.Ltr, [S("a", id: first.Id), S("b", id: first.Id)], Now)));
    }

    [Fact]
    public void Limits_and_bad_input_are_rejected_with_stable_codes()
    {
        Assert.Equal("slides.title_invalid", Code(() => SlideDeck.Create(Guid.NewGuid(), "  ", TextDirection.Ltr, [], Guid.NewGuid(), Now)));
        Assert.Equal("slides.title_invalid", Code(() => SlideDeck.Create(Guid.NewGuid(), new string('x', 201), TextDirection.Ltr, [], Guid.NewGuid(), Now)));
        Assert.Equal("slides.direction_invalid", Code(() => SlideDeck.Create(Guid.NewGuid(), "D", (TextDirection)7, [], Guid.NewGuid(), Now)));
        Assert.Equal("slides.too_many", Code(() => SlideDeck.Create(Guid.NewGuid(), "D", TextDirection.Ltr, Enumerable.Range(0, 101).Select(i => S($"s{i}")).ToList(), Guid.NewGuid(), Now)));
        Assert.Equal("slides.slide_invalid", Code(() => SlideDeck.Create(Guid.NewGuid(), "D", TextDirection.Ltr, [S("t", layout: (SlideLayout)9)], Guid.NewGuid(), Now)));
        Assert.Equal("slides.slide_invalid", Code(() => SlideDeck.Create(Guid.NewGuid(), "D", TextDirection.Ltr, [S(new string('t', 201))], Guid.NewGuid(), Now)));
        Assert.Equal("slides.slide_invalid", Code(() => SlideDeck.Create(Guid.NewGuid(), "D", TextDirection.Ltr, [S("t", string.Join('\n', Enumerable.Repeat("x", 31)))], Guid.NewGuid(), Now)));
        Assert.Equal("slides.slide_invalid", Code(() => SlideDeck.Create(Guid.NewGuid(), "D", TextDirection.Ltr, [S("t", new string('x', 4001))], Guid.NewGuid(), Now)));
        Assert.Equal("slides.slide_invalid", Code(() => SlideDeck.Create(Guid.NewGuid(), "D", TextDirection.Ltr, [S("t", "bell\a")], Guid.NewGuid(), Now)));
        Assert.Equal("slides.slide_invalid", Code(() => SlideDeck.Create(Guid.NewGuid(), "D", TextDirection.Ltr, [S("t", notes: new string('n', 2001))], Guid.NewGuid(), Now)));
    }
}

[Collection(SqlTestGroup.Name)]
public sealed class SlideTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    private static async Task<System.Text.Json.JsonElement> CreateAsync(Browser b, string key, object body)
    {
        using var r = await b.PostAsync($"api/v1/spaces/{key}/slides", body);
        return await ReadAsync(r);
    }

    [Fact]
    public async Task Decks_are_created_edited_with_etags_reordered_and_deleted()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var created = (await CreateAsync(admin, key, new
        {
            title = "Quarterly review",
            direction = "Ltr",
            slides = new object[]
            {
                new { layout = "Title", title = "Q3", body = "Engineering" },
                new { layout = "Bullets", title = "Wins", body = "Shipped tasks\nShipped calendar", notes = "Mention databases" },
            },
        }));
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(2, created.GetProperty("slideCount").GetInt32());
        Assert.True(created.GetProperty("canEdit").GetBoolean());

        Assert.Equal(1, (await admin.GetJsonAsync($"api/v1/spaces/{key}/slides")).GetArrayLength());
        Assert.Equal(0, (await admin.GetJsonAsync($"api/v1/spaces/{key}/slides"))[0].GetProperty("slides").GetArrayLength()); // list stays light

        var (current, etag) = await GetWithETagAsync(admin, $"api/v1/slides/{id}");
        var slides = current.GetProperty("slides");
        var (firstId, secondId) = (slides[0].GetProperty("id").GetString()!, slides[1].GetProperty("id").GetString()!);
        var reordered = new
        {
            title = "Quarterly review",
            direction = "Rtl",
            slides = new object[]
            {
                new { id = secondId, layout = "Bullets", title = "Wins", body = "Shipped tasks", notes = "x" },
                new { id = firstId, layout = "Title", title = "Q3", body = "Engineering" },
            },
        };

        using (var missing = await admin.SendAsync(HttpMethod.Put, $"api/v1/slides/{id}", reordered, withXsrf: true))
        {
            await Browser.AssertStatusAsync(missing, HttpStatusCode.PreconditionRequired);
        }

        using (var ok = await admin.SendAsync(HttpMethod.Put, $"api/v1/slides/{id}", reordered, withXsrf: true, ifMatch: etag))
        {
            var updated = await ReadAsync(ok);
            Assert.Equal([secondId, firstId], updated.GetProperty("slides").EnumerateArray().Select(s => s.GetProperty("id").GetString()));
            Assert.Equal("Rtl", updated.GetProperty("direction").GetString());
        }

        using (var stale = await admin.SendAsync(HttpMethod.Put, $"api/v1/slides/{id}", reordered, withXsrf: true, ifMatch: etag))
        {
            await Browser.AssertStatusAsync(stale, HttpStatusCode.PreconditionFailed);
        }

        foreach (var (body, code) in new (object, string)[]
        {
            (new { title = "", slides = Array.Empty<object>() }, "slides.title_invalid"),
            (new { title = "T", direction = "Up", slides = Array.Empty<object>() }, "slides.direction_invalid"),
            (new { title = "T", slides = new object[] { new { layout = "Grid", title = "x" } } }, "slides.slide_invalid"),
            (new { title = "T", slides = new object[] { new { id = "sforged00", layout = "Text", title = "x" } } }, "slides.slide_invalid"),
        })
        {
            using var bad = await admin.PostAsync($"api/v1/spaces/{key}/slides", body);
            await Browser.AssertStatusAsync(bad, HttpStatusCode.BadRequest, code);
        }

        using (var del = await admin.DeleteAsync($"api/v1/slides/{id}"))
        {
            Assert.True(del.IsSuccessStatusCode);
        }

        using (var gone = await admin.GetAsync($"api/v1/slides/{id}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }
    }

    [Fact]
    public async Task Export_is_a_valid_pptx_with_slides_notes_and_rtl()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var deck = (await CreateAsync(admin, key, new
        {
            title = "گزارش فصلی",
            direction = "Rtl",
            slides = new object[]
            {
                new { layout = "Title", title = "گزارش", body = "تیم فنی" },
                new { layout = "Bullets", title = "دستاوردها", body = "کار\n\nتقویم <b>&</b>", notes = "یادداشت سخنران" },
                new { layout = "Text", title = "Plain", body = "Just text" },
            },
        }));

        using var response = await admin.GetAsync($"api/v1/slides/{deck.GetProperty("id").GetGuid()}/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.presentationml.presentation", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("attachment", response.Content.Headers.ContentDisposition!.ToString(), StringComparison.Ordinal);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal((byte)'P', bytes[0]); // zip container

        using var stream = new MemoryStream(bytes);
        using var doc = PresentationDocument.Open(stream, false);
        var errors = new OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Office2019).Validate(doc).Select(e => $"{e.Path?.XPath}: {e.Description}").ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));

        var parts = doc.PresentationPart!.SlideParts.ToList();
        Assert.Equal(3, parts.Count);
        var xml = string.Join("\n", parts.Select(p => p.Slide!.OuterXml));
        Assert.Contains("گزارش", xml, StringComparison.Ordinal);
        Assert.Contains("rtl=\"1\"", xml, StringComparison.Ordinal);
        Assert.Contains("fa-IR", xml, StringComparison.Ordinal);
        Assert.Contains("تقویم &lt;b&gt;&amp;&lt;/b&gt;", xml, StringComparison.Ordinal); // text is data, never markup
        Assert.Equal(1, parts.Count(p => p.NotesSlidePart is not null));
        Assert.Contains("یادداشت سخنران", parts[1].NotesSlidePart!.NotesSlide!.OuterXml, StringComparison.Ordinal);
        Assert.Equal("گزارش فصلی", doc.PackageProperties.Title);
    }

    [Fact]
    public async Task Permissions_follow_the_space_and_other_tenants_see_nothing()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (viewer, _, viewerId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var (outsider, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var (_, otherAdmin, _) = await _kit.Identity.TenantWithAdminAsync();
        var adminId = (await admin.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();

        var key = (await CreateSpaceAsync(admin, visibility: "Private")).GetProperty("key").GetString()!;
        var grants = new[] { "View", "ManagePermissions", "Edit", "Create", "Publish", "Move", "Archive", "Delete", "Comment", "ManageTemplates" }
            .Select(a => (object)new { subjectType = "User", subjectId = (Guid?)adminId, action = a, effect = "Allow" })
            .Append(new { subjectType = "User", subjectId = (Guid?)viewerId, action = "View", effect = "Allow" }).ToList();
        using (var put = await admin.PutAsync($"api/v1/spaces/{key}/permissions", new { grants }))
        {
            await ReadAsync(put);
        }

        var id = (await CreateAsync(admin, key, new { title = "Secret", slides = new object[] { new { layout = "Text", title = "x", body = "y" } } })).GetProperty("id").GetGuid();

        Assert.False((await viewer.GetJsonAsync($"api/v1/slides/{id}")).GetProperty("canEdit").GetBoolean());
        using (var export = await viewer.GetAsync($"api/v1/slides/{id}/export"))
        {
            Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        }

        using (var create = await viewer.PostAsync($"api/v1/spaces/{key}/slides", new { title = "nope" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        }

        using (var put = await viewer.SendAsync(HttpMethod.Put, $"api/v1/slides/{id}", new { title = "hack" }, withXsrf: true, ifMatch: "\"r1\""))
        {
            Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        }

        using (var del = await viewer.DeleteAsync($"api/v1/slides/{id}"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, del.StatusCode);
        }

        foreach (var who in new[] { outsider, otherAdmin })
        {
            foreach (var url in new[] { $"api/v1/slides/{id}", $"api/v1/slides/{id}/export" })
            {
                using var r = await who.GetAsync(url);
                Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            }
        }

        using (var hidden = await outsider.GetAsync($"api/v1/spaces/{key}/slides"))
        {
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        }
    }
}

