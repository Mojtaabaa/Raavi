using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Raavi.Tenancy.Persistence;

/// <summary>Applies the tenancy schema migrations. Called by Raavi.Migrator and database integration tests — never by app hosts.</summary>
public static class TenancyMigrations
{
    public static async Task ApplyAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var scope = services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            await scope.ServiceProvider.GetRequiredService<TenancyDbContext>().Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
