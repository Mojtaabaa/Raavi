using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Raavi.Tests.Identity;
using Raavi.Tests.Infrastructure;

namespace Raavi.Tests.Tenancy;

[Collection(SqlTestGroup.Name)]
public sealed class EmailSettingsTests(SqlServerFixture fx)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Email_settings_are_saved_without_returning_the_password_and_invalid_ones_are_refused()
    {
        using var admin = fx.Api.ForSystemAdmin();
        using var tenantHost = fx.Api.ForHost($"x.{SqlServerFixture.BaseDomain}");
        Assert.True((await tenantHost.GetAsync(new Uri("api/v1/system/email", UriKind.Relative))).StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized);

        using (var bad = await admin.PutAsJsonAsync("api/v1/system/email", new { mode = "Smtp", fromAddress = "nope", host = "", port = 587, security = "StartTls" })) { Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode); }
        using var ok = await admin.PutAsJsonAsync("api/v1/system/email", new { mode = "Smtp", fromAddress = "raavi@example.com", fromName = "Raavi", host = "smtp.example.com", port = 587, security = "StartTls", userName = "u", password = "s3cret" });
        var body = await ok.Content.ReadAsStringAsync();
        Assert.True(ok.IsSuccessStatusCode, body);
        Assert.DoesNotContain("s3cret", body, StringComparison.Ordinal);
        var shown = await admin.GetFromJsonAsync<JsonElement>("api/v1/system/email", Json);
        Assert.Equal("smtp.example.com", shown.GetProperty("host").GetString());
        Assert.True(shown.GetProperty("hasPassword").GetBoolean());
        Assert.Equal("application", shown.GetProperty("source").GetString());
        // leaving the password empty keeps it; the setting can be switched off again
        using var keep = await admin.PutAsJsonAsync("api/v1/system/email", new { mode = "Disabled", fromAddress = "raavi@example.com", host = "smtp.example.com", port = 587, security = "StartTls", userName = "u" });
        Assert.True((await keep.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("hasPassword").GetBoolean());
        using (var badTo = await admin.PostAsJsonAsync("api/v1/system/email/test", new { to = "" })) { Assert.Equal(HttpStatusCode.BadRequest, badTo.StatusCode); }
    }

    [Fact]
    public async Task Workspace_administrators_set_the_time_zone_and_calendar_and_the_public_context_follows()
    {
        var kit = new IdentityTestKit(fx);
        var (tenant, admin, _) = await kit.TenantWithAdminAsync();
        var (member, _, _) = await kit.AddMemberAsync(tenant, admin);
        using (var denied = await member.PutAsync("api/v1/tenant/settings", new { defaultLocale = "en", timeZone = "UTC", calendar = "gregorian" })) { Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        using (var bad = await admin.PutAsync("api/v1/tenant/settings", new { defaultLocale = "en", timeZone = "Mars/Olympus", calendar = "gregorian" })) { Assert.True(bad.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity); }
        using (var ok = await admin.PutAsync("api/v1/tenant/settings", new { defaultLocale = "fa", timeZone = "Europe/Berlin", calendar = "persian" })) { Assert.Equal(HttpStatusCode.OK, ok.StatusCode); }
        var ctx = await admin.GetJsonAsync("api/v1/host/context");
        Assert.Equal("Europe/Berlin", ctx.GetProperty("tenant").GetProperty("timeZone").GetString());
        Assert.Equal("persian", ctx.GetProperty("tenant").GetProperty("calendar").GetString());
    }
}
