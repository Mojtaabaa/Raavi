using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Application.Abstractions.Identity;
using Raavi.Identity.Api;
using Raavi.Identity.Application;
using Raavi.Identity.Persistence;
using Raavi.Identity.Providers;
using Raavi.Identity.Security;
using Raavi.Identity.SystemAccess;
using Raavi.Infrastructure.Persistence;

namespace Raavi.Identity;

public static class IdentityModule
{
    /// <summary>Identity persistence and services, usable by every host (API, workers, migrator, tests).</summary>
    public static IServiceCollection AddIdentityCore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<RaaviAuthenticationOptions>()
            .Bind(configuration.GetSection(RaaviAuthenticationOptions.Section))
            .Validate<Microsoft.Extensions.Hosting.IHostEnvironment>(
                (o, env) => !o.Session.AllowInsecureHttp || Microsoft.Extensions.Hosting.HostEnvironmentEnvExtensions.IsDevelopment(env),
                "Raavi:Authentication:Session:AllowInsecureHttp may only be enabled in the Development environment.")
            .ValidateOnStart();
        services.AddRaaviDbContext<IdentityDbContext>(configuration, IdentityDbContext.Schema);

        services.AddDataProtection().SetApplicationName("raavi");
        // Shared, persistent key ring (Raavi:Security:DataProtection:KeyRingPath) so every node can unprotect TOTP secrets
        // and antiforgery tokens. Resolved lazily from the built configuration.
        services.AddOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>()
            .Configure<IConfiguration, Microsoft.Extensions.Logging.ILoggerFactory>((o, cfg, logs) =>
            {
                var keyRing = cfg["Raavi:Security:DataProtection:KeyRingPath"];
                if (!string.IsNullOrWhiteSpace(keyRing))
                {
                    o.XmlRepository = new Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository(new DirectoryInfo(keyRing), logs);
                }
            });

        services.AddHttpClient(IdentityHttp.ClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(15);
            c.MaxResponseContentBufferSize = 1024 * 1024;
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Raavi/1.0");
        });
        services.AddSingleton<IAuthenticationProviderFactory, LocalAuthenticationProviderFactory>();
        services.AddSingleton<IAuthenticationProviderFactory, OpenIdConnectProviderFactory>();
        services.AddSingleton<IAuthenticationProviderFactory, JiraDataCenterProviderFactory>();
        services.AddSingleton<ILdapDirectoryFactory, LdapDirectoryFactory>();
        services.AddSingleton<IAuthenticationProviderFactory>(sp => new LdapProviderFactory(AuthenticationProviderType.Ldap, sp.GetRequiredService<ILdapDirectoryFactory>(), sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()));
        services.AddSingleton<IAuthenticationProviderFactory>(sp => new LdapProviderFactory(AuthenticationProviderType.ActiveDirectory, sp.GetRequiredService<ILdapDirectoryFactory>(), sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()));
        services.AddSingleton<Raavi.Identity.ExternalLogin.PendingLoginStore>();
        services.AddScoped<Raavi.Identity.ExternalLogin.ExternalSignInService>();
        services.AddScoped<Raavi.Identity.ExternalLogin.ExternalLoginFlow>();
        services.AddScoped<AccountService>();
        services.AddSingleton<AuthenticationProviderRegistry>();
        services.AddSingleton<IAuthenticationProviderRegistry>(sp => sp.GetRequiredService<AuthenticationProviderRegistry>());
        services.AddScoped<ITenantProviderPolicyService, TenantProviderPolicyService>();
        services.AddSingleton<PasswordService>();
        services.AddSingleton<TotpService>();
        services.AddScoped<SessionService>();
        services.AddScoped<MembershipService>();
        services.AddScoped<ITenantMembershipService>(sp => sp.GetRequiredService<MembershipService>());
        services.AddScoped<SecurityEventWriter>();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddScoped<IPrincipalDirectory, PrincipalDirectory>();
        services.AddScoped<ISessionValidator, SessionValidator>();
        services.AddScoped<AuthService>();
        services.AddScoped<TenantIdentityAdminService>();
        services.AddScoped<SystemIdentityService>();
        return services;
    }

    /// <summary>HTTP authentication, authorization policies, antiforgery and login rate limiting (API host only).</summary>
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddIdentityCore(configuration);
        services.AddOptions<SystemApiKeyOptions>().Bind(configuration.GetSection(SystemApiKeyOptions.Section));

        services.AddAuthentication(RaaviSessionAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, RaaviSessionAuthenticationHandler>(RaaviSessionAuthenticationHandler.SchemeName, null)
            .AddScheme<AuthenticationSchemeOptions, SystemApiKeyAuthenticationHandler>(SystemApiKeyAuthenticationHandler.SchemeName, null);

        services.AddAuthorizationBuilder()
            .AddPolicy(RaaviPolicies.SystemAdministrator, p => p
                .AddAuthenticationSchemes(RaaviSessionAuthenticationHandler.SchemeName, SystemApiKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireRole(RaaviPolicies.SystemAdministratorRole))
            .AddPolicy(RaaviPolicies.TenantAdministrator, p => p
                .AddAuthenticationSchemes(RaaviSessionAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireRole(RaaviPolicies.TenantAdministratorRole))
            .AddPolicy(RaaviPolicies.AuthenticatedUser, p => p
                .AddAuthenticationSchemes(RaaviSessionAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireClaim(RaaviPolicies.ActorTypeClaim, "user"));

        services.AddAntiforgery();
        services.AddOptions<Microsoft.AspNetCore.Antiforgery.AntiforgeryOptions>()
            .Configure<Microsoft.Extensions.Options.IOptions<RaaviAuthenticationOptions>>((o, auth) =>
            {
                var insecure = auth.Value.Session.AllowInsecureHttp;
                o.HeaderName = AntiforgeryProtection.HeaderName;
                o.Cookie.Name = auth.Value.Session.AntiforgeryCookieName;
                o.Cookie.SecurePolicy = insecure ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.HttpOnly = true;
                o.Cookie.Path = "/";
            });

        services.Configure<RateLimiterOptions>(o => o.AddPolicy(IdentityEndpoints.LoginRateLimitPolicy, ctx =>
        {
            var limit = ctx.RequestServices.GetRequiredService<IConfiguration>().GetValue("Raavi:Security:RateLimiting:LoginPerMinutePerIp", 10);
            return RateLimitPartition.GetFixedWindowLimiter(
                "login:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
        }));

        return services;
    }
}
