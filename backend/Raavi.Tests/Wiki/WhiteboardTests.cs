using System.Net;
using System.Xml.Linq;
using Raavi.Domain.Common;
using Raavi.Tests.Identity;
using Raavi.Tests.Infrastructure;
using Raavi.Wiki.Domain;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>Phase 9e: element validation of whiteboards — no SQL needed.</summary>
public sealed class WhiteboardDomainTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static BoardElementInput Rect(string id = "r1", double w = 100, double h = 50) => new(id, BoardShape.Rect, 10, 20, w, h, null, null, null, null, null, null, null, null);

    private static Whiteboard Make(params BoardElementInput[] e) => Whiteboard.Create(Guid.NewGuid(), "Plan", e, Guid.NewGuid(), Now);

    private static string Code(Action a) => Assert.Throws<DomainException>(a).Code;

    [Fact]
    public void Elements_get_defaults_keep_order_and_drop_fields_their_type_does_not_use()
    {
        var board = Make(
            Rect(),
            new("t1", BoardShape.Text, 0, 0, 0, 0, 725, "#ABCDEF", null, null, "line1\r\nline2", 24, true, [1, 2]),
            new("a1", BoardShape.Arrow, 5, 5, 99, 99, null, null, "#112233", 4, "ignored", null, null, [0, 0, 50, 50]),
            new("p1", BoardShape.Pen, 0, 0, null, null, null, null, null, null, null, null, null, [0, 0, 1, 1, 2, 3]));

        Assert.Equal(["r1", "t1", "a1", "p1"], board.Elements.Select(e => e.Id));
        var rect = board.Elements[0];
        Assert.Equal((BoardShape.Rect, 2, 20, "#1f2937", (string?)null, ""), (rect.Type, rect.StrokeWidth, rect.FontSize, rect.Stroke, rect.Fill, rect.Text));
        var text = board.Elements[1];
        Assert.Equal(("#abcdef", "line1\nline2", 5d, true, 0), (text.Stroke, text.Text, text.Rotation, text.Rtl, text.Points.Length)); // colour lower-cased, rotation wrapped, points dropped
        var arrow = board.Elements[2];
        Assert.Equal((0d, 0d, "", false), (arrow.W, arrow.H, arrow.Text, arrow.Rtl));
        Assert.Equal("#112233", arrow.Fill);

        board.Update("Plan 2", [Rect("r2")], Now);
        Assert.Equal((2, 1), (board.Revision, board.Elements.Count));
    }

    [Fact]
    public void Diamonds_triangles_and_lines_follow_the_rules_of_the_other_shapes()
    {
        var board = Make(
            new BoardElementInput("d1", BoardShape.Diamond, 0, 0, 100, 80, null, null, null, null, "ignored", null, null, [1, 2]),
            new BoardElementInput("l1", BoardShape.Line, 0, 0, 9, 9, null, null, null, null, null, null, null, [0, 0, 50, 50]));
        Assert.Equal((100d, 80d, 0), (board.Elements[0].W, board.Elements[0].H, board.Elements[0].Points.Length)); // a diamond has a size, no points
        Assert.Equal((0d, 0d, 4), (board.Elements[1].W, board.Elements[1].H, board.Elements[1].Points.Length)); // a line has two points, no size
        Assert.Equal("board.element_invalid", Code(() => Make(new BoardElementInput("d", BoardShape.Diamond, 0, 0, 0, 10, null, null, null, null, null, null, null, null))));
        Assert.Equal("board.element_invalid", Code(() => Make(new BoardElementInput("t", BoardShape.Triangle, 0, 0, 10, 0, null, null, null, null, null, null, null, null))));
        Assert.Equal("board.element_invalid", Code(() => Make(new BoardElementInput("l", BoardShape.Line, 0, 0, null, null, null, null, null, null, null, null, null, [0, 0, 1, 1, 2, 2]))));
        Assert.Equal("board.element_invalid", Code(() => Make(new BoardElementInput("l", BoardShape.Line, 0, 0, null, null, null, null, null, null, null, null, null, null))));
    }

    [Fact]
    public void Bad_elements_are_rejected_with_stable_codes()
    {
        Assert.Equal("board.title_invalid", Code(() => Whiteboard.Create(Guid.NewGuid(), " ", [], Guid.NewGuid(), Now)));
        Assert.Equal("board.too_many", Code(() => Whiteboard.Create(Guid.NewGuid(), "B", Enumerable.Range(0, 2001).Select(i => Rect($"e{i}")).ToList(), Guid.NewGuid(), Now)));
        Assert.Equal("board.element_invalid", Code(() => Make(Rect("a"), Rect("a"))));
        Assert.Equal("board.element_invalid", Code(() => Make(Rect("bad id"))));
        Assert.Equal("board.element_invalid", Code(() => Make(Rect("a", w: 0))));
        Assert.Equal("board.element_invalid", Code(() => Make(Rect("a", w: -1))));
        Assert.Equal("board.element_invalid", Code(() => Make(Rect("a", w: double.PositiveInfinity))));
        Assert.Equal("board.element_invalid", Code(() => Make(Rect("a", w: 1e9))));
        Assert.Equal("board.element_invalid", Code(() => Make(Rect() with { Stroke = "red" })));
        Assert.Equal("board.element_invalid", Code(() => Make(Rect() with { Fill = "url(#x)" })));
        Assert.Equal("board.element_invalid", Code(() => Make(Rect() with { StrokeWidth = 0 })));
        Assert.Equal("board.element_invalid", Code(() => Make(Rect() with { FontSize = 500 })));
        Assert.Equal("board.element_invalid", Code(() => Make(Rect() with { Type = (BoardShape)42 })));
        Assert.Equal("board.element_invalid", Code(() => Make(new BoardElementInput("t", BoardShape.Text, 0, 0, 0, 0, null, null, null, null, "  ", null, null, null))));
        Assert.Equal("board.element_invalid", Code(() => Make(new BoardElementInput("t", BoardShape.Sticky, 0, 0, 100, 100, null, null, null, null, new string('x', 2001), null, null, null))));
        Assert.Equal("board.element_invalid", Code(() => Make(new BoardElementInput("t", BoardShape.Sticky, 0, 0, 100, 100, null, null, null, null, "bell\a", null, null, null))));
        Assert.Equal("board.element_invalid", Code(() => Make(new BoardElementInput("a", BoardShape.Arrow, 0, 0, 0, 0, null, null, null, null, null, null, null, [1, 2, 3]))));
        Assert.Equal("board.element_invalid", Code(() => Make(new BoardElementInput("a", BoardShape.Arrow, 0, 0, 0, 0, null, null, null, null, null, null, null, [0, 0, double.NaN, 1]))));
        Assert.Equal("board.element_invalid", Code(() => Make(new BoardElementInput("p", BoardShape.Pen, 0, 0, 0, 0, null, null, null, null, null, null, null, new double[4002]))));
    }
}

