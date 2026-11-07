using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Raavi.Infrastructure.Configuration;

namespace Raavi.Tests.Configuration;

public sealed class SandboxConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "raavi-tests", Guid.NewGuid().ToString("N"));

    public SandboxConfigurationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Sandbox(params (string File, string Json)[] files)
    {
        var dir = Path.Combine(_root, "content", "sandbox", "config");
        Directory.CreateDirectory(dir);
        foreach (var (file, json) in files)
        {
            File.WriteAllText(Path.Combine(dir, file), json);
        }

        return dir;
    }

    private TestEnvironment Env(string name = "Production") => new(Path.Combine(_root, "content"), name);

    [Fact]
    public void Module_file_overrides_appsettings_and_environment_file_overrides_module_file()
    {
        var path = Sandbox(
            ("appsettings.json", """{ "Raavi": { "Search": { "IndexPrefix": "base" } } }"""),
            ("search.json", """{ "Raavi": { "Search": { "IndexPrefix": "module" } } }"""),
            ("search.Staging.json", """{ "Raavi": { "Search": { "IndexPrefix": "staging" } } }"""));
        var config = new ConfigurationManager();

        config.AddRaaviSandboxConfiguration(Env("Staging"), explicitPath: path);

        Assert.Equal("staging", config["Raavi:Search:IndexPrefix"]);
        Assert.Equal(path, config["Raavi:SandboxPath"]);
    }

    [Fact]
    public void Command_line_overrides_files()
    {
        var path = Sandbox(("appsettings.json", """{ "Raavi": { "X": "file" } }"""));
        var config = new ConfigurationManager();

        config.AddRaaviSandboxConfiguration(Env(), ["--Raavi:X=cli"], path);

        Assert.Equal("cli", config["Raavi:X"]);
    }

    [Fact]
    public void Missing_appsettings_fails_fast()
    {
        var path = Sandbox(("search.json", "{}"));
        var config = new ConfigurationManager();

        var ex = Assert.Throws<InvalidOperationException>(() => config.AddRaaviSandboxConfiguration(Env(), explicitPath: path));
        Assert.Contains("appsettings.json", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sandbox_inside_web_root_is_rejected()
    {
        var dir = Path.Combine(_root, "content", "wwwroot", "config");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "appsettings.json"), "{}");
        var config = new ConfigurationManager();

        Assert.Throws<InvalidOperationException>(() => config.AddRaaviSandboxConfiguration(Env(), explicitPath: dir));
    }

    [Fact]
    public void Path_is_discovered_by_walking_up_from_content_root()
    {
        var expected = Sandbox(("appsettings.json", "{}"));
        var nested = Path.Combine(_root, "content", "backend", "Raavi.Api");
        Directory.CreateDirectory(nested);

        Assert.Equal(expected, SandboxConfiguration.ResolvePath(nested, explicitPath: null));
    }

    [Fact]
    public void Repository_sandbox_contains_every_known_module_file()
    {
        var path = SandboxConfiguration.ResolvePath(AppContext.BaseDirectory, explicitPath: null);
        Assert.True(File.Exists(Path.Combine(path, "appsettings.json")));
        foreach (var module in SandboxConfiguration.ModuleFiles)
        {
            Assert.True(File.Exists(Path.Combine(path, module + ".json")), $"missing {module}.json");
        }
    }

    private sealed class TestEnvironment(string contentRoot, string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Raavi.Tests";

        public string ContentRootPath { get; set; } = contentRoot;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
