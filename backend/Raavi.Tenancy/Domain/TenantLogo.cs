using Raavi.Domain.Common;

namespace Raavi.Tenancy.Domain;

/// <summary>The square logo of a workspace (PNG, JPEG or WebP, already cropped by the browser). Tenant-owned, RLS-protected; kept apart from <see cref="TenantBranding"/> so branding reads never load image bytes.</summary>
public sealed class TenantLogo : Entity, ITenantOwned
{
    private TenantLogo() { }

    public Guid TenantId { get; private set; }

    public string ContentType { get; private set; } = string.Empty;

    public byte[] Data { get; private set; } = [];

    public int Version { get; private set; } = 1;

    public static TenantLogo Create(string contentType, byte[] data) => new() { ContentType = contentType, Data = data };

    public void Replace(string contentType, byte[] data)
    {
        ContentType = contentType;
        Data = data;
        Version++;
    }
}
