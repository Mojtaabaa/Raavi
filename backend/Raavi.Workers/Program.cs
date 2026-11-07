using Hangfire;
using Raavi.Infrastructure;
using Raavi.Infrastructure.Configuration;
using Raavi.Infrastructure.Jobs;
using Raavi.Infrastructure.Logging;
using Raavi.Infrastructure.Observability;
using Raavi.Tenancy;
using Raavi.Tenancy.Application;
using Raavi.Identity;
using Raavi.Migration;
using Raavi.Wiki;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddRaaviSandboxConfiguration(builder.Environment, args);
builder.AddRaaviLogging();
builder.AddRaaviObservability("raavi-workers");

builder.Services.AddRaaviInfrastructure(builder.Configuration);
builder.Services.AddRaaviJobs(builder.Configuration, runServer: true);
builder.Services.AddTenancyModule(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddIdentityCore(builder.Configuration);
builder.Services.AddWikiModule(builder.Configuration, runOutboxPoller: true);
builder.Services.AddMigrationModule(builder.Configuration);

var host = builder.Build();

// Recurring system jobs. Tenant jobs are enqueued through ITenantJobScheduler and executed by TenantJobRunner.
var recurring = host.Services.GetRequiredService<IRecurringJobManager>();
recurring.AddOrUpdate<DomainVerificationJob>(
    DomainVerificationJob.RecurringJobId,
    "maintenance",
    j => j.RunAsync(CancellationToken.None),
    Cron.Hourly());

await host.RunAsync().ConfigureAwait(false);
