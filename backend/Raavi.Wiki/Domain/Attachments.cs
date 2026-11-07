using Raavi.Domain.Common;

namespace Raavi.Wiki.Domain;

/// <summary>
/// Attachments-lite (Phase 4): a file owned by one content item, stored in SQL Server. Phase 7 moves the bytes to object
/// storage (ADR-014) and adds versions, quotas, scanning and previews; the id and the download endpoint stay stable.
/// </summary>
public sealed class Attachment : Entity, ITenantOwned
{
    public const int MaxFileNameLength = 255;

    private Attachment() { }

    public Guid TenantId { get; private set; }

    public Guid ContentId { get; private set; }

    public string FileName { get; private set; } = string.Empty;

    /// <summary>Media type determined by content sniffing on upload — never the client's Content-Type.</summary>
    public string MediaType { get; private set; } = "application/octet-stream";

    public long Size { get; private set; }

    public string Sha256 { get; private set; } = string.Empty;

    public byte[] Data { get; private set; } = [];

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsImage => MediaType.StartsWith("image/", StringComparison.Ordinal);

    public static Attachment Create(Guid contentId, string fileName, byte[] data, string sha256, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new Attachment
        {
            ContentId = contentId,
            FileName = CleanFileName(fileName),
            MediaType = Sniff(data),
            Size = data.LongLength,
            Sha256 = sha256,
            Data = data,
            CreatedBy = userId,
            CreatedAt = now,
        };
    }

    /// <summary>Strips paths and control characters; keeps Unicode (Persian) names.</summary>
    /// <summary>An imported file: keeps its original time and the deterministic id the converted page bodies already reference.</summary>
    public static Attachment Import(Guid id, Guid contentId, string fileName, byte[] data, string sha256, Guid userId, DateTimeOffset createdAt)
    {
        var a = Create(contentId, fileName, data, sha256, userId, createdAt);
        return new Attachment
        {
            Id = id,
            ContentId = a.ContentId,
            FileName = a.FileName,
            MediaType = a.MediaType,
            Size = a.Size,
            Sha256 = a.Sha256,
            Data = a.Data,
            CreatedBy = a.CreatedBy,
            CreatedAt = a.CreatedAt,
        };
    }

    /// <summary>The source delivers different bytes for an imported file (re-conversion refreshes it); the id and page stay.</summary>
    public void ReplaceImportedContent(byte[] data, string sha256)
    {
        ArgumentNullException.ThrowIfNull(data);
        Data = data;
        Size = data.LongLength;
        Sha256 = sha256;
        MediaType = Sniff(data);
    }

    public static string CleanFileName(string? name)
    {
        var n = Path.GetFileName((name ?? string.Empty).Replace('\\', '/'));
        n = new string(n.Where(c => !char.IsControl(c) && c is not ('"' or '<' or '>' or '|' or ':' or '*' or '?')).ToArray()).Trim().Trim('.');
        if (n.Length == 0)
        {
            n = "file";
        }

        return n.Length > MaxFileNameLength ? n[..MaxFileNameLength] : n;
    }

    /// <summary>Magic-byte detection for the formats that are displayed inline. SVG is deliberately not an image (scripts).</summary>
    public static string Sniff(ReadOnlySpan<byte> d)
    {
        if (d.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }

        if (d.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }

        if (d.StartsWith("GIF87a"u8) || d.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }

        if (d.Length >= 12 && d[..4].SequenceEqual("RIFF"u8) && d[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        if (d.StartsWith("%PDF-"u8))
        {
            return "application/pdf";
        }

        return "application/octet-stream";
    }
}
