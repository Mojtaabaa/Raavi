using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Raavi.Infrastructure.Configuration;

/// <summary>
/// Loads GLOBAL (all-tenant) configuration from the sandbox/config directory (ADR-018, docs/architecture/SYSTEM_ARCHITECTURE.md §6).
/// Tenant-specific configuration is never read from here — it lives in SQL Server.
///
/// Order (later wins):
///   1. appsettings.json (required) and appsettings.{Environment}.json
///   2. each known module file (authentication, identity-providers, storage, search, collaboration, security, feature-flags)
///      and its {name}.{Environment}.json variant
///   3. environment variables prefixed RAAVI_ (e.g. RAAVI_ConnectionStrings__System)
///   4. command-line arguments
/// </summary>
public static class SandboxConfiguration
{
    public const string PathVariable = "RAAVI_SANDBOX_PATH";
    public const string EnvironmentPrefix = "RAAVI_";

    public static readonly IReadOnlyList<string> ModuleFiles =
    [
        "authentication",
        "identity-providers",
        "storage",
        "search",
        "collaboration",
        "notifications",
        "migration",
        "ai",
        "security",
        "feature-flags",
    ];

    /// <summary>Adds the sandbox sources to <paramref name="configuration"/> and returns the resolved sandbox path.</summary>
    public static string AddRaaviSandboxConfiguration(
        this IConfigurationManager configuration,
        IHostEnvironment environment,
        string[]? commandLineArgs = null,
        string? explicitPath = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var path = ResolvePath(environment.ContentRootPath, explicitPath ?? Environment.GetEnvironmentVariable(PathVariable));
        EnsureNotPubliclyServed(path, environment.ContentRootPath);

        var main = System.IO.Path.Combine(path, "appsettings.json");
        if (!File.Exists(main))
        {
            throw new InvalidOperationException($"Raavi sandbox configuration is missing the required file '{main}'.");
        }

        configuration.AddJsonFile(main, optional: false, reloadOnChange: false);
        configuration.AddJsonFile(System.IO.Path.Combine(path, $"appsettings.{environment.EnvironmentName}.json"), optional: true, reloadOnChange: false);

        foreach (var module in ModuleFiles)
        {
            configuration.AddJsonFile(System.IO.Path.Combine(path, $"{module}.json"), optional: true, reloadOnChange: false);
            configuration.AddJsonFile(System.IO.Path.Combine(path, $"{module}.{environment.EnvironmentName}.json"), optional: true, reloadOnChange: false);
        }

        configuration.AddEnvironmentVariables(EnvironmentPrefix);
        if (commandLineArgs is { Length: > 0 })
        {
            configuration.AddCommandLine(commandLineArgs);
        }

        configuration["Raavi:SandboxPath"] = path;
        return path;
    }

    /// <summary>
    /// Uses the explicit path when given; otherwise walks up from the content root looking for "sandbox/config"
    /// (development layout). Production deployments always set RAAVI_SANDBOX_PATH to a mounted, non-public directory.
    /// </summary>
    public static string ResolvePath(string contentRoot, string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var full = System.IO.Path.GetFullPath(explicitPath);
            return Directory.Exists(full)
                ? full
                : throw new InvalidOperationException($"Raavi sandbox directory '{full}' does not exist.");
        }

        for (var dir = new DirectoryInfo(contentRoot); dir is not null; dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "sandbox", "config");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Raavi sandbox configuration directory was not found above '{contentRoot}'. Set {PathVariable}.");
    }

    private static void EnsureNotPubliclyServed(string sandboxPath, string contentRoot)
    {
        var webRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(contentRoot, "wwwroot")) + System.IO.Path.DirectorySeparatorChar;
        var full = System.IO.Path.GetFullPath(sandboxPath) + System.IO.Path.DirectorySeparatorChar;
        if (full.StartsWith(webRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The Raavi sandbox configuration directory must not be located inside the web root.");
        }
    }
}
