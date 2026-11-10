using Raavi.AI;
using Raavi.Integrations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Identity;
using Raavi.Identity.Persistence;
using Raavi.Infrastructure;
using Raavi.Infrastructure.Jobs;
using Raavi.Infrastructure.Tenancy;
using Raavi.Tenancy;
using Raavi.Tenancy.Application;
using Raavi.Tenancy.Persistence;
using Raavi.Migration;
using Raavi.Wiki;
using Raavi.Wiki.Persistence;

namespace Raavi.Tests.Infrastructure;

/// <summary>
/// Real SQL Server for persistence and isolation tests (never EF in-memory — TESTING_GUIDE.md §2).
/// Uses <c>RAAVI_TEST_SQL</c> (server-level connection string, e.g. a CI service container) or LocalDB on Windows.
/// Creates a uniquely named database, applies the real migrations (incl. RLS) and drops it afterwards.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string SystemHost = "admin.raavi.test";
    public const string BaseDomain = "raavi.test";
    public const string SystemApiKey = "test-system-key-0123456789";
    public const string CollaborationServiceKey = "test-collab-service-key-0123456789abcdef";

    private readonly string _serverConnectionString =
        Environment.GetEnvironmentVariable("RAAVI_TEST_SQL")
        ?? @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true";

    private readonly string _databaseName = $"RaaviTest_{Guid.NewGuid():N}";
    private ApiFactory? _factory;

    public string ConnectionString { get; private set; } = string.Empty;

    public int CompatibilityLevel { get; private set; } = 160;

    public ServiceProvider Services { get; private set; } = null!;

    public FakeDnsTxtResolver Dns { get; } = new();

    public FakeExternalIdentity External { get; } = new();

    public FakeAiProvider Ai { get; } = new();

    public FakeWebhookReceiver Hooks { get; } = new();

    public FakeIssueTracker Tracker { get; } = new();

    public FakeLdapDirectory Ldap { get; } = new();

    public FakeEmailSender Mail { get; } = new();

    public Raavi.Tests.Migration.FakeConfluence Confluence { get; } = new();

    public ApiFactory Api => _factory ??= new ApiFactory(this);

    public IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["ConnectionStrings:System"] = ConnectionString,
        ["ConnectionStrings:Hangfire"] = ConnectionString,
        ["ConnectionStrings:Redis"] = string.Empty,
        ["Raavi:Database:CompatibilityLevel"] = CompatibilityLevel.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Raavi:Hosting:SystemHost"] = SystemHost,
        ["Raavi:Hosting:TenantBaseDomains:0"] = BaseDomain,
        ["Raavi:Hosting:PublicScheme"] = "https",
        ["Raavi:Hosting:PublicPort"] = string.Empty, // tests run in Development; do not inherit the dev server port
        ["Raavi:Security:RateLimiting:LoginPerMinutePerIp"] = "100000",
        ["Raavi:Attachments:MaxBytes"] = "1048576",
        ["Raavi:Migration:AllowedSourceHosts:0"] = "wiki.example.test",
        ["Raavi:Ai:AllowedHosts:0"] = "ai.example.test",
        ["Raavi:Integrations:AllowedWebhookHosts:0"] = "hooks.example.test",
        ["Raavi:Integrations:AllowedConnectorHosts:0"] = "git.example.test",
        ["Raavi:Integrations:AllowedConnectorHosts:1"] = "jira.example.test",
        ["Raavi:Ai:MaxRequestsPerUserPerHour"] = "6",
        ["Raavi:Email:Mode"] = "Disabled", // replaced by FakeEmailSender (enabled) in RegisterFakes
        ["Raavi:Notifications:PushIntervalMilliseconds"] = "200",
        // Real-time collaboration: tickets are signed with the Development ephemeral key; the collaboration server itself
        // is not running in API tests (calls to it fail fast and are best effort).
        ["Raavi:Collaboration:Enabled"] = "true",
        ["Raavi:Collaboration:PublicUrl"] = "wss://collab.raavi.test",
        ["Raavi:Collaboration:ServerUrl"] = string.Empty, // no collaboration server in API tests: flush/reset/recheck are no-ops
        ["Raavi:Collaboration:ServiceKey"] = CollaborationServiceKey,
        ["Raavi:Links:PathRewrites:0:Prefix"] = "vianna",
        ["Raavi:Links:PathRewrites:0:BaseUrl"] = "https://svn.viannacloud.ir",
        ["Raavi:Authentication:Session:AllowInsecureHttp"] = "false", // tests always exercise the strict production cookie mode
        // Globally defined external providers (tenants enable them through their own policies).
        ["Raavi:IdentityProviders:0:Id"] = "local",
        ["Raavi:IdentityProviders:0:Type"] = "Local",
        ["Raavi:IdentityProviders:0:Enabled"] = "true",
        ["Raavi:IdentityProviders:0:DisplayName:En"] = "Raavi account",
        ["Raavi:IdentityProviders:1:Id"] = "oidc-test",
        ["Raavi:IdentityProviders:1:Type"] = "OpenIdConnect",
        ["Raavi:IdentityProviders:1:Enabled"] = "true",
        ["Raavi:IdentityProviders:1:DisplayName:En"] = "Partner SSO",
        ["Raavi:IdentityProviders:1:Authority"] = FakeExternalIdentity.Issuer,
        ["Raavi:IdentityProviders:1:ClientId"] = FakeExternalIdentity.ClientId,
        ["Raavi:IdentityProviders:1:ClientSecret"] = FakeExternalIdentity.ClientSecret,
        ["Raavi:IdentityProviders:2:Id"] = "jira-test",
        ["Raavi:IdentityProviders:2:Type"] = "JiraDataCenter",
        ["Raavi:IdentityProviders:2:Enabled"] = "true",
        ["Raavi:IdentityProviders:2:DisplayName:En"] = "Engineering Jira",
        ["Raavi:IdentityProviders:2:BaseUrl"] = FakeExternalIdentity.JiraBase,
        ["Raavi:IdentityProviders:2:ClientId"] = FakeExternalIdentity.ClientId,
        ["Raavi:IdentityProviders:2:ClientSecret"] = FakeExternalIdentity.ClientSecret,
        ["Raavi:IdentityProviders:2:CallbackMode"] = "Relay",
        ["Raavi:IdentityProviders:3:Id"] = "corp-ad",
        ["Raavi:IdentityProviders:3:Type"] = "ActiveDirectory",
        ["Raavi:IdentityProviders:3:Enabled"] = "true",
        ["Raavi:IdentityProviders:3:DisplayName:En"] = "Corporate AD",
        ["Raavi:IdentityProviders:3:Server"] = "ldaps://dc.test:636",
        ["Raavi:IdentityProviders:3:BaseDn"] = "DC=test",
        ["Raavi:IdentityProviders:3:BindDn"] = "CN=svc,DC=test",
        ["Raavi:IdentityProviders:3:BindPassword"] = "svc-password",
        ["Raavi:IdentityProviders:3:TrustEmail"] = "true",
        ["Raavi:Security:SystemApiKeys:0:Id"] = "test",
        ["Raavi:Security:SystemApiKeys:0:Sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SystemApiKey))),
    };

    public async Task InitializeAsync()
    {
        var builder = new SqlConnectionStringBuilder(_serverConnectionString) { InitialCatalog = "master" };
        await using (var master = new SqlConnection(builder.ConnectionString))
        {
            await master.OpenAsync();
            await using var version = new SqlCommand("SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int)", master);
            var major = (int)(await version.ExecuteScalarAsync())!;
            CompatibilityLevel = Math.Min(160, major * 10); // 13 → 130 (2016), 16 → 160 (2022)
#pragma warning disable CA2100 // generated, validated identifier
            await using var create = new SqlCommand($"CREATE DATABASE [{_databaseName}]", master);
#pragma warning restore CA2100
            await create.ExecuteNonQueryAsync();
        }

        builder.InitialCatalog = _databaseName;
        ConnectionString = builder.ConnectionString;

        Services = BuildServices();
        await TenancyMigrations.ApplyAsync(Services);
        await IdentityMigrations.ApplyAsync(Services);
        await WikiMigrations.ApplyAsync(Services);
        await Raavi.Migration.Persistence.MigrationMigrations.ApplyAsync(Services);
        await Raavi.AI.Persistence.AiMigrations.ApplyAsync(Services);
        await Raavi.Integrations.Persistence.IntegrationsMigrations.ApplyAsync(Services);
        HangfireExtensions.InstallSchema(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await Services.DisposeAsync();
        SqlConnection.ClearAllPools();
        var builder = new SqlConnectionStringBuilder(_serverConnectionString) { InitialCatalog = "master" };
        await using var master = new SqlConnection(builder.ConnectionString);
        await master.OpenAsync();
#pragma warning disable CA2100
        await using var drop = new SqlCommand($"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}];", master);
#pragma warning restore CA2100
        await drop.ExecuteNonQueryAsync();
    }

    /// <summary>Service provider equivalent to the worker host (no HTTP), for persistence-level tests.</summary>
    private ServiceProvider BuildServices()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddRaaviInfrastructure(configuration);
        services.AddRaaviJobs(configuration, runServer: false);
        services.AddTenancyModule(configuration);
        services.AddHttpContextAccessor();
        services.AddIdentityCore(configuration);
        services.AddWikiModule(configuration);
        services.AddMigrationModule(configuration);
        services.AddAiModule(configuration);
        services.AddIntegrationsModule(configuration);
        services.Replace(ServiceDescriptor.Singleton<IDnsTxtResolver>(Dns));
        RegisterFakes(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>Routes identity-provider HTTP traffic to the in-process fakes and replaces the LDAP directory.</summary>
    public void RegisterFakes(IServiceCollection services)
    {
        services.AddHttpClient(Raavi.Integrations.Webhooks.WebhookHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => new SharedHandler(Hooks));
        services.AddHttpClient(Raavi.Integrations.Connectors.ConnectorHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => new SharedHandler(Tracker));
        services.AddHttpClient(Raavi.AI.AiHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => new SharedHandler(Ai));
        services.AddHttpClient(Raavi.Identity.Providers.IdentityHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => new SharedHandler(External));
        services.Replace(ServiceDescriptor.Singleton<Raavi.Identity.Providers.ILdapDirectoryFactory>(Ldap));
        services.Replace(ServiceDescriptor.Singleton<Raavi.Application.Abstractions.Email.IEmailSender>(Mail));
        services.Replace(ServiceDescriptor.Singleton<Raavi.Migration.Confluence.IConfluenceSourceFactory>(Confluence));
    }

    /// <summary>Creates and provisions an active tenant through the real administration service.</summary>
    public async Task<TenantDto> CreateTenantAsync(string code, string slug)
    {
        await using var scope = Services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<TenantAdministrationService>();
        var result = await svc.CreateAsync(new CreateTenantRequest(code, slug, $"سازمان {code}", $"Org {code}", "fa", "Asia/Tehran", "persian"), CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Code);
        return result.Value;
    }

    public AsyncServiceScope TenantScope(TenantDto tenant) =>
        Services.GetRequiredService<ITenantScopeFactory>()
            .CreateScope(new ResolvedTenant(tenant.Id, tenant.Code, tenant.Slug, TenantResolutionSource.ServicePrincipal, null));

    public AsyncServiceScope UnscopedScope() => Services.CreateAsyncScope();

    public static string Unique(string prefix) => prefix + RandomNumberGenerator.GetHexString(6, lowercase: false);
}

[CollectionDefinition(Name)]
public sealed class SqlTestGroup : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sql";
}

