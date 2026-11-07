using Raavi.Tenancy.Resolution;

namespace Raavi.Tests.Tenancy;

public sealed class TenantHostTests
{
    [Theory]
    [InlineData("Tenant1.Raavi.IR", "tenant1.raavi.ir")]
    [InlineData("tenant1.raavi.ir:443", "tenant1.raavi.ir")]
    [InlineData("tenant1.raavi.ir.", "tenant1.raavi.ir")]
    [InlineData("  wiki.company1.com  ", "wiki.company1.com")]
    public void Normalizes_valid_hosts(string raw, string expected)
    {
        Assert.True(TenantHost.TryNormalize(raw, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void Converts_internationalized_domain_to_punycode()
    {
        Assert.True(TenantHost.TryNormalize("دانش.Example.ir", out var normalized));
        Assert.StartsWith("xn--", normalized, StringComparison.Ordinal);
        Assert.EndsWith(".example.ir", normalized, StringComparison.Ordinal);
        Assert.True(TenantHost.TryNormalize(normalized, out var again));
        Assert.Equal(normalized, again);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0")]
    [InlineData("[::1]:8080")]
    [InlineData("tenant1.raavi.ir:")]
    [InlineData("tenant1.raavi.ir:99999")]
    [InlineData("tenant1..raavi.ir")]
    [InlineData("-bad.raavi.ir")]
    [InlineData("bad-.raavi.ir")]
    [InlineData("under_score.raavi.ir")]
    [InlineData("evil.com/raavi.ir")]
    [InlineData("evil.com@tenant1.raavi.ir")]
    [InlineData("tenant1.raavi.ir\r\nX-Injected: 1")]
    public void Rejects_invalid_or_hostile_hosts(string? raw)
    {
        Assert.False(TenantHost.TryNormalize(raw, out _));
    }

    [Fact]
    public void Rejects_label_longer_than_63()
    {
        Assert.False(TenantHost.TryNormalize(new string('a', 64) + ".raavi.ir", out _));
    }

    [Theory]
    [InlineData("tenant1.raavi.ir", "raavi.ir", true, "tenant1")]
    [InlineData("a.b.raavi.ir", "raavi.ir", false, "")]
    [InlineData("raavi.ir", "raavi.ir", false, "")]
    [InlineData("tenant1.notraavi.ir", "raavi.ir", false, "")]
    [InlineData("tenant1raavi.ir", "raavi.ir", false, "")]
    public void Extracts_single_label_subdomain_only(string host, string baseDomain, bool ok, string slug)
    {
        Assert.Equal(ok, TenantHost.TryGetSubdomainSlug(host, baseDomain, out var actual));
        Assert.Equal(slug, actual);
    }

    [Theory]
    [InlineData("acme", true)]
    [InlineData("tenant-2", true)]
    [InlineData("a", false)]
    [InlineData("Acme", false)]
    [InlineData("xn--abc", false)]
    [InlineData("-acme", false)]
    [InlineData("ac.me", false)]
    public void Validates_slugs(string slug, bool expected)
    {
        Assert.Equal(expected, TenantHost.IsValidSlug(slug));
    }
}