[Collection(SqlTestGroup.Name)]
public sealed class WhiteboardTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    private static async Task<System.Text.Json.JsonElement> CreateAsync(Browser b, string key, object body)
    {
        using var r = await b.PostAsync($"api/v1/spaces/{key}/whiteboards", body);
        return await ReadAsync(r);
    }

    private static readonly object[] Sample =
    [
        new { id = "r1", type = "Rect", x = 10, y = 20, w = 120, h = 60, fill = "#fde68a" },
        new { id = "s1", type = "Sticky", x = 200, y = 20, w = 160, h = 120, text = "Ship <it> & \"test\"", rtl = false },
        new { id = "t1", type = "Text", x = 0, y = 200, w = 0, h = 0, text = "سلام دنیا", rtl = true, fontSize = 28 },
        new { id = "a1", type = "Arrow", x = 50, y = 300, points = new[] { 0, 0, 120, 40 }, stroke = "#ef4444" },
        new { id = "p1", type = "Pen", x = 0, y = 0, points = new[] { 0, 0, 5, 5, 10, 0 } },
    ];

    [Fact]
    public async Task Boards_are_saved_whole_with_etags_and_validated()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var created = await CreateAsync(admin, key, new { title = "Retro", elements = Sample });
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(5, created.GetProperty("elementCount").GetInt32());
        Assert.Equal(0, (await admin.GetJsonAsync($"api/v1/spaces/{key}/whiteboards"))[0].GetProperty("elements").GetArrayLength()); // list stays light

        var (current, etag) = await GetWithETagAsync(admin, $"api/v1/whiteboards/{id}");
        Assert.Equal(["r1", "s1", "t1", "a1", "p1"], current.GetProperty("elements").EnumerateArray().Select(e => e.GetProperty("id").GetString()));
        var next = new { title = "Retro v2", elements = new object[] { Sample[4], Sample[0] } }; // reordered, three removed

        using (var missing = await admin.SendAsync(HttpMethod.Put, $"api/v1/whiteboards/{id}", next, withXsrf: true))
        {
            await Browser.AssertStatusAsync(missing, HttpStatusCode.PreconditionRequired);
        }

        using (var ok = await admin.SendAsync(HttpMethod.Put, $"api/v1/whiteboards/{id}", next, withXsrf: true, ifMatch: etag))
        {
            var updated = await ReadAsync(ok);
            Assert.Equal(["p1", "r1"], updated.GetProperty("elements").EnumerateArray().Select(e => e.GetProperty("id").GetString()));
        }

        using (var stale = await admin.SendAsync(HttpMethod.Put, $"api/v1/whiteboards/{id}", next, withXsrf: true, ifMatch: etag))
        {
            await Browser.AssertStatusAsync(stale, HttpStatusCode.PreconditionFailed);
        }

        foreach (var (body, code) in new (object, string)[]
        {
            (new { title = "", elements = Array.Empty<object>() }, "board.title_invalid"),
            (new { title = "T", elements = new object[] { new { id = "x", type = "Hexagon" } } }, "board.element_invalid"),
            (new { title = "T", elements = new object[] { new { id = "x", type = "Rect", w = 10, h = 10, stroke = "javascript:1" } } }, "board.element_invalid"),
            (new { title = "T", elements = new object[] { new { id = "x", type = "Rect", w = 0, h = 10 } } }, "board.element_invalid"),
        })
        {
            using var bad = await admin.PostAsync($"api/v1/spaces/{key}/whiteboards", body);
            await Browser.AssertStatusAsync(bad, HttpStatusCode.BadRequest, code);
        }

        using (var del = await admin.DeleteAsync($"api/v1/whiteboards/{id}"))
        {
            Assert.True(del.IsSuccessStatusCode);
        }

        using var gone = await admin.GetAsync($"api/v1/whiteboards/{id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Svg_export_is_well_formed_escapes_text_and_contains_every_element()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var id = (await CreateAsync(admin, key, new { title = "A & B <board>", elements = Sample })).GetProperty("id").GetGuid();

        using var response = await admin.GetAsync($"api/v1/whiteboards/{id}/export");
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType!.MediaType);
        var svg = await response.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(svg); // throws if not well-formed
        XNamespace ns = "http://www.w3.org/2000/svg";
        Assert.Equal("A & B <board>", doc.Root!.Element(ns + "title")!.Value);
        Assert.Equal(5, doc.Root.Elements(ns + "g").Count());
        Assert.Equal("Ship <it> & \"test\"", string.Join(" ", doc.Descendants(ns + "g").ElementAt(1).Descendants(ns + "tspan").Select(t => t.Value))); // sticky wraps into several lines
        Assert.DoesNotContain("<it>", svg, StringComparison.Ordinal);
        var rtl = doc.Descendants(ns + "text").Single(t => t.Value == "سلام دنیا");
        Assert.Equal(("rtl", "start"), ((string?)rtl.Attribute("direction"), (string?)rtl.Attribute("text-anchor")));
        Assert.Single(doc.Descendants(ns + "polygon")); // arrow head
        Assert.Single(doc.Descendants(ns + "polyline")); // pen
        Assert.DoesNotContain("script", svg, StringComparison.OrdinalIgnoreCase);

        // An empty board still exports a valid picture.
        var empty = (await CreateAsync(admin, key, new { title = "Empty" })).GetProperty("id").GetGuid();
        using var e = await admin.GetAsync($"api/v1/whiteboards/{empty}/export");
        Assert.NotNull(XDocument.Parse(await e.Content.ReadAsStringAsync()).Root);
    }

    [Fact]
    public async Task Diamonds_triangles_and_lines_are_saved_and_exported()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var elements = new object[]
        {
            new { id = "d1", type = "Diamond", x = 10, y = 10, w = 100, h = 80, fill = "#fde68a" },
            new { id = "t1", type = "Triangle", x = 200, y = 10, w = 90, h = 70 },
            new { id = "l1", type = "Line", x = 5, y = 5, points = new[] { 0, 0, 120, 40 } },
        };
        var id = (await CreateAsync(admin, key, new { title = "Shapes", elements })).GetProperty("id").GetGuid();
        using var response = await admin.GetAsync($"api/v1/whiteboards/{id}/export");
        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());
        XNamespace ns = "http://www.w3.org/2000/svg";
        Assert.Equal(2, doc.Descendants(ns + "polygon").Count()); // diamond + triangle (a line has no arrow head)
        Assert.Single(doc.Descendants(ns + "line"));
        var saved = await admin.GetJsonAsync($"api/v1/whiteboards/{id}");
        Assert.Equal(["Diamond", "Triangle", "Line"], saved.GetProperty("elements").EnumerateArray().Select(e => e.GetProperty("type").GetString()));
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

        var id = (await CreateAsync(admin, key, new { title = "Secret", elements = Sample })).GetProperty("id").GetGuid();

        Assert.False((await viewer.GetJsonAsync($"api/v1/whiteboards/{id}")).GetProperty("canEdit").GetBoolean());
        using (var export = await viewer.GetAsync($"api/v1/whiteboards/{id}/export"))
        {
            Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        }

        using (var create = await viewer.PostAsync($"api/v1/spaces/{key}/whiteboards", new { title = "nope" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        }

        using (var put = await viewer.SendAsync(HttpMethod.Put, $"api/v1/whiteboards/{id}", new { title = "hack" }, withXsrf: true, ifMatch: "\"r1\""))
        {
            Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        }

        using (var del = await viewer.DeleteAsync($"api/v1/whiteboards/{id}"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, del.StatusCode);
        }

        foreach (var who in new[] { outsider, otherAdmin })
        {
            foreach (var url in new[] { $"api/v1/whiteboards/{id}", $"api/v1/whiteboards/{id}/export" })
            {
                using var r = await who.GetAsync(url);
                Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            }
        }

        using var hidden = await outsider.GetAsync($"api/v1/spaces/{key}/whiteboards");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }
}
