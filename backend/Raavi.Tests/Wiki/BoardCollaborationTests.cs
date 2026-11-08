using System.Net;
using System.Text.Json;
using Raavi.Tests.Identity;
using Raavi.Tests.Infrastructure;
using Raavi.Wiki.Collaboration;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>Phase 11c: live whiteboards, API side — addresses, tickets, the signed state endpoints, validation and checks.</summary>
public sealed class BoardAddressTests
{
    [Fact]
    public void Document_names_distinguish_pages_from_boards_and_reject_everything_else()
    {
        var t = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var c = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Assert.Equal($"{t:D}:board:{c:D}:live", new DocumentAddress(t, c, DocumentKind.Board).Name);
        Assert.Equal(new DocumentAddress(t, c, DocumentKind.Board), DocumentAddress.Parse($"{t:D}:board:{c:D}:live"));
        Assert.Equal(new DocumentAddress(t, c), DocumentAddress.Parse($"{t:D}:page:{c:D}:draft"));
        foreach (var bad in new[] { $"{t:D}:board:{c:D}:draft", $"{t:D}:page:{c:D}:live", $"{t:D}:whiteboard:{c:D}:live", $"{t:D}:board:{c:D}", "x:board:y:live", "", null })
        {
            Assert.Null(DocumentAddress.Parse(bad));
        }
    }
}

