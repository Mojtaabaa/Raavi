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
using Raavi.Integrations;
using Raavi.Wiki;
using Raavi.Wiki.Application;

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
builder.Services.AddScoped<TenantFanOut<StaleSweepJob, StaleSweepArgs>>();
builder.Services.AddMigrationModule(builder.Configuration);
builder.Services.AddIntegrationsModule(builder.Configuration);
builder.Services.AddScoped<DatabaseMaintenanceJob>();

var host = builder.Build();
SandboxCommand.WarnIfDrifted(builder.Environment.ContentRootPath, builder.Configuration["Raavi:SandboxPath"]!, host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Raavi.Sandbox"));

// Recurring system jobs. Tenant jobs are enqueued through ITenantJobScheduler and executed by TenantJobRunner.
var recurring = host.Services.GetRequiredService<IRecurringJobManager>();
recurring.AddOrUpdate<DatabaseMaintenanceJob>(DatabaseMaintenanceJob.RecurringJobId, "maintenance", j => j.RunAsync(CancellationToken.None), Cron.Daily(3, 30));
recurring.AddOrUpdate<DomainVerificationJob>(
    DomainVerificationJob.RecurringJobId,
    "maintenance",
    j => j.RunAsync(CancellationToken.None),
    Cron.Hourly());

// Time-based workflow trigger: once a day every active tenant gets a stale-page sweep (ADR-033).
recurring.AddOrUpdate<TenantFanOut<StaleSweepJob, StaleSweepArgs>>(
    StaleSweepJob.RecurringJobId,
    "maintenance",
    j => j.RunAsync(new StaleSweepArgs(1), CancellationToken.None),
    Cron.Daily(3));

await host.RunAsync().ConfigureAwait(false);
