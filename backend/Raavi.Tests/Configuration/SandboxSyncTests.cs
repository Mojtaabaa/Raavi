using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Raavi.Infrastructure.Configuration;

namespace Raavi.Tests.Configuration;

/// <summary>ADR-040: the sandbox keeps everything an installation owns, and an upgrade can add new settings without touching the administrator's values.</summary>
public sealed class SandboxSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "raavi-tests", Guid.NewGuid().ToString("N"));

    public SandboxSyncTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Dir(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string dir, string file, string json) => File.WriteAllText(Path.Combine(dir, file), json);

    private static IEnumerable<string> Keys(JsonNode? node, string prefix = "")
    {
        if (node is JsonObject o)
        {
            foreach (var (name, value) in o)
            {
                if (name.StartsWith('_'))
                {
                    continue;
                }

                if (value is JsonObject)
                {
                    foreach (var k in Keys(value, prefix + name + ":"))
                    {
                        yield return k;
                    }
                }
                else
                {
                    yield return prefix + name;
                }
            }
        }
    }

    [Fact]
    public void Repository_sandbox_and_shipped_defaults_declare_the_same_settings()
    {
        var sandbox = SandboxConfiguration.ResolvePath(AppContext.BaseDirectory, explicitPath: null);
        var defaults = SandboxSync.ResolveDefaultsPath(AppContext.BaseDirectory);
        Assert.NotNull(defaults);
        foreach (var file in SandboxSync.BaseFiles())
        {
            Assert.True(File.Exists(Path.Combine(defaults!, file)), $"sandbox-defaults/config/{file} is missing — copy a new file there too");
            var shipped = Keys(JsonNode.Parse(File.ReadAllText(Path.Combine(defaults!, file)))).Order().ToList();
            var current = Keys(JsonNode.Parse(File.ReadAllText(Path.Combine(sandbox, file)))).Order().ToList();
            Assert.Equal(shipped, current); // a setting added to one side only means the other side was forgotten
        }

        Assert.Empty(SandboxSync.Run(defaults!, sandbox, apply: false).Drift);
    }

    [Fact]
    public void Sync_adds_missing_keys_and_files_but_never_changes_existing_values()
    {
        var defaults = Dir("defaults");
        var sandbox = Dir("inst/config");
        Write(defaults, "appsettings.json", """{ "A": { "Old": 1, "New": 2, "Deep": { "X": "x", "Y": "y" } }, "List": [1, 2, 3], "NewSection": { "K": true } }""");
        Write(defaults, "security.json", """{ "Raavi": { "Security": { "Limit": 5 } } }""");
        Write(sandbox, "appsettings.json", """{ "A": { "Old": 99, "Deep": { "X": "mine" } }, "List": [9], "Local": 1 }""");

        var preview = SandboxSync.Run(defaults, sandbox, apply: false);
        Assert.Contains(preview.Drift, d => d is { Kind: DriftKind.MissingKey, Key: "A:New" });
        Assert.Contains(preview.Drift, d => d is { Kind: DriftKind.MissingKey, Key: "A:Deep:Y" });
        Assert.Contains(preview.Drift, d => d is { Kind: DriftKind.MissingFile, File: "security.json" });
        Assert.Contains(preview.Drift, d => d is { Kind: DriftKind.UnknownKey, Key: "Local" });
        Assert.Empty(preview.ChangedFiles);
        Assert.DoesNotContain("New", File.ReadAllText(Path.Combine(sandbox, "appsettings.json")));

        var applied = SandboxSync.Run(defaults, sandbox, apply: true, now: new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        Assert.Equal(["appsettings.json", "security.json"], applied.ChangedFiles.Order().ToArray());
        var after = JsonNode.Parse(File.ReadAllText(Path.Combine(sandbox, "appsettings.json")))!;
        Assert.Equal(99, (int)after["A"]!["Old"]!);
        Assert.Equal(2, (int)after["A"]!["New"]!);
        Assert.Equal("mine", (string)after["A"]!["Deep"]!["X"]!);
        Assert.Equal("y", (string)after["A"]!["Deep"]!["Y"]!);
        Assert.Equal([9], after["List"]!.AsArray().Select(n => (int)n!).ToArray()); // arrays are one value, never merged
        Assert.Equal(1, (int)after["Local"]!); // local extras are kept
        Assert.True((bool)after["NewSection"]!["K"]!);
        Assert.True(File.Exists(Path.Combine(sandbox, "security.json")));
        Assert.NotNull(applied.BackupDirectory);
        Assert.Contains("\"Old\": 99", File.ReadAllText(Path.Combine(applied.BackupDirectory!, "appsettings.json"))); // the original is kept
        Assert.StartsWith(Path.Combine(_root, "inst", "backups"), applied.BackupDirectory);

        var second = SandboxSync.Run(defaults, sandbox, apply: true);
        Assert.Empty(second.ChangedFiles); // idempotent
        Assert.DoesNotContain(second.Drift, d => d.Kind != DriftKind.UnknownKey);
    }

    [Fact]
    public void Sync_refuses_to_rewrite_a_file_that_is_not_valid_json()
    {
        var defaults = Dir("defaults");
        var sandbox = Dir("inst/config");
        Write(defaults, "appsettings.json", """{ "A": 1 }""");
        Write(sandbox, "appsettings.json", "{ not json");

        Assert.Throws<InvalidOperationException>(() => SandboxSync.Run(defaults, sandbox, apply: true));
        Assert.Equal("{ not json", File.ReadAllText(Path.Combine(sandbox, "appsettings.json")));
    }

    [Fact]
    public void Shipped_scalar_defaults_apply_when_the_sandbox_lacks_them_but_arrays_do_not_leak()
    {
        var defaults = Dir("sandbox-defaults/config");
        var sandbox = Dir("sandbox-defaults-host/sandbox/config");
        Write(defaults, "appsettings.json", """{ "Raavi": { "New": { "Limit": 7 }, "Kept": 1, "Hosts": ["a", "b", "c"] } }""");
        Write(sandbox, "appsettings.json", """{ "Raavi": { "Kept": 2, "Hosts": ["mine"] } }""");
        Environment.SetEnvironmentVariable("RAAVI_SANDBOX_DEFAULTS_PATH", defaults);
        try
        {
            var config = new ConfigurationManager();
            config.AddRaaviSandboxConfiguration(new Env(Path.Combine(_root, "x"), "Production"), explicitPath: sandbox);

            Assert.Equal("7", config["Raavi:New:Limit"]);
            Assert.Equal("2", config["Raavi:Kept"]);
            Assert.Equal(["mine"], config.GetSection("Raavi:Hosts").Get<string[]>() ?? []);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RAAVI_SANDBOX_DEFAULTS_PATH", null);
        }
    }

    [Fact]
    public void State_paths_are_resolved_under_the_sandbox_root_not_the_working_directory()
    {
        var sandbox = Dir("inst2/config");
        Write(sandbox, "appsettings.json", """{ "Raavi": { "Email": { "PickupDirectory": "mail" }, "Collaboration": { "SigningKeyPath": "collab.pem" } } }""");
        var config = new ConfigurationManager();

        config.AddRaaviSandboxConfiguration(new Env(Path.Combine(_root, "x"), "Production"), explicitPath: sandbox);

        var root = Path.Combine(_root, "inst2");
        Assert.Equal(root, config["Raavi:Sandbox:Root"]);
        Assert.Equal(Path.Combine(root, "data", "keys"), config["Raavi:Security:DataProtection:KeyRingPath"]); // empty -> default
        Assert.Equal(Path.Combine(root, "data", "mail"), config["Raavi:Email:PickupDirectory"]);
        Assert.Equal(Path.Combine(root, "secrets", "collab.pem"), config["Raavi:Collaboration:SigningKeyPath"]);
    }

    [Fact]
    public void Absolute_state_paths_are_left_alone()
    {
        var sandbox = Dir("inst3/config");
        var keys = Path.Combine(_root, "elsewhere", "keys");
        Write(sandbox, "appsettings.json", System.Text.Json.JsonSerializer.Serialize(new { Raavi = new { Security = new { DataProtection = new { KeyRingPath = keys } } } }));
        var config = new ConfigurationManager();

        config.AddRaaviSandboxConfiguration(new Env(Path.Combine(_root, "x"), "Production"), explicitPath: sandbox);

        Assert.Equal(keys, config["Raavi:Security:DataProtection:KeyRingPath"]);
    }

    private sealed class Env(string contentRoot, string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Raavi.Tests";

        public string ContentRootPath { get; set; } = contentRoot;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
