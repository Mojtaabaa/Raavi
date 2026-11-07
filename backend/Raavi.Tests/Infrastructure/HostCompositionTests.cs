using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raavi.Infrastructure;
using Raavi.Infrastructure.Jobs;
using Raavi.Identity;
using Raavi.Tenancy;
using Raavi.Wiki;

namespace Raavi.Tests.Infrastructure;

/// <summary>
/// Builds the Workers composition with full DI validation. (Phase 1 smoke testing found the worker host failing at
/// startup because an HTTP-only module was registered; this guards against that class of regression.)
/// </summary>
[Collection(SqlTestGroup.Name)]
public sealed class HostCompositionTests(SqlServerFixture fx)
{
    [Fact]
    public void Worker_service_graph_is_valid()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Production" });
        builder.Configuration.AddInMemoryCollection(fx.Settings);
        builder.Services.AddRaaviInfrastructure(builder.Configuration);
        builder.Services.AddRaaviJobs(builder.Configuration, runServer: true);
        builder.Services.AddTenancyModule(builder.Configuration);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddIdentityCore(builder.Configuration);
        builder.Services.AddWikiModule(builder.Configuration, runOutboxPoller: true);

        using var provider = builder.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.NotNull(provider.GetRequiredService<Hangfire.IRecurringJobManager>());
    }
}
