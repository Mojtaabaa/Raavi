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
        "integrations",
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

        // Lowest layer: scalar defaults the shipped version declares in sandbox-defaults/, so a setting added by an upgrade has a value
        // even before `Raavi.Migrator sandbox sync` has copied it into the installation's files (ADR-040).
        if (SandboxSync.ResolveDefaultsPath(environment.ContentRootPath) is { } defaults)
        {
            configuration.Add(new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource { InitialData = SandboxSync.FlattenScalars(defaults) });
        }

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
        ApplyStatePaths(configuration, path);
        return path;
    }

    /// <summary>The instance root: the parent of <c>config</c> (development layout and the documented production layout), else the directory itself.</summary>
    public static string RootOf(string sandboxConfigPath)
    {
        var full = System.IO.Path.GetFullPath(sandboxConfigPath).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        return string.Equals(System.IO.Path.GetFileName(full), "config", StringComparison.OrdinalIgnoreCase) ? System.IO.Path.GetDirectoryName(full)! : full;
    }

    /// <summary>
    /// Everything an installation must keep across upgrades lives under the sandbox root: <c>config/</c> (settings), <c>data/</c> (state the
    /// application writes: Data Protection key ring, mail pickup) and <c>secrets/</c> (keys; never committed). Relative paths in settings are
    /// resolved against these folders instead of the process working directory, which changes with every release.
    /// </summary>
    private static void ApplyStatePaths(IConfigurationManager configuration, string sandboxConfigPath)
    {
        var root = RootOf(sandboxConfigPath);
        configuration["Raavi:Sandbox:Root"] = root;
        configuration["Raavi:Sandbox:DataPath"] = System.IO.Path.Combine(root, "data");
        configuration["Raavi:Sandbox:SecretsPath"] = System.IO.Path.Combine(root, "secrets");

        void Resolve(string key, string baseFolder, string? fallbackRelative)
        {
            var value = configuration[key];
            if (string.IsNullOrWhiteSpace(value))
            {
                value = fallbackRelative;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                configuration[key] = System.IO.Path.IsPathRooted(value) ? value : System.IO.Path.GetFullPath(System.IO.Path.Combine(root, baseFolder, value));
            }
        }

        Resolve("Raavi:Security:DataProtection:KeyRingPath", "data", "keys");
        Resolve("Raavi:Email:PickupDirectory", "data", null);
        Resolve("Raavi:Collaboration:SigningKeyPath", "secrets", null);
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
