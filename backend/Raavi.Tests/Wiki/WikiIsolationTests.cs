using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Infrastructure.Messaging;
using Raavi.Infrastructure.Tenancy;
using Raavi.Tests.Infrastructure;
using Raavi.Wiki.Application;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>Tenant isolation of the wiki core: HTTP, EF filters, RLS on raw SQL, and the outbox.</summary>
[Collection(SqlTestGroup.Name)]
public sealed class WikiIsolationTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    [Fact]
    public async Task Ids_and_keys_of_another_tenant_are_not_found()
    {
        var (_, adminA, _) = await _kit.Identity.TenantWithAdminAsync();
        var (_, adminB, _) = await _kit.Identity.TenantWithAdminAsync();
        var space = await CreateSpaceAsync(adminA);
        var key = space.GetProperty("key").GetString()!;
        var page = Id(await CreatePageAsync(adminA, key, "Tenant A only"));

        foreach (var path in new[] { $"api/v1/spaces/{key}", $"api/v1/content/{page}", $"api/v1/content/{page}/restrictions", $"api/v1/spaces/{key}/permissions" })
        {
            using var r = await adminB.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }

        // Writing into tenant A from tenant B's host: parent ids of another tenant are not found either.
        var bKey = (await CreateSpaceAsync(adminB)).GetProperty("key").GetString()!;
        using (var foreignParent = await adminB.PostAsync($"api/v1/spaces/{bKey}/content", new { title = "x", parentId = page, body = Doc("x") }))
        {
            await Browser.AssertStatusAsync(foreignParent, HttpStatusCode.NotFound, "content.not_found");
        }

        using (var foreignMove = await adminB.SendAsync(HttpMethod.Post, $"api/v1/content/{page}/move", new { parentId = (Guid?)null }, withXsrf: true, ifMatch: "\"r1\""))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreignMove.StatusCode);
        }

        // The same space key may exist in two tenants independently.
        using var sameKey = await adminB.PostAsync("api/v1/spaces", new { key, name = "B's own", type = "Team", visibility = "Open" });
        Assert.Equal(HttpStatusCode.Created, sameKey.StatusCode);
        Assert.Empty((await adminB.GetJsonAsync("api/v1/quick-find?q=Tenant%20A")).GetProperty("content").EnumerateArray());
    }

    [Fact]
    public async Task Row_level_security_confines_raw_sql_and_blocks_foreign_inserts()
    {
        var a = await fx.CreateTenantAsync(SqlServerFixture.Unique("WA"), SqlServerFixture.Unique("wa").ToLowerInvariant());
        var b = await fx.CreateTenantAsync(SqlServerFixture.Unique("WB"), SqlServerFixture.Unique("wb").ToLowerInvariant());
        var spaceId = Guid.Empty;
        await using (var scope = fx.TenantScope(a))
        {
            var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
            var space = Space.Create(UniqueKey(), "RLS", null, SpaceType.Team, Guid.NewGuid(), DateTimeOffset.UtcNow);
            db.Spaces.Add(space);
            await db.SaveChangesAsync();
            spaceId = space.Id;
        }

        await using (var scope = fx.TenantScope(b))
        {
            var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
            // Even with the EF filter disabled, RLS returns nothing of tenant A.
            Assert.Equal(0, await db.Spaces.IgnoreQueryFilters().CountAsync(s => s.Id == spaceId));
            Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM [spaces].[Spaces] WHERE Id = {spaceId}").SingleAsync());
            Assert.Equal(0, await db.Database.ExecuteSqlAsync($"UPDATE [spaces].[Spaces] SET Name = N'pwned' WHERE Id = {spaceId}"));

            // Block predicate: inserting a row stamped with tenant A from tenant B's session fails.
            var ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlAsync(
                $"INSERT INTO [content].[Labels] (Id, TenantId, Name, CreatedAt) VALUES ({Guid.NewGuid()}, {a.Id}, N'x', SYSDATETIMEOFFSET())"));
            Assert.Contains("block predicate", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Outbox_events_are_handled_in_their_own_tenant_and_inactive_tenants_are_deferred()
    {
        var a = await fx.CreateTenantAsync(SqlServerFixture.Unique("OA"), SqlServerFixture.Unique("oa").ToLowerInvariant());
        var actor = Guid.NewGuid();
        Guid spaceId;
        await using (var scope = fx.TenantScope(a))
        {
            var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
            var space = Space.Create(UniqueKey(), "Outbox", null, SpaceType.Team, actor, DateTimeOffset.UtcNow);
            db.Spaces.Add(space);
            db.Publish(WikiEventTypes.SpaceCreated, new WikiEvent(space.Id, null, actor, space.Name, null), DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            spaceId = space.Id;
        }

        await _kit.DispatchOutboxAsync();
        await _kit.DispatchOutboxAsync(); // replay-safe: nothing left, nothing duplicated
        await using (var scope = fx.TenantScope(a))
        {
            var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
            var entries = await db.Activity.Where(e => e.SpaceId == spaceId).ToListAsync();
            Assert.Single(entries);
            Assert.Equal(a.Id, entries[0].TenantId);
        }

        // Suspended tenant: its events wait instead of running outside an active tenant.
        await using (var scope = fx.TenantScope(a))
        {
            var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
            db.Publish(WikiEventTypes.SpaceUpdated, new WikiEvent(spaceId, null, actor, "again", null), DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        await using (var scope = fx.UnscopedScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<Raavi.Tenancy.Application.TenantAdministrationService>();
            var suspended = await admin.SuspendAsync(a.Id, "outbox test", CancellationToken.None);
            Assert.True(suspended.IsSuccess, suspended.Error?.Code);
        }

        await _kit.DispatchOutboxAsync();
        await using (var scope = fx.UnscopedScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
            var pending = await db.Outbox.Where(o => o.TenantId == a.Id && o.Type == WikiEventTypes.SpaceUpdated).SingleAsync();
            Assert.Equal(OutboxStatus.Pending, pending.Status);
            Assert.True(pending.AvailableAt > DateTimeOffset.UtcNow);
            Assert.Equal(0, pending.Attempts);
        }
    }

    [Fact]
    public async Task Failing_handlers_are_retried_with_back_off()
    {
        var a = await fx.CreateTenantAsync(SqlServerFixture.Unique("OF"), SqlServerFixture.Unique("of").ToLowerInvariant());
        var services = new ServiceCollection();
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(fx.Settings).Build();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(configuration);
        services.AddLogging();
        Raavi.Infrastructure.InfrastructureExtensions.AddRaaviInfrastructure(services, configuration);
        Raavi.Tenancy.TenancyModule.AddTenancyModule(services, configuration);
        services.AddHttpContextAccessor();
        Raavi.Identity.IdentityModule.AddIdentityCore(services, configuration);
        Raavi.Wiki.WikiModule.AddWikiModule(services, configuration);
        services.AddScoped<IOutboxHandler, FailingHandler>();
        await using var provider = services.BuildServiceProvider();

        Guid messageId;
        await using (var scope = provider.GetRequiredService<ITenantScopeFactory>().CreateScope(new ResolvedTenant(a.Id, a.Code, a.Slug, TenantResolutionSource.ServicePrincipal, null)))
        {
            var db = scope.ServiceProvider.GetRequiredService<WikiDbContext>();
            var message = OutboxMessage.Create(a.Id, FailingHandler.Type, new { x = 1 }, DateTimeOffset.UtcNow);
            db.Outbox.Add(message);
            await db.SaveChangesAsync();
            messageId = message.Id;
        }

        var dispatcher = provider.GetRequiredService<OutboxDispatcher<WikiDbContext>>();
        while (await dispatcher.DispatchOnceAsync() > 0)
        {
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<WikiDbContext>().Outbox.SingleAsync(o => o.Id == messageId);
            Assert.Equal(OutboxStatus.Pending, row.Status);
            Assert.Equal(1, row.Attempts);
            Assert.True(row.AvailableAt > DateTimeOffset.UtcNow);
            Assert.Contains("InvalidOperationException", row.LastError, StringComparison.Ordinal);
        }
    }

    private sealed class FailingHandler : IOutboxHandler
    {
        public const string Type = "test.always_fails";

        public bool CanHandle(string eventType) => eventType == Type;

        public Task HandleAsync(OutboxEvent outboxEvent, CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
    }
}
