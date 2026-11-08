using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Raavi.Infrastructure.Configuration;

/// <summary>One difference between the shipped defaults (<c>sandbox-defaults</c>) and an installation's <c>sandbox/config</c>.</summary>
public sealed record SandboxDrift(string File, string Key, DriftKind Kind);

public enum DriftKind
{
    /// <summary>The whole file is missing in the sandbox.</summary>
    MissingFile,

    /// <summary>The key exists in the shipped defaults but not in the sandbox file (a setting added by a newer version).</summary>
    MissingKey,

    /// <summary>The key exists in the sandbox but no longer in the shipped defaults (renamed/removed setting, or a local extra). Reported only.</summary>
    UnknownKey,
}

public sealed record SandboxSyncResult(IReadOnlyList<SandboxDrift> Drift, IReadOnlyList<string> ChangedFiles, string? BackupDirectory);

/// <summary>
/// Keeps an installation's <c>sandbox/config</c> in step with the settings a new version ships (ADR-040).
/// Rules: it only ADDS what is missing; it never changes or removes a value the administrator set; arrays are one value (never merged);
/// <c>*.{Environment}.json</c>/<c>*.Local.json</c> variants and keys starting with "_" are not part of the comparison; before any file is
/// touched the original is copied to <c>sandbox/backups/&lt;timestamp&gt;/</c>.
/// </summary>
public static class SandboxSync
{
    private static readonly string[] Base = ["appsettings"];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Finds <c>sandbox-defaults/config</c> by walking up from <paramref name="contentRoot"/> (or uses <c>RAAVI_SANDBOX_DEFAULTS_PATH</c>). Null when absent.</summary>
    public static string? ResolveDefaultsPath(string contentRoot)
    {
        var explicitPath = Environment.GetEnvironmentVariable("RAAVI_SANDBOX_DEFAULTS_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Directory.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;
        }

        for (var dir = new DirectoryInfo(contentRoot); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "sandbox-defaults", "config");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Base files only: appsettings.json and {module}.json.</summary>
    public static IEnumerable<string> BaseFiles() => Base.Concat(SandboxConfiguration.ModuleFiles).Select(n => n + ".json");

    public static SandboxSyncResult Run(string defaultsPath, string sandboxConfigPath, bool apply, DateTimeOffset? now = null)
    {
        var drift = new List<SandboxDrift>();
        var changed = new List<string>();
        string? backup = null;
        foreach (var file in BaseFiles())
        {
            var shipped = Path.Combine(defaultsPath, file);
            if (!File.Exists(shipped))
            {
                continue;
            }

            var target = Path.Combine(sandboxConfigPath, file);
            var shippedNode = Parse(shipped) ?? new JsonObject();
            if (!File.Exists(target))
            {
                drift.Add(new SandboxDrift(file, "(file)", DriftKind.MissingFile));
                if (apply)
                {
                    File.Copy(shipped, target);
                    changed.Add(file);
                }

                continue;
            }

            var current = Parse(target) ?? throw new InvalidOperationException($"'{target}' is not a valid JSON object; fix it by hand (sync never rewrites an unreadable file).");
            var added = new List<string>();
            Merge(shippedNode, current, "", file, drift, added);
            foreach (var key in Unknown(current, shippedNode, ""))
            {
                drift.Add(new SandboxDrift(file, key, DriftKind.UnknownKey));
            }

            if (apply && added.Count > 0)
            {
                backup ??= Path.Combine(SandboxConfiguration.RootOf(sandboxConfigPath), "backups", (now ?? DateTimeOffset.UtcNow).ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(backup);
                File.Copy(target, Path.Combine(backup, file), overwrite: false);
                File.WriteAllText(target, current.ToJsonString(Indented) + Environment.NewLine);
                changed.Add(file);
            }
        }

        return new SandboxSyncResult(drift, changed, backup);
    }

    private static JsonObject? Parse(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Merge(JsonObject shipped, JsonObject current, string prefix, string file, List<SandboxDrift> drift, List<string> added)
    {
        foreach (var (name, value) in shipped)
        {
            if (name.StartsWith('_'))
            {
                continue;
            }

            var key = prefix + name;
            if (!current.ContainsKey(name))
            {
                drift.Add(new SandboxDrift(file, key, DriftKind.MissingKey));
                added.Add(key);
                current[name] = value?.DeepClone();
            }
            else if (value is JsonObject so && current[name] is JsonObject co)
            {
                Merge(so, co, key + ":", file, drift, added);
            }
        }
    }

    private static IEnumerable<string> Unknown(JsonObject current, JsonObject shipped, string prefix)
    {
        foreach (var (name, value) in current)
        {
            if (name.StartsWith('_'))
            {
                continue;
            }

            if (!shipped.ContainsKey(name))
            {
                yield return prefix + name;
            }
            else if (value is JsonObject co && shipped[name] is JsonObject so)
            {
                foreach (var k in Unknown(co, so, prefix + name + ":"))
                {
                    yield return k;
                }
            }
        }
    }

    /// <summary>
    /// Flattens the shipped defaults into configuration keys (<c>A:B:C</c>) for the lowest configuration layer. Arrays and nulls are left out on
    /// purpose: configuration merges arrays index by index, so a default list would leak items into a shorter list the administrator wrote.
    /// </summary>
    public static Dictionary<string, string?> FlattenScalars(string defaultsPath)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in BaseFiles())
        {
            var path = Path.Combine(defaultsPath, file);
            if (File.Exists(path) && Parse(path) is { } node)
            {
                Walk(node, "", result);
            }
        }

        return result;
    }

    private static void Walk(JsonObject node, string prefix, Dictionary<string, string?> result)
    {
        foreach (var (name, value) in node)
        {
            if (name.StartsWith('_'))
            {
                continue;
            }

            switch (value)
            {
                case JsonObject o:
                    Walk(o, prefix + name + ":", result);
                    break;
                case JsonValue v:
                    result[prefix + name] = v.ToString();
                    break;
            }
        }
    }
}

/// <summary>Command line and start-up helpers around <see cref="SandboxSync"/>.</summary>
public static partial class SandboxCommand
{
    /// <summary><c>sandbox check</c> (exit 1 when settings of this version are missing) or <c>sandbox sync</c> (adds them; never changes existing values).</summary>
    public static int Run(string[] args, string contentRoot)
    {
        var verb = args.Length > 1 ? args[1].ToLowerInvariant() : "check";
        if (verb is not ("check" or "sync"))
        {
            Console.Error.WriteLine("Usage: Raavi.Migrator sandbox [check|sync]");
            return 2;
        }

        var defaults = SandboxSync.ResolveDefaultsPath(contentRoot);
        if (defaults is null)
        {
            Console.Error.WriteLine("sandbox-defaults/config was not found (set RAAVI_SANDBOX_DEFAULTS_PATH).");
            return 2;
        }

        var sandbox = SandboxConfiguration.ResolvePath(contentRoot, Environment.GetEnvironmentVariable(SandboxConfiguration.PathVariable));
        var result = SandboxSync.Run(defaults, sandbox, apply: verb == "sync");
        foreach (var d in result.Drift)
        {
            Console.WriteLine($"{d.Kind,-11} {d.File}  {d.Key}");
        }

        var missing = result.Drift.Count(d => d.Kind != DriftKind.UnknownKey);
        if (verb == "sync")
        {
            Console.WriteLine(result.ChangedFiles.Count == 0
                ? "Sandbox already contains every setting of this version."
                : $"Updated {result.ChangedFiles.Count} file(s): {string.Join(", ", result.ChangedFiles)}. Originals: {result.BackupDirectory ?? "(none needed)"}");
            return 0;
        }

        Console.WriteLine(missing == 0 ? "Sandbox is up to date." : $"{missing} setting(s)/file(s) of this version are missing from the sandbox. Run: Raavi.Migrator sandbox sync");
        return missing == 0 ? 0 : 1;
    }

    /// <summary>Logs (never throws) when the running sandbox lacks settings the shipped version declares; values are not logged.</summary>
    public static void WarnIfDrifted(string contentRoot, string sandboxConfigPath, Microsoft.Extensions.Logging.ILogger logger)
    {
        try
        {
            if (SandboxSync.ResolveDefaultsPath(contentRoot) is not { } defaults)
            {
                return;
            }

            var missing = SandboxSync.Run(defaults, sandboxConfigPath, apply: false).Drift.Where(d => d.Kind != DriftKind.UnknownKey).ToList();
            if (missing.Count > 0)
            {
                Drifted(logger, missing.Count, string.Join(", ", missing.Take(10).Select(d => d.File + ":" + d.Key)));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            CheckFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sandbox configuration lacks {Count} setting(s) of this version (running on shipped defaults): {Keys}. Run 'Raavi.Migrator sandbox sync'.")]
    private static partial void Drifted(ILogger logger, int count, string keys);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sandbox drift check failed.")]
    private static partial void CheckFailed(ILogger logger, Exception exception);
}
