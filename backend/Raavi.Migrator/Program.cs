using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Raavi.Infrastructure;
using Raavi.Infrastructure.Configuration;
using Raavi.Infrastructure.Jobs;
using Raavi.Infrastructure.Logging;
using Raavi.Identity;
using Raavi.Identity.Persistence;
using Raavi.AI;
using Raavi.Migration;
using Raavi.Tenancy;
using Raavi.Tenancy.Persistence;
using Raavi.Wiki;
using Raavi.Wiki.Persistence;

// Raavi database migrator (ADR-020). Applies module migrations in dependency order and installs the Hangfire schema.
// Uses ConnectionStrings:Migrator (DDL login) when present, otherwise ConnectionStrings:System (development only).
var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddRaaviSandboxConfiguration(builder.Environment, args);
builder.AddRaaviLogging();

var migratorCs = builder.Configuration.GetConnectionString("Migrator");
if (!string.IsNullOrWhiteSpace(migratorCs))
{
    builder.Configuration["ConnectionStrings:System"] = migratorCs;
}

builder.Services.AddRaaviInfrastructure(builder.Configuration);
builder.Services.AddRaaviJobs(builder.Configuration, runServer: false);
builder.Services.AddTenancyModule(builder.Configuration);
builder.Services.AddIdentityCore(builder.Configuration);
builder.Services.AddWikiModule(builder.Configuration);
builder.Services.AddMigrationModule(builder.Configuration);
builder.Services.AddAiModule(builder.Configuration);

using var host = builder.Build();

// Order matters: system (tenancy) first; later modules (identity, core, …) are appended as they are added.
await TenancyMigrations.ApplyAsync(host.Services).ConfigureAwait(false);
await IdentityMigrations.ApplyAsync(host.Services).ConfigureAwait(false);
await WikiMigrations.ApplyAsync(host.Services).ConfigureAwait(false);
await Raavi.Migration.Persistence.MigrationMigrations.ApplyAsync(host.Services).ConfigureAwait(false);
await Raavi.AI.Persistence.AiMigrations.ApplyAsync(host.Services).ConfigureAwait(false);
HangfireExtensions.InstallSchema(builder.Configuration.GetConnectionString("System")!);
Console.WriteLine("Raavi database is up to date.");
