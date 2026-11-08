using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Raavi.Identity;
using Raavi.Identity.Api;
using Raavi.Identity.Security;
using Raavi.AI;
using Raavi.Integrations;
using Raavi.Migration;
using Raavi.Infrastructure;
using Raavi.Infrastructure.Configuration;
using Raavi.Infrastructure.Http;
using Raavi.Infrastructure.Jobs;
using Raavi.Infrastructure.Logging;
using Raavi.Infrastructure.Observability;
using Raavi.Tenancy;
using Raavi.Tenancy.Api;
using Raavi.Wiki;
using Raavi.Wiki.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddRaaviSandboxConfiguration(builder.Environment, args);
builder.AddRaaviLogging();
builder.AddRaaviObservability("raavi-api");

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Only configured proxies may set X-Forwarded-*; with none configured, only loopback is trusted (ASP.NET default).
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    foreach (var proxy in builder.Configuration.GetSection("Raavi:Hosting:KnownProxies").Get<string[]>() ?? [])
    {
        o.KnownProxies.Add(IPAddress.Parse(proxy));
    }

    foreach (var network in builder.Configuration.GetSection("Raavi:Hosting:KnownNetworks").Get<string[]>() ?? [])
    {
        o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    }
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(TenancyEndpoints.DiscoveryRateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddRaaviHttpFoundation();
builder.Services.AddRaaviInfrastructure(builder.Configuration);
builder.Services.AddRaaviJobs(builder.Configuration, runServer: false);
builder.Services.AddTenancyModule(builder.Configuration);
builder.Services.AddIdentityModule(builder.Configuration);
builder.Services.AddWikiModule(builder.Configuration);
builder.Services.AddWikiRealtime();
builder.Services.AddMigrationModule(builder.Configuration);
builder.Services.AddAiModule(builder.Configuration);
builder.Services.AddIntegrationsModule(builder.Configuration);
builder.Services.AddOpenApi("v1");

var app = builder.Build();
SandboxCommand.WarnIfDrifted(app.Environment.ContentRootPath, builder.Configuration["Raavi:SandboxPath"]!, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Raavi.Sandbox"));

app.UseForwardedHeaders();
app.UseRaaviHttpFoundation();
app.UseTenantResolution();
app.UseCollaborationSignatures();
app.UseRouting();
app.UseRaaviHostKindEnforcement();
app.UseRateLimiter();
app.UseAuthentication();
app.UseRaaviAntiforgery();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

var api = app.MapRaaviApiV1();
api.MapTenancyEndpoints();
api.MapIdentityEndpoints();
api.MapWikiEndpoints();
api.MapCollaborationEndpoints();
api.MapCommentEndpoints();
api.MapTaskEndpoints();
api.MapCalendarEndpoints();
api.MapDatabaseEndpoints();
api.MapSlideEndpoints();
api.MapWhiteboardEndpoints();
api.MapWorkflowEndpoints();
api.MapExportEndpoints();
api.MapApprovalEndpoints();
api.MapMigrationEndpoints();
api.MapAiEndpoints();
api.MapIntegrationsEndpoints();
app.MapCollaborationInternalEndpoints();
app.MapWikiRealtime();

await app.RunAsync().ConfigureAwait(false);

/// <summary>Entry point; public for WebApplicationFactory-based integration tests.</summary>
public partial class Program;
