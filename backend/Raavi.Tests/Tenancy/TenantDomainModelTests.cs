using Raavi.Domain.Common;
using Raavi.Tenancy.Domain;

namespace Raavi.Tests.Tenancy;

public sealed class TenantDomainModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("t001", "acme", "T001")]
    [InlineData(" abc ", "my-org", "ABC")]
    public void Creates_tenant_with_normalized_code_in_provisioning(string code, string slug, string expectedCode)
    {
        var t = Tenant.Create(code, slug, "سازمان", "Org", Now);
        Assert.Equal(expectedCode, t.Code);
        Assert.Equal(TenantStatus.Provisioning, t.Status);
    }

    [Theory]
    [InlineData("1AB", "acme", "tenant.code_invalid")]
    [InlineData("AB", "acme", "tenant.code_invalid")]
    [InlineData("T-01", "acme", "tenant.code_invalid")]
    [InlineData("T001", "Ac me", "tenant.slug_invalid")]
    [InlineData("T001", "www", "tenant.slug_reserved")]
    [InlineData("T001", "xn--abc", "tenant.slug_invalid")]
    public void Rejects_invalid_codes_and_slugs(string code, string slug, string error)
    {
        var ex = Assert.Throws<DomainException>(() => Tenant.Create(code, slug, "a", "a", Now));
        Assert.Equal(error, ex.Code);
    }

    [Fact]
    public void Lifecycle_transitions_are_enforced()
    {
        var t = Tenant.Create("T001", "acme", "a", "a", Now);
        Assert.Throws<DomainException>(() => t.Suspend("x", Now));
        t.Activate(Now);
        Assert.Throws<DomainException>(() => t.Suspend(" ", Now));
        Assert.Equal(TenantStatus.Active, t.Status); // a rejected transition must not change state
        t.Suspend("billing", Now);
        Assert.Equal(TenantStatus.Suspended, t.Status);
        t.Deactivate("closed", Now);
        Assert.Equal(TenantStatus.Deactivated, t.Status);
        t.Activate(Now);
        Assert.Equal(TenantStatus.Active, t.Status);
        Assert.Null(t.StatusReason);
    }

    [Fact]
    public void Verified_domain_survives_transient_failures_then_is_lost()
    {
        var d = TenantDomain.Create(Guid.NewGuid(), "wiki.example.com", Now);
        Assert.False(d.Resolves);
        Assert.Equal("_raavi-verify.wiki.example.com", d.TxtRecordName("_raavi-verify"));
        Assert.StartsWith("raavi-verify=", d.ExpectedTxtValue, StringComparison.Ordinal);

        d.RecordVerificationSuccess(Now);
        d.MakePrimary();
        Assert.True(d.Resolves);

        d.RecordVerificationFailure(Now);
        d.RecordVerificationFailure(Now);
        Assert.True(d.Resolves);
        d.RecordVerificationFailure(Now);
        Assert.False(d.Resolves);
        Assert.Equal(TenantDomainStatus.VerificationLost, d.Status);
        Assert.False(d.IsPrimary);
    }

    [Fact]
    public void Unverified_domain_cannot_be_primary_and_removed_domain_is_frozen()
    {
        var d = TenantDomain.Create(Guid.NewGuid(), "wiki.example.com", Now);
        Assert.Throws<DomainException>(d.MakePrimary);
        d.Remove(Now);
        Assert.Throws<DomainException>(() => d.RecordVerificationSuccess(Now));
    }

    [Fact]
    public void Branding_accepts_only_hex_colors()
    {
        var b = TenantBranding.CreateEmpty();
        Assert.Throws<DomainException>(() => b.Update("expression(alert(1))", null, null, null));
        Assert.Throws<DomainException>(() => b.Update("#12345", null, null, null));
        b.Update("#ABCDEF", " ", "  سلام  ", null);
        Assert.Equal("#abcdef", b.PrimaryColor);
        Assert.Null(b.SecondaryColor);
        Assert.Equal("سلام", b.WelcomeMessageFa);
    }

    [Fact]
    public void Settings_validate_locale_calendar_and_iana_time_zone()
    {
        Assert.Throws<DomainException>(() => TenantSettings.CreateDefault("de", "Asia/Tehran", "persian"));
        Assert.Throws<DomainException>(() => TenantSettings.CreateDefault("fa", "Asia/Tehran", "lunar"));
        Assert.Throws<DomainException>(() => TenantSettings.CreateDefault("fa", "Nowhere/City", "persian"));
        var s = TenantSettings.CreateDefault("en", "Europe/Berlin", "gregorian");
        Assert.Equal("Europe/Berlin", s.TimeZone);
    }
}
