using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Raavi.Tests.Infrastructure;
using Raavi.Wiki.Collaboration;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>Phase 6, API side: tickets, the signed internal persistence endpoints, draft synchronisation, revocation checks.</summary>
[Collection(SqlTestGroup.Name)]
public sealed class CollaborationTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    private static object Paragraphs(params string[] text) => new
    {
        type = "doc",
        content = text.Select(t => new { type = "paragraph", attrs = new { textAlign = (string?)null, dir = (string?)null }, content = new[] { new { type = "text", text = t } } }).ToArray(),
    };

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

    [Fact]
    public void Service_signature_matches_the_collaboration_server()
    {
        // Same vector as collaboration/tests/unit.test.ts.
        var header = ServiceSignature.Create("test-service-key-0123456789abcdef", "POST", "/internal/flush", "{}"u8, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        Assert.Equal("t=1700000000,s=be8cabd7a939efc74d5ccb9670e92610e961d8dc62429476453b37992af20911", header);
        Assert.True(ServiceSignature.Verify("test-service-key-0123456789abcdef", header, "POST", "/internal/flush", "{}"u8, DateTimeOffset.FromUnixTimeSeconds(1_700_000_030)));
        Assert.False(ServiceSignature.Verify("test-service-key-0123456789abcdef", header, "POST", "/internal/flush", "{}"u8, DateTimeOffset.FromUnixTimeSeconds(1_700_000_100)));
        Assert.Equal("11111111-1111-1111-1111-111111111111:page:22222222-2222-2222-2222-222222222222:draft",
            new DocumentAddress(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222")).Name);
        Assert.Null(DocumentAddress.Parse("x:page:y:draft"));
    }

    [Fact]
    public async Task Tickets_are_short_lived_signed_and_bound_to_user_session_and_document()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var adminId = (await admin.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var page = Id(await CreatePageAsync(admin, key, "Live"));

        Assert.True((await admin.GetJsonAsync("api/v1/collaboration/config")).GetProperty("enabled").GetBoolean());
        using var issued = await admin.PostAsync("api/v1/collaboration/tickets", new { contentId = page });
        var ticket = await ReadAsync(issued);
        Assert.Equal("no-store", issued.Headers.CacheControl!.ToString());
        Assert.Equal($"{tenant.Id:D}:page:{page:D}:draft", ticket.GetProperty("documentName").GetString());
        Assert.Equal("wss://collab.raavi.test", ticket.GetProperty("url").GetString());

        // Verify with the public key the collaboration server would fetch.
        using var jwksResponse = await InternalAsync(HttpMethod.Get, "/internal/collaboration/jwks");
        var jwks = new JsonWebKeySet(await jwksResponse.Content.ReadAsStringAsync());
        Assert.All(jwks.Keys, k => Assert.False(k.HasPrivateKey));
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(ticket.GetProperty("ticket").GetString(), new TokenValidationParameters
        {
            ValidIssuer = TicketSigner.Issuer,
            ValidAudience = TicketSigner.Audience,
            IssuerSigningKeys = jwks.GetSigningKeys(),
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
        });
        Assert.True(validation.IsValid, validation.Exception?.Message);
        var jwt = (JsonWebToken)validation.SecurityToken;
        Assert.Equal(adminId.ToString("D"), jwt.Subject);
        Assert.Equal("edit", jwt.GetClaim("perm").Value);
        Assert.True(Guid.TryParse(jwt.GetClaim("sid").Value, out _));
        Assert.True(jwt.ValidTo - jwt.IssuedAt <= TimeSpan.FromSeconds(60));

        // No edit right → no ticket; other tenant's content → not found.
        using (var restrict = await admin.PutAsync($"api/v1/content/{page}/restrictions", new { view = Array.Empty<object>(), edit = new[] { new { subjectType = "User", subjectId = adminId } } }))
        {
            await ReadAsync(restrict);
        }

        using (var denied = await member.PostAsync("api/v1/collaboration/tickets", new { contentId = page }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        var (_, otherAdmin, _) = await _kit.Identity.TenantWithAdminAsync();
        using var foreign = await otherAdmin.PostAsync("api/v1/collaboration/tickets", new { contentId = page });
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task Internal_endpoints_require_a_valid_signature_and_stay_inside_the_document_tenant()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (otherTenant, otherAdmin, _) = await _kit.Identity.TenantWithAdminAsync();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var page = Id(await CreatePageAsync(admin, key, "Doc", body: Doc("published text")));
        var name = $"{tenant.Id:D}:page:{page:D}:draft";

        using (var unsigned = await InternalAsync(HttpMethod.Get, $"/internal/collaboration/documents/{name}/state", key: null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        }

        using (var wrongKey = await InternalAsync(HttpMethod.Get, $"/internal/collaboration/documents/{name}/state", key: "wrong-key-wrong-key-wrong-key-wrong"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrongKey.StatusCode);
        }

        using (var state = await InternalAsync(HttpMethod.Get, $"/internal/collaboration/documents/{name}/state"))
        {
            var body = await ReadAsync(state);
            Assert.Equal(JsonValueKind.Null, body.GetProperty("snapshot").ValueKind);
            Assert.Contains("published text", body.GetProperty("seed").GetRawText(), StringComparison.Ordinal); // first load is seeded
        }

        // The tenant in the address decides the scope: the same content id under another tenant does not exist.
        using (var crossTenant = await InternalAsync(HttpMethod.Get, $"/internal/collaboration/documents/{otherTenant.Id:D}:page:{page:D}:draft/state"))
        {
            Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
        }

        _ = otherAdmin;
    }

    [Fact]
    public async Task Stored_updates_keep_the_shared_draft_in_step_and_publish_takes_the_live_body()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, memberId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var adminId = (await admin.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var page = Id(await CreatePageAsync(admin, key, "Live page", body: Doc("v1")));
        var name = $"{tenant.Id:D}:page:{page:D}:draft";

        // A store identical to the published body creates no draft.
        using (var same = await InternalAsync(HttpMethod.Post, $"/internal/collaboration/documents/{name}/updates", new { update = Convert.ToBase64String([1, 2, 3]), json = Paragraphs("v1"), contributors = new[] { adminId } }))
        {
            Assert.Equal(1, (await ReadAsync(same)).GetProperty("seq").GetInt64());
        }

        Assert.False((await admin.GetJsonAsync($"api/v1/content/{page}/draft")).GetProperty("exists").GetBoolean());

        using (var edited = await InternalAsync(HttpMethod.Post, $"/internal/collaboration/documents/{name}/updates", new { update = Convert.ToBase64String([4, 5]), json = Paragraphs("live edit by two people"), contributors = new[] { adminId, memberId } }))
        {
            Assert.Equal(2, (await ReadAsync(edited)).GetProperty("seq").GetInt64());
        }

        var draft = await admin.GetJsonAsync($"api/v1/content/{page}/draft");
        Assert.True(draft.GetProperty("exists").GetBoolean());
        Assert.Contains("live edit by two people", draft.GetProperty("body").GetRawText(), StringComparison.Ordinal);
        Assert.Equal(2, draft.GetProperty("contributors").GetArrayLength());
        Assert.Equal("v1", (await member.GetJsonAsync($"api/v1/content/{page}")).GetProperty("body").GetProperty("doc").GetProperty("content")[0].GetProperty("content")[0].GetProperty("text").GetString()); // readers: published

        // Content outside the editor registry is refused and nothing is stored.
        using (var bad = await InternalAsync(HttpMethod.Post, $"/internal/collaboration/documents/{name}/updates", new { update = Convert.ToBase64String([6]), json = new { type = "doc", content = new[] { new { type = "script" } } }, contributors = Array.Empty<Guid>() }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }

        // Snapshot compacts the covered updates; state = snapshot + later updates.
        using (var snap = await InternalAsync(HttpMethod.Post, $"/internal/collaboration/documents/{name}/snapshot", new { state = Convert.ToBase64String(Encoding.UTF8.GetBytes("full-state")), seq = 2 }))
        {
            Assert.Equal(HttpStatusCode.NoContent, snap.StatusCode);
        }

        var state = await ReadAsync(await InternalAsync(HttpMethod.Get, $"/internal/collaboration/documents/{name}/state"));
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("full-state")), state.GetProperty("snapshot").GetString());
        Assert.Empty(state.GetProperty("updates").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("seed").ValueKind);

        // Real-time editors publish the latest draft with If-Match: * and the title set separately.
        using (var title = await admin.PutAsync($"api/v1/content/{page}/draft/title", new { title = "Live page (edited)" }))
        {
            await ReadAsync(title);
        }

        using (var publish = await admin.SendAsync(HttpMethod.Post, $"api/v1/content/{page}/publish", new { changeSummary = "from live editing" }, withXsrf: true, ifMatch: "*"))
        {
            var result = await ReadAsync(publish);
            Assert.Equal(2, result.GetProperty("page").GetProperty("version").GetInt32());
            Assert.Equal("Live page (edited)", result.GetProperty("page").GetProperty("title").GetString());
        }

        Assert.Contains("live edit by two people", (await member.GetJsonAsync($"api/v1/content/{page}")).GetProperty("body").GetRawText(), StringComparison.Ordinal);

        // Discarding the draft drops the live document: the next load is seeded again.
        await EditAndDiscardAsync(admin, page, name);
    }

    private async Task EditAndDiscardAsync(Browser admin, Guid page, string name)
    {
        using (var edit = await InternalAsync(HttpMethod.Post, $"/internal/collaboration/documents/{name}/updates", new { update = Convert.ToBase64String([9]), json = Paragraphs("to be discarded"), contributors = Array.Empty<Guid>() }))
        {
            await ReadAsync(edit);
        }

        using (var discard = await admin.DeleteAsync($"api/v1/content/{page}/draft"))
        {
            Assert.Equal(HttpStatusCode.NoContent, discard.StatusCode);
        }

        var state = await ReadAsync(await InternalAsync(HttpMethod.Get, $"/internal/collaboration/documents/{name}/state"));
        Assert.Equal(JsonValueKind.Null, state.GetProperty("snapshot").ValueKind);
        Assert.Contains("live edit by two people", state.GetProperty("seed").GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connection_checks_follow_sessions_membership_and_restrictions()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, memberId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var adminId = (await admin.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var page = Id(await CreatePageAsync(admin, key, "Checked"));
        var name = $"{tenant.Id:D}:page:{page:D}:draft";

        using var ticketResponse = await member.PostAsync("api/v1/collaboration/tickets", new { contentId = page });
        var sid = Guid.Parse(new JsonWebToken((await ReadAsync(ticketResponse)).GetProperty("ticket").GetString()).GetClaim("sid").Value);

        async Task<string> CheckAsync(Guid user, Guid session)
        {
            var r = await ReadAsync(await InternalAsync(HttpMethod.Post, "/internal/collaboration/check", new { documentName = name, userId = user, sessionId = session }));
            return r.GetProperty("permission").GetString()!;
        }

        Assert.Equal("edit", await CheckAsync(memberId, sid));
        Assert.Equal("none", await CheckAsync(memberId, Guid.NewGuid())); // unknown session

        using (var restrict = await admin.PutAsync($"api/v1/content/{page}/restrictions", new { view = Array.Empty<object>(), edit = new[] { new { subjectType = "User", subjectId = adminId } } }))
        {
            await ReadAsync(restrict);
        }

        Assert.Equal("view", await CheckAsync(memberId, sid)); // downgraded to read-only

        using (var remove = await admin.DeleteAsync($"api/v1/tenant/members/{memberId}"))
        {
            Assert.True(remove.IsSuccessStatusCode);
        }

        Assert.Equal("none", await CheckAsync(memberId, sid)); // removed member (sessions revoked) — immediately, no cache
    }
}
