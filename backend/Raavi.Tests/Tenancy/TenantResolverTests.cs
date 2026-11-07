using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Domain.Common;
using Raavi.Tenancy.Resolution;

namespace Raavi.Tests.Tenancy;

public sealed class TenantResolverTests
{
    private static readonly TenantSnapshot Acme = new(Guid.NewGuid(), "ACME", "acme", TenantStatus.Active, "اکمی", "Acme");
    private static readonly TenantSnapshot Beta = new(Guid.NewGuid(), "BETA", "beta", TenantStatus.Active, "بتا", "Beta");
    private static readonly TenantSnapshot Frozen = new(Guid.NewGuid(), "FRZN", "frozen", TenantStatus.Suspended, "یخ", "Frozen");

    private static TenantResolver Resolver()
    {
        var options = new HostingOptions { SystemHost = "admin.raavi.ir" };
        options.TenantBaseDomains.Add("raavi.ir");
        options.TenantBaseDomains.Add("wiki.local");
        new HostingOptionsSetup().PostConfigure(null, options);
        return new TenantResolver(new FakeDirectory(), Options.Create(options));
    }

    [Fact]
    public async Task System_host_establishes_no_tenant()
    {
        var r = await Resolver().ResolveAsync("admin.raavi.ir", default);
        Assert.Equal(TenantResolutionOutcome.SystemHost, r.Outcome);
        Assert.Null(r.Tenant);
    }

    [Theory]
    [InlineData("acme.raavi.ir")]
    [InlineData("acme.wiki.local")]
    public async Task Subdomain_of_any_base_domain_resolves(string host)
    {
        var r = await Resolver().ResolveAsync(host, default);
        Assert.Equal(TenantResolutionOutcome.Resolved, r.Outcome);
        Assert.Equal(Acme.Id, r.Tenant!.TenantId);
        Assert.Equal(TenantResolutionSource.Subdomain, r.Tenant.ResolvedBy);
    }

    [Fact]
    public async Task Verified_custom_domain_resolves()
    {
        var r = await Resolver().ResolveAsync("knowledge.beta.example", default);
        Assert.Equal(Beta.Id, r.Tenant!.TenantId);
        Assert.Equal(TenantResolutionSource.CustomDomain, r.Tenant.ResolvedBy);
    }

    [Theory]
    [InlineData("frozen.raavi.ir", "tenant_suspended")]
    [InlineData("frozen.example", "tenant_suspended")]
    [InlineData("ghost.raavi.ir", "unknown_slug")]
    [InlineData("a.acme.raavi.ir", "not_a_tenant_host")]
    [InlineData("raavi.ir", "not_a_tenant_host")]
    [InlineData("unknown.example", "unknown_host")]
    public async Task Rejects_inactive_unknown_and_nested_hosts(string host, string reason)
    {
        var r = await Resolver().ResolveAsync(host, default);
        Assert.Equal(TenantResolutionOutcome.Rejected, r.Outcome);
        Assert.Equal(reason, r.ReasonCode);
        Assert.Null(r.Tenant);
    }

    [Fact]
    public async Task Platform_hosts_are_never_looked_up_as_custom_domains()
    {
        // "acme.raavi.ir" is registered in the fake directory as a custom domain of Beta; it must still resolve by slug to Acme.
        var r = await Resolver().ResolveAsync("acme.raavi.ir", default);
        Assert.Equal(Acme.Id, r.Tenant!.TenantId);
    }

    [Fact]
    public void Invalid_hosting_configuration_fails_fast()
    {
        var options = new HostingOptions { SystemHost = "not a host" };
        options.TenantBaseDomains.Add("raavi.ir");
        Assert.Throws<OptionsValidationException>(() => new HostingOptionsSetup().PostConfigure(null, options));

        var noBase = new HostingOptions { SystemHost = "admin.raavi.ir" };
        Assert.Throws<OptionsValidationException>(() => new HostingOptionsSetup().PostConfigure(null, noBase));
    }

    private sealed class FakeDirectory : ITenantDirectory
    {
        private static readonly TenantSnapshot[] All = [Acme, Beta, Frozen];

        public ValueTask<TenantSnapshot?> FindByIdAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(All.FirstOrDefault(t => t.Id == tenantId));

        public ValueTask<TenantSnapshot?> FindByVerifiedDomainAsync(string normalizedHost, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(normalizedHost switch
            {
                "knowledge.beta.example" or "acme.raavi.ir" => Beta,
                "frozen.example" => Frozen,
                _ => null,
            });

        public ValueTask<TenantSnapshot?> FindBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(All.FirstOrDefault(t => t.Slug == slug));

        public ValueTask<TenantSnapshot?> FindByCodeAsync(string code, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(All.FirstOrDefault(t => t.Code == code));

        public ValueTask InvalidateAsync(Guid tenantId, IEnumerable<string>? extraHosts = null, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