public sealed class ApiFactory(SqlServerFixture fixture) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(fixture.Settings));
        builder.ConfigureServices(s =>
        {
            s.Replace(ServiceDescriptor.Singleton<IDnsTxtResolver>(fixture.Dns));
            fixture.RegisterFakes(s);
        });
    }

    public HttpClient ForHost(string host)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{host}/"), AllowAutoRedirect = false, HandleCookies = false });
        return client;
    }

    public HttpClient ForSystemAdmin()
    {
        var client = ForHost(SqlServerFixture.SystemHost);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SqlServerFixture.SystemApiKey);
        return client;
    }
}

public sealed class FakeDnsTxtResolver : IDnsTxtResolver
{
    private readonly Dictionary<string, List<string>> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public void Set(string name, params string[] values)
    {
        lock (_gate)
        {
            _records[name] = [.. values];
        }
    }

    public void Clear(string name)
    {
        lock (_gate)
        {
            _records.Remove(name);
        }
    }

    public Task<IReadOnlyList<string>> QueryTxtAsync(string name, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<string>>(_records.TryGetValue(name, out var v) ? [.. v] : []);
        }
    }
}

/// <summary>Captures outgoing e-mail instead of sending it.</summary>
public sealed class FakeEmailSender : Raavi.Application.Abstractions.Email.IEmailSender
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<Raavi.Application.Abstractions.Email.EmailMessage> _sent = new();

    public bool IsEnabled => true;

    public IReadOnlyList<Raavi.Application.Abstractions.Email.EmailMessage> Sent => [.. _sent];

    public IReadOnlyList<Raavi.Application.Abstractions.Email.EmailMessage> To(string address) => _sent.Where(m => string.Equals(m.To, address, StringComparison.OrdinalIgnoreCase)).ToList();

    public Task SendAsync(Raavi.Application.Abstractions.Email.EmailMessage message, CancellationToken cancellationToken = default)
    {
        _sent.Enqueue(message);
        return Task.CompletedTask;
    }
}
