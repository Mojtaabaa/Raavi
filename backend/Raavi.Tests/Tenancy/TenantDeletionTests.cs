using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Infrastructure.Tenancy;
using Raavi.Tenancy.Application;
using Raavi.Tests.Identity;
using Raavi.Tests.Infrastructure;
using Raavi.Wiki.Persistence;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Tenancy;

/// <summary>Workspace deletion (ADR-050): request → cancel / purge, with the other workspace untouched.</summary>
[Collection(SqlTestGroup.Name)]
public sealed class TenantDeletionTests(SqlServerFixture fx)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Raavi.Tests.Wiki.WikiTestKit _kit = new(fx);

    private async Task<int> SpaceCountAsync(IdentityTestKit.TestTenant t)
    {
        await using var scope = fx.Services.GetRequiredService<ITenantScopeFactory>()
            .CreateScope(new ResolvedTenant(t.Id, t.Code, t.Slug, TenantResolutionSource.BackgroundJob, null));
        return await scope.ServiceProvider.GetRequiredService<WikiDbContext>().Spaces.CountAsync();
    }

    private static async Task<JsonElement> PostAsync(HttpClient admin, Guid id, string action, object? body = null)
    {
        using var r = await admin.PostAsJsonAsync($"api/v1/system/tenants/{id}/{action}", body ?? new { });
        var text = await r.Content.ReadAsStringAsync();
        var json = text.Length == 0 ? default : JsonSerializer.Deserialize<JsonElement>(text, Json);
        return JsonSerializer.SerializeToElement(new { status = (int)r.StatusCode, body = json });
    }

    [Fact]
    public async Task Deletion_needs_the_code_blocks_access_and_can_be_cancelled()
    {
        var (tenant, tenantAdmin, _) = await _kit.Identity.TenantWithAdminAsync();
        await CreateSpaceAsync(tenantAdmin);
        using var admin = fx.Api.ForSystemAdmin();

        var wrong = await PostAsync(admin, tenant.Id, "delete-request", new { reason = "closing down", confirmCode = "NOPE" });
        Assert.Equal(400, wrong.GetProperty("status").GetInt32());
        Assert.Contains("tenant.confirm_mismatch", wrong.GetProperty("body").ToString(), StringComparison.Ordinal);
        var noReason = await PostAsync(admin, tenant.Id, "delete-request", new { reason = "", confirmCode = tenant.Code });
        Assert.Equal(400, noReason.GetProperty("status").GetInt32());

        var ok = await PostAsync(admin, tenant.Id, "delete-request", new { reason = "closing down", confirmCode = tenant.Code.ToLowerInvariant() });
        Assert.Equal(200, ok.GetProperty("status").GetInt32());
        Assert.Equal("PendingDeletion", ok.GetProperty("body").GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, ok.GetProperty("body").GetProperty("deletionScheduledFor").ValueKind);

        using var host = fx.Api.ForHost($"{tenant.Slug}.{SqlServerFixture.BaseDomain}");
        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync(new Uri("api/v1/host/context", UriKind.Relative))).StatusCode); // access stopped at once
        Assert.Equal(409, (await PostAsync(admin, tenant.Id, "delete-request", new { reason = "again", confirmCode = tenant.Code })).GetProperty("status").GetInt32());
        Assert.Equal(409, (await PostAsync(admin, tenant.Id, "activate")).GetProperty("status").GetInt32()); // activation is not the way back

        var cancelled = await PostAsync(admin, tenant.Id, "cancel-deletion");
        Assert.Equal("Active", cancelled.GetProperty("body").GetProperty("status").GetString()); // back to what it was
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync(new Uri("api/v1/host/context", UriKind.Relative))).StatusCode);
        Assert.Equal(1, await SpaceCountAsync(tenant)); // nothing was removed
        Assert.Equal(409, (await PostAsync(admin, tenant.Id, "cancel-deletion")).GetProperty("status").GetInt32());
        Assert.Equal(409, (await PostAsync(admin, tenant.Id, "purge", new { confirmCode = tenant.Code })).GetProperty("status").GetInt32()); // not requested
    }

    [Fact]
    public async Task Purge_removes_the_workspace_data_only_keeps_a_tombstone_and_leaves_other_workspaces_alone()
    {
        var (doomed, doomedAdmin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (other, otherAdmin, _) = await _kit.Identity.TenantWithAdminAsync();
        await CreateSpaceAsync(doomedAdmin);
        await CreateSpaceAsync(otherAdmin);
        using var admin = fx.Api.ForSystemAdmin();

        Assert.Equal(1, await SpaceCountAsync(doomed));
        Assert.Equal(1, await SpaceCountAsync(other));
        await PostAsync(admin, doomed.Id, "delete-request", new { reason = "closing down", confirmCode = doomed.Code });

        Assert.Equal(400, (await PostAsync(admin, doomed.Id, "purge", new { confirmCode = "WRONG" })).GetProperty("status").GetInt32());
        Assert.Equal(1, await SpaceCountAsync(doomed)); // a wrong code removes nothing

        var purged = await PostAsync(admin, doomed.Id, "purge", new { confirmCode = doomed.Code });
        Assert.Equal(200, purged.GetProperty("status").GetInt32());
        Assert.Equal("Deleted", purged.GetProperty("body").GetProperty("status").GetString());

        Assert.Equal(0, await SpaceCountAsync(doomed));
        Assert.Equal(1, await SpaceCountAsync(other));
        using var otherHost = fx.Api.ForHost($"{other.Slug}.{SqlServerFixture.BaseDomain}");
        Assert.Equal(HttpStatusCode.OK, (await otherHost.GetAsync(new Uri("api/v1/host/context", UriKind.Relative))).StatusCode);

        // the tombstone keeps code and slug reserved, and nothing more can be done to it
        var again = await admin.PostAsJsonAsync("api/v1/system/tenants", new { code = doomed.Code, slug = doomed.Slug, displayNameFa = "x", displayNameEn = "x", defaultLocale = "en", timeZone = "UTC", calendar = "gregorian" });
        Assert.True(again.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest);
        Assert.Equal(409, (await PostAsync(admin, doomed.Id, "purge", new { confirmCode = doomed.Code })).GetProperty("status").GetInt32());
        Assert.Equal(409, (await PostAsync(admin, doomed.Id, "cancel-deletion")).GetProperty("status").GetInt32());
        using var gone = fx.Api.ForHost($"{doomed.Slug}.{SqlServerFixture.BaseDomain}");
        Assert.Equal(HttpStatusCode.NotFound, (await gone.GetAsync(new Uri("api/v1/host/context", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task The_daily_job_purges_only_workspaces_whose_grace_period_has_ended()
    {
        var (due, dueAdmin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (waiting, waitingAdmin, _) = await _kit.Identity.TenantWithAdminAsync();
        await CreateSpaceAsync(dueAdmin);
        await CreateSpaceAsync(waitingAdmin);
        using var admin = fx.Api.ForSystemAdmin();

        await PostAsync(admin, waiting.Id, "delete-request", new { reason = "later", confirmCode = waiting.Code }); // default grace: 14 days
        await PostAsync(admin, due.Id, "delete-request", new { reason = "now", confirmCode = due.Code });
        await using (var scope = fx.UnscopedScope())
        {
            // move the "due" workspace's schedule into the past
            var db = scope.ServiceProvider.GetRequiredService<Raavi.Tenancy.Persistence.TenancyDbContext>();
            await db.Database.ExecuteSqlRawAsync("UPDATE [system].[Tenants] SET DeletionScheduledFor = DATEADD(day, -1, SYSDATETIMEOFFSET()) WHERE Id = {0}", due.Id);
        }

        await using (var scope = fx.UnscopedScope())
        {
            await scope.ServiceProvider.GetRequiredService<TenantPurgeJob>().RunAsync(CancellationToken.None);
        }

        Assert.Equal(0, await SpaceCountAsync(due));
        Assert.Equal(1, await SpaceCountAsync(waiting)); // grace period still running: untouched
        var list = await admin.GetFromJsonAsync<JsonElement>("api/v1/system/tenants?take=100", Json);
        var states = list.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetGuid(), i => i.GetProperty("status").GetString());
        Assert.Equal("Deleted", states[due.Id]);
        Assert.Equal("PendingDeletion", states[waiting.Id]);
    }
}
