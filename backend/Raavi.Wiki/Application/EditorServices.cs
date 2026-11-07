using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Editor;
using Raavi.Wiki.Persistence;

namespace Raavi.Wiki.Application;

public sealed record AttachmentDto(Guid Id, Guid ContentId, string FileName, string MediaType, long Size, bool IsImage, DateTimeOffset CreatedAt);

public sealed record AttachmentDownload(string FileName, string MediaType, bool Inline, byte[] Data, string Sha256);

public sealed record EditorSchemaDto(string Schema, int SchemaVersion, int MaxBytes, IReadOnlyList<EditorExtensionDto> Extensions);

public sealed record EditorExtensionDto(string Id, string Name, int Version, string Kind, bool Insertable);

/// <summary>Attachments-lite: upload to content the caller can edit, download from content the caller can view.</summary>
public sealed class AttachmentService(WikiDbContext db, PageService pages, ICurrentUser current, IClock clock, IConfiguration configuration)
{
    public static readonly Error TooLarge = new("attachment.too_large", "The file is too large.", ErrorKind.Validation);
    public static readonly Error Empty = new("attachment.empty", "The file is empty.", ErrorKind.Validation);
    public static readonly Error NotFound = new("attachment.not_found", "Attachment not found.", ErrorKind.NotFound);

    public long MaxBytes => configuration.GetValue<long?>("Raavi:Attachments:MaxBytes") ?? 10 * 1024 * 1024;

    public async Task<Result<AttachmentDto>> UploadAsync(Guid contentId, string fileName, Stream content, long? declaredLength, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var loaded = await pages.LoadAsync(contentId, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<AttachmentDto>.Failure(loaded.Error!);
        }

        if (!loaded.Value.Rights.CanEdit)
        {
            return Result<AttachmentDto>.Failure(WikiErrors.Forbidden);
        }

        if (declaredLength > MaxBytes)
        {
            return Result<AttachmentDto>.Failure(TooLarge);
        }

        // Bounded copy: never trust the declared length.
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                return Result<AttachmentDto>.Failure(TooLarge);
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            return Result<AttachmentDto>.Failure(Empty);
        }

        var data = buffer.ToArray();
        var attachment = Attachment.Create(contentId, fileName, data, Convert.ToHexStringLower(SHA256.HashData(data)), current.UserId, clock.UtcNow);
        db.Attachments.Add(attachment);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<AttachmentDto>.Success(ToDto(attachment));
    }

    public async Task<Result<AttachmentDownload>> DownloadAsync(Guid id, CancellationToken ct)
    {
        var meta = await db.Attachments.AsNoTracking().Where(a => a.Id == id).Select(a => new { a.ContentId }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (meta is null)
        {
            return Result<AttachmentDownload>.Failure(NotFound);
        }

        // Same visibility as the owning content (space grants + inherited restrictions); otherwise "not found".
        var loaded = await pages.LoadAsync(meta.ContentId, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<AttachmentDownload>.Failure(NotFound);
        }

        var a = await db.Attachments.AsNoTracking().FirstAsync(x => x.Id == id, ct).ConfigureAwait(false);
        return Result<AttachmentDownload>.Success(new AttachmentDownload(a.FileName, a.MediaType, a.IsImage, a.Data, a.Sha256));
    }

    public async Task<Result<IReadOnlyList<AttachmentDto>>> ListAsync(Guid contentId, CancellationToken ct)
    {
        var loaded = await pages.LoadAsync(contentId, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<IReadOnlyList<AttachmentDto>>.Failure(loaded.Error!);
        }

        var rows = await db.Attachments.AsNoTracking().Where(a => a.ContentId == contentId).OrderBy(a => a.CreatedAt)
            .Select(a => new AttachmentDto(a.Id, a.ContentId, a.FileName, a.MediaType, a.Size, a.MediaType.StartsWith("image/"), a.CreatedAt))
            .ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<AttachmentDto>>.Success(rows);
    }

    private static AttachmentDto ToDto(Attachment a) => new(a.Id, a.ContentId, a.FileName, a.MediaType, a.Size, a.IsImage, a.CreatedAt);
}

/// <summary>Editor schema discovery and server-rendered HTML.</summary>
public sealed class EditorService(WikiDbContext db, PageService pages, IPrincipalDirectory directory, DocumentHtmlRenderer renderer)
{
    public static EditorSchemaDto Schema()
    {
        var s = EditorSchema.Current;
        return new EditorSchemaDto(s.Info.Schema, s.Info.SchemaVersion, s.Info.MaxBytes,
            s.Manifests.OrderBy(m => m.Id, StringComparer.Ordinal).Select(m => new EditorExtensionDto(m.Id, m.Name, m.Version, m.Kind, m.Insertable)).ToList());
    }

    /// <summary>Sanitized HTML of the current version for a viewer (mention names resolved within the tenant).</summary>
    public async Task<Result<string>> RenderAsync(Guid contentId, CancellationToken ct)
    {
        var loaded = await pages.LoadAsync(contentId, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<string>.Failure(loaded.Error!);
        }

        if (loaded.Value.Node.CurrentVersionId is not { } versionId)
        {
            return Result<string>.Success(string.Empty);
        }

        var json = await db.PageVersions.AsNoTracking().Where(v => v.Id == versionId).Select(v => v.ContentJson).FirstAsync(ct).ConfigureAwait(false);
        var document = RaaviDocument.Parse(json);
        var mentioned = new List<Guid>();
        CollectMentions(document.Doc, mentioned);
        var names = await directory.GetUserNamesAsync(mentioned, ct).ConfigureAwait(false);
        return Result<string>.Success(renderer.Render(document.Doc, names));
    }

    private static void CollectMentions(JsonObject node, List<Guid> ids)
    {
        if (node["type"]?.GetValue<string>() == "mention" && Guid.TryParse(node["attrs"]?["id"]?.GetValue<string>(), out var id))
        {
            ids.Add(id);
        }

        if (node["content"] is JsonArray children)
        {
            foreach (var c in children.OfType<JsonObject>())
            {
                CollectMentions(c, ids);
            }
        }
    }
}