[Collection(SqlTestGroup.Name)]
public sealed class BoardCollaborationTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    private async Task<HttpResponseMessage> InternalAsync(HttpMethod method, string path, object? body = null, string? key = SqlServerFixture.CollaborationServiceKey)
    {
        var bytes = body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(body, Browser.Json);
        using var client = fx.Api.ForHost("collab-backend.internal");
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation(ServiceSignature.Header, ServiceSignature.Create(key, method.Method, path, bytes, DateTimeOffset.UtcNow));
        }

        return await client.SendAsync(request);
    }

    private static readonly object[] Sample =
    [
        new { id = "r1", type = "Rect", x = 10, y = 20, w = 120, h = 60, fill = "#fde68a" },
        new { id = "t1", type = "Text", x = 0, y = 200, text = "سلام", rtl = true },
    ];

    private static async Task<Guid> BoardAsync(Browser b, string space, string title = "Live board")
    {
        using var r = await b.PostAsync($"api/v1/spaces/{space}/whiteboards", new { title, elements = Sample });
        return (await ReadAsync(r)).GetProperty("id").GetGuid();
    }

    private static string State(params byte[] bytes) => Convert.ToBase64String(bytes.Length == 0 ? [1, 2, 3, 4] : bytes);

    [Fact]
    public async Task Board_tickets_need_edit_rights_are_bound_to_the_board_and_the_internal_state_is_seeded_then_persisted()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (viewer, _, viewerId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var adminId = (await admin.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();
        var space = (await CreateSpaceAsync(admin, visibility: "Private")).GetProperty("key").GetString()!;
        var grants = new[] { "View", "ManagePermissions", "Edit", "Create", "Publish", "Move", "Archive", "Delete", "Comment", "ManageTemplates" }
            .Select(a => (object)new { subjectType = "User", subjectId = (Guid?)adminId, action = a, effect = "Allow" })
            .Append(new { subjectType = "User", subjectId = (Guid?)viewerId, action = "View", effect = "Allow" }).ToList();
        using (var put = await admin.PutAsync($"api/v1/spaces/{space}/permissions", new { grants }))
        {
            await ReadAsync(put);
        }

        var board = await BoardAsync(admin, space);
        var name = $"{tenant.Id:D}:board:{board:D}:live";

        // Editors get a ticket for exactly this board; readers and strangers do not.
        using (var ok = await admin.PostAsync("api/v1/collaboration/tickets", new { contentId = board, kind = "board" }))
        {
            var ticket = await ReadAsync(ok);
            Assert.Equal((name, "edit"), (ticket.GetProperty("documentName").GetString(), ticket.GetProperty("permission").GetString()));
        }

        using (var readOnly = await viewer.PostAsync("api/v1/collaboration/tickets", new { contentId = board, kind = "board" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, readOnly.StatusCode);
        }

        using (var unknown = await admin.PostAsync("api/v1/collaboration/tickets", new { contentId = Guid.NewGuid(), kind = "board" }))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        var (_, other, _) = await _kit.Identity.TenantWithAdminAsync();
        using (var crossTenant = await other.PostAsync("api/v1/collaboration/tickets", new { contentId = board, kind = "board" }))
        {
            Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
        }

        // First load: seeded from the stored elements, no Yjs state yet.
        using (var first = await InternalAsync(HttpMethod.Get, $"/internal/collaboration/documents/{name}/state"))
        {
            var state = await ReadAsync(first);
            Assert.Equal(JsonValueKind.Null, state.GetProperty("snapshot").ValueKind);
            Assert.Equal(["r1", "t1"], state.GetProperty("seed").GetProperty("elements").EnumerateArray().Select(e => e.GetProperty("id").GetString()!));
            Assert.Equal("Rect", state.GetProperty("seed").GetProperty("elements")[0].GetProperty("type").GetString());
        }

        // The server persists the converted elements together with the full Yjs state; the board row follows.
        var elements = new object[]
        {
            new { id = "r1", type = "Rect", x = 55, y = 20, w = 120, h = 60, rotation = 0, stroke = "#ef4444", fill = (string?)null, strokeWidth = 4, text = "", fontSize = 20, rtl = false, points = Array.Empty<double>() },
            new { id = "n9", type = "Sticky", x = 5, y = 5, w = 170, h = 130, rotation = 0, stroke = "#1f2937", fill = "#fde68a", strokeWidth = 2, text = "added live", fontSize = 20, rtl = false, points = Array.Empty<double>() },
        };
        using (var store = await InternalAsync(HttpMethod.Post, $"/internal/collaboration/documents/{name}/updates", new { update = State(9, 8, 7), json = new { elements }, contributors = new[] { adminId } }))
        {
            Assert.True(store.IsSuccessStatusCode);
        }

        var rest = await admin.GetJsonAsync($"api/v1/whiteboards/{board}");
        Assert.Equal(["r1", "n9"], rest.GetProperty("elements").EnumerateArray().Select(e => e.GetProperty("id").GetString()!));
        Assert.Equal((55d, "#ef4444"), (rest.GetProperty("elements")[0].GetProperty("x").GetDouble(), rest.GetProperty("elements")[0].GetProperty("stroke").GetString()));

        using (var again = await InternalAsync(HttpMethod.Get, $"/internal/collaboration/documents/{name}/state"))
        {
            var state = await ReadAsync(again);
            Assert.Equal(State(9, 8, 7), state.GetProperty("snapshot").GetString()); // now resumes from the stored Yjs state
            Assert.Equal(JsonValueKind.Null, state.GetProperty("seed").ValueKind);
        }

        using (var snapshot = await InternalAsync(HttpMethod.Post, $"/internal/collaboration/documents/{name}/snapshot", new { state = State(1), seq = 1 }))
        {
            Assert.Equal(HttpStatusCode.NoContent, snapshot.StatusCode); // nothing to compact for boards
        }

        // A REST save replaces the content and drops the live state, so the next live session starts from the new elements.
        var (_, etag) = await GetWithETagAsync(admin, $"api/v1/whiteboards/{board}");
        using (var save = await admin.SendAsync(HttpMethod.Put, $"api/v1/whiteboards/{board}", new { title = "Live board", elements = new object[] { Sample[0] } }, withXsrf: true, ifMatch: etag))
        {
            Assert.True(save.IsSuccessStatusCode);
        }

        using (var reseed = await InternalAsync(HttpMethod.Get, $"/internal/collaboration/documents/{name}/state"))
        {
            var state = await ReadAsync(reseed);
            Assert.Equal(JsonValueKind.Null, state.GetProperty("snapshot").ValueKind);
            Assert.Equal(1, state.GetProperty("seed").GetProperty("elements").GetArrayLength());
        }

        // Renaming keeps elements and live state alone.
        using (var persist = await InternalAsync(HttpMethod.Post, $"/internal/collaboration/documents/{name}/updates", new { update = State(5), json = new { elements } }))
        {
            Assert.True(persist.IsSuccessStatusCode);
        }

        using (var rename = await admin.PutAsync($"api/v1/whiteboards/{board}/title", new { title = "Renamed live" }))
        {
            Assert.Equal("Renamed live", (await ReadAsync(rename)).GetProperty("title").GetString());
        }

        using (var after = await InternalAsync(HttpMethod.Get, $"/internal/collaboration/documents/{name}/state"))
        {
            Assert.Equal(State(5), (await ReadAsync(after)).GetProperty("snapshot").GetString());
        }

        Assert.Equal(2, (await admin.GetJsonAsync($"api/v1/whiteboards/{board}")).GetProperty("elements").GetArrayLength());
        using (var viewerRename = await viewer.PutAsync($"api/v1/whiteboards/{board}/title", new { title = "nope" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, viewerRename.StatusCode);
        }
    }

    [Fact]
    public async Task Invalid_live_content_is_refused_like_a_rest_save_and_the_signature_and_tenant_rules_hold()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (other, _, _) = await _kit.Identity.TenantWithAdminAsync();
        var space = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var board = await BoardAsync(admin, space);
        var name = $"{tenant.Id:D}:board:{board:D}:live";
        var path = $"/internal/collaboration/documents/{name}/updates";

        object Element(string id, string type, double w = 50, string stroke = "#112233", string text = "") =>
            new { id, type, x = 0, y = 0, w, h = 40, rotation = 0, stroke, fill = (string?)null, strokeWidth = 2, text, fontSize = 20, rtl = false, points = Array.Empty<double>() };

        foreach (var (body, why) in new (object, string)[]
        {
            (new { update = State(), json = new { elements = new[] { Element("a", "Rect", stroke: "javascript:alert(1)") } } }, "bad colour"),
            (new { update = State(), json = new { elements = new[] { Element("a", "Rect", w: 0) } } }, "zero width"),
            (new { update = State(), json = new { elements = new[] { Element("a", "Hexagon") } } }, "unknown type"),
            (new { update = State(), json = new { elements = new[] { Element("a", "Rect"), Element("a", "Rect") } } }, "duplicate id"),
            (new { update = State(), json = new { elements = new[] { Element("a", "Text", text: "  ") } } }, "blank text"),
            (new { update = "", json = new { elements = Array.Empty<object>() } }, "no state"),
            (new { update = State(), json = new { nothing = 1 } }, "no element list"),
            (new { update = "%%%not-base64%%%", json = new { elements = Array.Empty<object>() } }, "bad base64"),
        })
        {
            using var r = await InternalAsync(HttpMethod.Post, path, body);
            Assert.True(r.StatusCode == HttpStatusCode.BadRequest, $"{why}: {r.StatusCode}");
        }

        // Nothing invalid was stored: the board still has its two original elements and no live state.
        Assert.Equal(2, (await admin.GetJsonAsync($"api/v1/whiteboards/{board}")).GetProperty("elements").GetArrayLength());
        using (var state = await InternalAsync(HttpMethod.Get, $"/internal/collaboration/documents/{name}/state"))
        {
            Assert.Equal(JsonValueKind.Null, (await ReadAsync(state)).GetProperty("snapshot").ValueKind);
        }

        using (var unsigned = await InternalAsync(HttpMethod.Post, path, new { update = State(), json = new { elements = Array.Empty<object>() } }, key: null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        }

        using (var crossTenant = await InternalAsync(HttpMethod.Get, $"/internal/collaboration/documents/{other.Id:D}:board:{board:D}:live/state"))
        {
            Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode); // the same board id under another tenant does not exist
        }

        using (var missing = await InternalAsync(HttpMethod.Get, $"/internal/collaboration/documents/{tenant.Id:D}:board:{Guid.NewGuid():D}:live/state"))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
    }

    [Fact]
    public async Task Live_connections_are_rechecked_against_sessions_and_current_rights()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (editor, _, editorId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var adminId = (await admin.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();
        var space = (await CreateSpaceAsync(admin, visibility: "Private")).GetProperty("key").GetString()!;
        async Task GrantAsync(params string[] editorActions)
        {
            var grants = new[] { "View", "ManagePermissions", "Edit", "Create", "Publish", "Move", "Archive", "Delete", "Comment", "ManageTemplates" }
                .Select(a => (object)new { subjectType = "User", subjectId = (Guid?)adminId, action = a, effect = "Allow" })
                .Concat(editorActions.Select(a => (object)new { subjectType = "User", subjectId = (Guid?)editorId, action = a, effect = "Allow" })).ToList();
            using var put = await admin.PutAsync($"api/v1/spaces/{space}/permissions", new { grants });
            await ReadAsync(put);
        }

        await GrantAsync("View", "Edit");
        var board = await BoardAsync(admin, space);
        var name = $"{tenant.Id:D}:board:{board:D}:live";

        using var ticketResponse = await editor.PostAsync("api/v1/collaboration/tickets", new { contentId = board, kind = "board" });
        var ticket = await ReadAsync(ticketResponse);
        var sessionId = await SessionOfAsync(ticket.GetProperty("ticket").GetString()!);

        async Task<string> CheckAsync(Guid user, Guid session)
        {
            using var r = await InternalAsync(HttpMethod.Post, "/internal/collaboration/check", new { documentName = name, userId = user, sessionId = session });
            return (await ReadAsync(r)).GetProperty("permission").GetString()!;
        }

        Assert.Equal("edit", await CheckAsync(editorId, sessionId));
        Assert.Equal("none", await CheckAsync(editorId, Guid.NewGuid())); // not a live session of this user

        await GrantAsync("View"); // edit right withdrawn
        Assert.Equal("view", await CheckAsync(editorId, sessionId));
        await GrantAsync(); // access withdrawn
        Assert.Equal("none", await CheckAsync(editorId, sessionId));
    }

    private static Task<Guid> SessionOfAsync(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
        return Task.FromResult(Guid.Parse(doc.RootElement.GetProperty("sid").GetString()!));
    }
}
