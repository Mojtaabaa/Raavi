using System.Reflection;

namespace Raavi.Tests.Architecture;

/// <summary>Enforces the module dependency rules in docs/architecture/MODULE_BOUNDARIES.md §2.</summary>
public sealed class DependencyRulesTests
{
    private static readonly string[] FeatureModules =
        ["Raavi.Tenancy", "Raavi.Identity", "Raavi.Wiki", "Raavi.Migration", "Raavi.Search", "Raavi.AI", "Raavi.Integrations"];

    private static string[] RaaviReferences(string assemblyName) =>
        Assembly.Load(assemblyName).GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("Raavi.", StringComparison.Ordinal))
            .ToArray();

    private static string[] AllReferences(string assemblyName) =>
        Assembly.Load(assemblyName).GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    [Fact]
    public void Domain_depends_on_nothing_inside_raavi_and_no_infrastructure_frameworks()
    {
        Assert.Empty(RaaviReferences("Raavi.Domain"));
        Assert.DoesNotContain(AllReferences("Raavi.Domain"), n =>
            n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || n.StartsWith("Microsoft.Data.SqlClient", StringComparison.Ordinal)
            || n.StartsWith("StackExchange.Redis", StringComparison.Ordinal));
    }

    [Fact]
    public void Application_depends_only_on_domain()
    {
        Assert.All(RaaviReferences("Raavi.Application"), n => Assert.Equal("Raavi.Domain", n));
        Assert.DoesNotContain(AllReferences("Raavi.Application"), n =>
            n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_feature_modules_or_hosts()
    {
        var refs = RaaviReferences("Raavi.Infrastructure");
        Assert.DoesNotContain(refs, FeatureModules.Contains);
        Assert.DoesNotContain("Raavi.Api", refs);
        Assert.DoesNotContain("Raavi.Workers", refs);
    }

    [Theory]
    [InlineData("Raavi.Tenancy")]
    [InlineData("Raavi.Identity")]
    [InlineData("Raavi.Wiki")]
    [InlineData("Raavi.Migration")]
    [InlineData("Raavi.Search")]
    [InlineData("Raavi.AI")]
    [InlineData("Raavi.Integrations")]
    public void Feature_modules_do_not_reference_each_other_or_hosts(string module)
    {
        var refs = RaaviReferences(module);
        Assert.DoesNotContain(refs, r => FeatureModules.Contains(r) && r != module);
        Assert.DoesNotContain("Raavi.Api", refs);
        Assert.DoesNotContain("Raavi.Workers", refs);
    }
}
