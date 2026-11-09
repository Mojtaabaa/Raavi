using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Raavi.Identity.Security;
using Raavi.Tests.Infrastructure;
using Raavi.Tests.Wiki;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Identity;

/// <summary>Phase 14: two-step verification setting, administrator tools for members, colour theme, workspace logo, database filters, new board shapes.</summary>
[Collection(SqlTestGroup.Name)]
public sealed class Phase14Tests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    private static ByteArrayContent Part(byte[] bytes, string type = "image/png")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
        form.Add(file, "file", "picture");
        return new ByteArrayContent(form.ReadAsByteArrayAsync().Result) { Headers = { ContentType = form.Headers.ContentType } };
    }

    // ── Two-step verification can be switched off ────────────────────────────────────────────────

    [Fact]
    public async Task System_sign_in_needs_the_code_by_default_and_not_when_the_setting_turns_it_off()
    {
        using var defaults = new Browser(fx.Api, SqlServerFixture.SystemHost);
        await defaults.PrimeAsync();
        Assert.True((await defaults.GetJsonAsync("api/v1/auth/system/options")).GetProperty("mfaRequired").GetBoolean());

        await using var off = fx.Api.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Raavi:Authentication:SystemMfaRequired"] = "false" })));
        using var client = off.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{SqlServerFixture.SystemHost}/"), AllowAutoRedirect = false, HandleCookies = true });

        async Task<HttpResponseMessage> Post(string path, object body, string xsrf)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            req.Headers.Add("X-XSRF-TOKEN", xsrf);
            return await client.SendAsync(req);
        }

        async Task<string> Xsrf() => (await client.GetFromJsonAsync<JsonElement>("api/v1/auth/antiforgery", Browser.Json)).GetProperty("token").GetString()!;

        Assert.False((await client.GetFromJsonAsync<JsonElement>("api/v1/auth/system/options", Browser.Json)).GetProperty("mfaRequired").GetBoolean());

        // Invite (API key), enrol WITHOUT an authenticator, then sign in with the password alone.
        var email = $"{SqlServerFixture.Unique("nomfa").ToLowerInvariant()}@example.com";
        using var key = fx.Api.ForSystemAdmin();
        using var invite = await key.PostAsJsonAsync("api/v1/system/administrators/invitations", new { email, tenantAdministrator = false });
        var created = await invite.Content.ReadFromJsonAsync<JsonElement>(Browser.Json);
        var token = new Uri(created.GetProperty("acceptUrl").GetString()!).Fragment["#token=".Length..];

        var xsrf = await Xsrf();
        using var begin = await Post("api/v1/auth/system/setup/begin", new { token }, xsrf);
        var secret = (await begin.Content.ReadFromJsonAsync<JsonElement>(Browser.Json)).GetProperty("secret").GetString();
        Assert.True(string.IsNullOrEmpty(secret)); // nothing to scan

        using var complete = await Post("api/v1/auth/system/setup/complete", new { token, displayName = "Root", password = "No-Second-Step-Passw0rd!", code = "" }, xsrf);
        Assert.True(complete.IsSuccessStatusCode, await complete.Content.ReadAsStringAsync());

        using var fresh = off.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{SqlServerFixture.SystemHost}/"), AllowAutoRedirect = false, HandleCookies = true });
        var freshXsrf = (await fresh.GetFromJsonAsync<JsonElement>("api/v1/auth/antiforgery", Browser.Json)).GetProperty("token").GetString()!;
        using var login = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/system/login") { Content = JsonContent.Create(new { email, password = "No-Second-Step-Passw0rd!", code = "" }) };
        login.Headers.Add("X-XSRF-TOKEN", freshXsrf);
        using var signedIn = await fresh.SendAsync(login);
        Assert.True(signedIn.IsSuccessStatusCode, await signedIn.Content.ReadAsStringAsync());

        // The same sign-in on the normal (default) server still demands a code: the setting is per installation, not per request.
        using var strict = new Browser(fx.Api, SqlServerFixture.SystemHost);
        await strict.PrimeAsync();
        using var noCode = await strict.PostAsync("api/v1/auth/system/login", new { email, password = "No-Second-Step-Passw0rd!", code = "" });
        await Browser.AssertStatusAsync(noCode, HttpStatusCode.Unauthorized, "auth.invalid_credentials");
    }

    // ── Administrator tools for members ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_workspace_administrator_sets_a_members_password_and_picture_but_a_member_cannot()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, memberEmail, memberId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var (other, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);

        // Password: the policy applies, only administrators may, the member's sessions end, and the new password works.
        using (var weak = await admin.SendAsync(HttpMethod.Put, $"api/v1/tenant/members/{memberId}/password", JsonContent.Create(new { newPassword = "short" }), withXsrf: true))
        {
            await Browser.AssertStatusAsync(weak, HttpStatusCode.BadRequest, "password.length");
        }

        using (var denied = await other.SendAsync(HttpMethod.Put, $"api/v1/tenant/members/{memberId}/password", JsonContent.Create(new { newPassword = "A-Brand-New-Passw0rd!" }), withXsrf: true))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        using (var ok = await admin.SendAsync(HttpMethod.Put, $"api/v1/tenant/members/{memberId}/password", JsonContent.Create(new { newPassword = "A-Brand-New-Passw0rd!" }), withXsrf: true))
        {
            Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        }

        using (var ended = await member.GetAsync("api/v1/auth/me"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);
        }

        var again = await _kit.Identity.LoginAsync(tenant, memberEmail, "A-Brand-New-Passw0rd!");
        Assert.Equal(memberId, (await again.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid());

        // Picture: the administrator sets and removes it; a plain member cannot.
        using (var set = await admin.SendAsync(HttpMethod.Put, $"api/v1/tenant/members/{memberId}/avatar", Part(Pic.Png(256, 256)), withXsrf: true))
        {
            Assert.Equal(1, (await ReadAsync(set)).GetProperty("version").GetInt32());
        }

        using (var seen = await other.GetAsync($"api/v1/users/{memberId}/avatar"))
        {
            Assert.Equal(HttpStatusCode.OK, seen.StatusCode);
        }

        using (var denied = await other.SendAsync(HttpMethod.Put, $"api/v1/tenant/members/{memberId}/avatar", Part(Pic.Png(256, 256)), withXsrf: true))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        using (var bad = await admin.SendAsync(HttpMethod.Put, $"api/v1/tenant/members/{memberId}/avatar", Part(System.Text.Encoding.UTF8.GetBytes("<svg onload='x()'/>")), withXsrf: true))
        {
            await Browser.AssertStatusAsync(bad, HttpStatusCode.BadRequest, "avatar.type_invalid");
        }

        using (var removed = await admin.DeleteAsync($"api/v1/tenant/members/{memberId}/avatar"))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        using (var gone = await other.GetAsync($"api/v1/users/{memberId}/avatar"))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }
    }

    [Fact]
    public async Task An_account_shared_with_another_workspace_is_not_managed_by_either_administrator()
    {
        var (tenantA, adminA, _) = await _kit.Identity.TenantWithAdminAsync();
        var (tenantB, adminB, _) = await _kit.Identity.TenantWithAdminAsync();
        var (_, email, sharedId) = await _kit.Identity.AddMemberAsync(tenantA, adminA);

        // The same person is invited into workspace B as well.
        var token = await _kit.Identity.SystemInviteAsync(tenantB, email, admin: false);
        var inB = new Browser(fx.Api, tenantB.Host);
        await inB.PrimeAsync();
        await inB.PostOkAsync("api/v1/auth/invitations/accept", new { token, mode = "existing", password = IdentityTestKit.Password });

        foreach (var admin in new[] { adminA, adminB })
        {
            using var pw = await admin.SendAsync(HttpMethod.Put, $"api/v1/tenant/members/{sharedId}/password", JsonContent.Create(new { newPassword = "Take-Over-Passw0rd!" }), withXsrf: true);
            await Browser.AssertStatusAsync(pw, HttpStatusCode.Forbidden, "member.account_shared");
            using var pic = await admin.SendAsync(HttpMethod.Put, $"api/v1/tenant/members/{sharedId}/avatar", Part(Pic.Png(64, 64)), withXsrf: true);
            await Browser.AssertStatusAsync(pic, HttpStatusCode.Forbidden, "member.account_shared");
        }

        // Someone who is not a member of the workspace at all is simply not found.
        using var stranger = await adminA.SendAsync(HttpMethod.Put, $"api/v1/tenant/members/{Guid.NewGuid()}/password", JsonContent.Create(new { newPassword = "Whatever-Passw0rd!" }), withXsrf: true);
        await Browser.AssertStatusAsync(stranger, HttpStatusCode.NotFound, "member.not_found");
    }

    // ── Colour theme ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_chosen_theme_is_stored_in_the_profile_and_only_known_themes_are_kept()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        Assert.Equal(JsonValueKind.Null, (await admin.GetJsonAsync("api/v1/auth/me")).GetProperty("theme").ValueKind);

        using (var set = await admin.SendAsync(HttpMethod.Put, "api/v1/auth/me/theme", JsonContent.Create(new { theme = "chocolate" }), withXsrf: true))
        {
            Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        }

        Assert.Equal("chocolate", (await admin.GetJsonAsync("api/v1/auth/me")).GetProperty("theme").GetString());

        foreach (var bad in new[] { "<script>", "neon", string.Empty })
        {
            using var r = await admin.SendAsync(HttpMethod.Put, "api/v1/auth/me/theme", JsonContent.Create(new { theme = bad }), withXsrf: true);
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
            Assert.Equal(JsonValueKind.Null, (await admin.GetJsonAsync("api/v1/auth/me")).GetProperty("theme").ValueKind); // unknown = back to "like my device"
        }

        Assert.All(Raavi.Identity.Domain.User.Themes, t => Assert.Matches("^[a-z]{1,16}$", t));
    }

    // ── Workspace logo and address ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_system_administrator_gives_a_workspace_a_logo_that_its_own_pages_can_show()
    {
        var tenant = await _kit.Identity.CreateTenantAsync();
        using var key = fx.Api.ForSystemAdmin();
        using var anonymous = fx.Api.ForHost(tenant.Host);

        using (var none = await anonymous.GetAsync("api/v1/host/logo"))
        {
            Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        }

        var png = Pic.Png(256, 256);
        using (var set = await key.PutAsync($"api/v1/system/tenants/{tenant.Id}/logo", Part(png)))
        {
            Assert.Equal(1, (await ReadAsync(set)).GetInt32());
        }

        using (var logo = await anonymous.GetAsync("api/v1/host/logo"))
        {
            Assert.Equal(HttpStatusCode.OK, logo.StatusCode);
            Assert.Equal("image/png", logo.Content.Headers.ContentType!.MediaType);
            Assert.Equal(png, await logo.Content.ReadAsByteArrayAsync());
            Assert.Equal("\"l1\"", logo.Headers.ETag!.Tag);
            Assert.Equal("nosniff", logo.Headers.GetValues("X-Content-Type-Options").Single());
        }

        // The public context tells the SPA which version to ask for (so a new logo has a new address).
        var context = await anonymous.GetFromJsonAsync<JsonElement>("api/v1/host/context", Browser.Json);
        Assert.Equal(1, context.GetProperty("tenant").GetProperty("branding").GetProperty("logoVersion").GetInt32());

        // Another workspace does not get it; only real pictures are accepted.
        var other = await _kit.Identity.CreateTenantAsync();
        using (var otherLogo = await fx.Api.ForHost(other.Host).GetAsync("api/v1/host/logo"))
        {
            Assert.Equal(HttpStatusCode.NotFound, otherLogo.StatusCode);
        }

        foreach (var (bytes, code) in new[]
        {
            (System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"), "logo.type_invalid"),
            (Pic.Png(256, 256, extra: 300 * 1024), "logo.too_large"),
        })
        {
            using var bad = await key.PutAsync($"api/v1/system/tenants/{tenant.Id}/logo", Part(bytes));
            await Browser.AssertStatusAsync(bad, HttpStatusCode.BadRequest, code);
        }

        using (var replaced = await key.PutAsync($"api/v1/system/tenants/{tenant.Id}/logo", Part(Pic.Jpeg(200, 200), "image/jpeg")))
        {
            Assert.Equal(2, (await ReadAsync(replaced)).GetInt32());
        }

        using (var removed = await key.DeleteAsync($"api/v1/system/tenants/{tenant.Id}/logo"))
        {
            Assert.True(removed.IsSuccessStatusCode);
        }

        using (var gone = await anonymous.GetAsync("api/v1/host/logo"))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }

        // The workspace list carries the address people open it with.
        var list = await key.GetFromJsonAsync<JsonElement>("api/v1/system/tenants?take=100", Browser.Json);
        var mine = list.GetProperty("items").EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == tenant.Id);
        Assert.Contains(tenant.Slug, mine.GetProperty("url").GetString(), StringComparison.Ordinal);
    }

    // ── Database filters and totals ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Database_rows_can_be_filtered_by_column_and_number_columns_are_totalled_over_all_matches()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        using var created = await admin.PostAsync($"api/v1/spaces/{key}/databases", new
        {
            name = "Work",
            columns = new object[]
            {
                new { name = "Name", type = "Text" },
                new { name = "Points", type = "Number" },
                new { name = "Status", type = "Select", options = new[] { new { label = "Todo" }, new { label = "Done" } } },
                new { name = "Ok", type = "Checkbox" },
            },
        });
        var database = await ReadAsync(created);
        var id = database.GetProperty("id").GetGuid();
        var cols = database.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("id").GetString()!).ToArray();
        var done = database.GetProperty("columns")[2].GetProperty("options")[1].GetProperty("id").GetString()!;

        async Task Add(Dictionary<string, object> values)
        {
            using var r = await admin.PostAsync($"api/v1/databases/{id}/rows", new { values });
            Assert.True(r.IsSuccessStatusCode);
        }

        await Add(new() { [cols[0]] = "Banana", [cols[1]] = 10, [cols[2]] = done, [cols[3]] = true });
        await Add(new() { [cols[0]] = "apple", [cols[1]] = 20 });
        await Add(new() { [cols[0]] = "Cherry", [cols[1]] = 30, [cols[2]] = done });

        async Task<JsonElement> Rows(string query) => await admin.GetJsonAsync($"api/v1/databases/{id}/rows?{query}");

        Assert.Equal(2, (await Rows($"f={cols[2]}:{done}")).GetProperty("total").GetInt32()); // Select: that option
        Assert.Equal(1, (await Rows($"f={cols[2]}:")).GetProperty("total").GetInt32()); // Select: no value
        Assert.Equal(1, (await Rows($"f={cols[3]}:true")).GetProperty("total").GetInt32()); // Checkbox
        Assert.Equal(2, (await Rows($"f={cols[3]}:false")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await Rows($"f={cols[0]}:AN")).GetProperty("total").GetInt32()); // "Banana" only
        Assert.Equal(1, (await Rows($"f={cols[0]}:cherry")).GetProperty("total").GetInt32()); // text: contains, ignoring case
        Assert.Equal(1, (await Rows($"f={cols[2]}:{done}&f={cols[3]}:true")).GetProperty("total").GetInt32()); // filters combine (AND)

        // Totals cover every matching row, not only the page that is returned.
        var all = await Rows("take=1");
        var points = all.GetProperty("summary").GetProperty(cols[1]);
        Assert.Equal((3, 60d, 10d, 30d, 20d), (points.GetProperty("count").GetInt32(), points.GetProperty("sum").GetDouble(), points.GetProperty("min").GetDouble(), points.GetProperty("max").GetDouble(), points.GetProperty("average").GetDouble()));
        var filtered = (await Rows($"take=1&f={cols[2]}:{done}")).GetProperty("summary").GetProperty(cols[1]);
        Assert.Equal(40d, filtered.GetProperty("sum").GetDouble());

        using var unknown = await admin.GetAsync($"api/v1/databases/{id}/rows?f=cnope:x");
        await Browser.AssertStatusAsync(unknown, HttpStatusCode.BadRequest, "db.sort_invalid");
    }
}
