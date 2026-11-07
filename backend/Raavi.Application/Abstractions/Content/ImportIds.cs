using System.Security.Cryptography;
using System.Text;

namespace Raavi.Application.Abstractions.Content;

/// <summary>
/// Deterministic identifiers for imported items. An attachment's id is derived from where it came from, so a converted page body
/// can reference the attachment before it exists, and re-importing the same attachment always yields the same row (idempotency).
/// </summary>
public static class ImportIds
{
    /// <summary>
    /// Id of the attachment <paramref name="fileName"/> of the source page <paramref name="pageSourceId"/> in workspace <paramref name="tenantId"/>.
    /// The workspace is part of the hash: attachment keys are global, so two workspaces importing the same source must not collide
    /// (and one workspace can never learn another's ids).
    /// </summary>
    public static Guid Attachment(Guid tenantId, string sourceSystem, string pageSourceId, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(pageSourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"attachment|{tenantId:N}|{sourceSystem}|{pageSourceId}|{fileName}"));
        Span<byte> b = hash.AsSpan(0, 16).ToArray();
        b[7] = (byte)((b[7] & 0x0F) | 0x50); // version 5 style (name-based)
        b[8] = (byte)((b[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(b);
    }
}
