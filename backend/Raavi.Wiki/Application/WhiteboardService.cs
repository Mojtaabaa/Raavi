using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Domain.Common;
using Raavi.Infrastructure.Http;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki.Application;

public sealed record BoardElementDto(
    string Id, string Type, double X, double Y, double W, double H, double Rotation, string Stroke, string? Fill, int StrokeWidth, string Text, int FontSize, bool Rtl, double[] Points,
    double CornerRadius, Guid? AttachmentId, string Alt, bool Locked, bool Hidden, string? ParentId,
    string? FromId, string? ToId, string FromPort, string ToPort, string Route, bool StartArrow, bool EndArrow,
    string[]? Children, int Rows, int Cols, string[]? Cells, double[]? ColWidths);

public sealed record WhiteboardDto(
    Guid Id, string SpaceKey, string SpaceName, string Title, string? Description, int ElementCount, BoardElementDto[] Elements,
    string Background, string GridStyle, string? CreatedByName, string? UpdatedByName, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt, int Revision, bool CanEdit, bool Favorited);

public sealed record BoardElementRequest(
    string? Id, string? Type, double? X, double? Y, double? W, double? H, double? Rotation, string? Stroke, string? Fill, int? StrokeWidth, string? Text, int? FontSize, bool? Rtl, double[]? Points,
    double? CornerRadius, Guid? AttachmentId, string? Alt, bool? Locked, bool? Hidden, string? ParentId,
    string? FromId, string? ToId, string? FromPort, string? ToPort, string? Route, bool? StartArrow, bool? EndArrow,
    string[]? Children, int? Rows, int? Cols, string[]? Cells, double[]? ColWidths);

public sealed record WhiteboardRequest(string? Title, string? Description, BoardElementRequest[]? Elements, string? Background, string? GridStyle);

public sealed record BoardTitleRequest(string? Title);

public sealed record BoardDescriptionRequest(string? Description);

public sealed record BoardMetaRequest(string? Description, string? Background, string? GridStyle);

public sealed record WhiteboardExport(string FileName, byte[] Data);

public sealed record WhiteboardAttachmentDto(Guid Id, Guid WhiteboardId, string FileName, string MediaType, long Size, bool IsImage, DateTimeOffset CreatedAt);

public sealed record WhiteboardAttachmentDownload(string FileName, string MediaType, bool IsImage, byte[] Data, string Sha256);

public sealed record WhiteboardRevisionDto(Guid Id, int BoardRevision, string Title, DateTimeOffset CreatedAt, string? CreatedByName);

public sealed record WhiteboardRevisionDetailDto(Guid Id, int BoardRevision, string Title, string? Description, string Background, string GridStyle, BoardElementDto[] Elements, DateTimeOffset CreatedAt, string? CreatedByName);

public sealed record WhiteboardTemplateDto(Guid Id, Guid? SpaceId, string Name, string? Description, bool IsSystem, DateTimeOffset CreatedAt);

public sealed record WhiteboardCommentDto(Guid Id, Guid? ParentId, string? ObjectId, double? X, double? Y, string Body, string? CreatedByName, DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt);

public sealed record WhiteboardCommentRequest(string? Body, Guid? ParentId, string? ObjectId, double? X, double? Y);

/// <summary>Space whiteboards: validated elements, ETag concurrency, attachments, favorites, revisions, SVG export.</summary>
public sealed class WhiteboardService(
    WikiDbContext db,
    SpaceService spaces,
    WikiAuthorizer authz,
    IPrincipalDirectory directory,
    ICurrentUser current,
    IClock clock,
    IConfiguration configuration,
    Raavi.Wiki.Collaboration.CollaborationClient collaboration)
{
    public static readonly Error NotFound = new("board.not_found", "Whiteboard not found.", ErrorKind.NotFound);
    public static readonly Error ElementInvalid = new("board.element_invalid", "An element is not valid.", ErrorKind.Validation);
    public static readonly Error AttachmentNotFound = new("board.attachment_not_found", "Attachment not found.", ErrorKind.NotFound);
    public static readonly Error AttachmentTooLarge = new("board.attachment_too_large", "The file is too large.", ErrorKind.Validation);
    public static readonly Error AttachmentEmpty = new("board.attachment_empty", "The file is empty.", ErrorKind.Validation);
    public static readonly Error AttachmentNotImage = new("board.attachment_not_image", "Whiteboard attachments must be images.", ErrorKind.Validation);

    public long MaxAttachmentBytes => configuration.GetValue<long?>("Raavi:Attachments:MaxBytes") ?? 10 * 1024 * 1024;

    public async Task<Result<IReadOnlyList<WhiteboardDto>>> ListAsync(string spaceKey, bool includeArchived, CancellationToken ct)
    {
        var space = await spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
        if (space is null)
        {
            return Result<IReadOnlyList<WhiteboardDto>>.Failure(WikiErrors.SpaceNotFound);
        }

        var query = db.Whiteboards.AsNoTracking().Where(b => b.SpaceId == space.Space.Id);
        if (!includeArchived)
        {
            query = query.Where(b => b.ArchivedAt == null);
        }

        var boards = await query.OrderBy(b => b.Title).Take(500).ToListAsync(ct).ConfigureAwait(false);
        var favIds = await db.WhiteboardFavorites.AsNoTracking().Where(f => f.UserId == current.UserId).Select(f => f.WhiteboardId).ToListAsync(ct).ConfigureAwait(false);
        var fav = favIds.ToHashSet();
        boards = boards.OrderByDescending(b => fav.Contains(b.Id)).ThenBy(b => b.Title, StringComparer.OrdinalIgnoreCase).ToList();
        var list = new List<WhiteboardDto>();
        foreach (var b in boards)
        {
            list.Add(await ToDtoAsync(b, space, includeElements: false, fav.Contains(b.Id), ct).ConfigureAwait(false));
        }

        return Result<IReadOnlyList<WhiteboardDto>>.Success(list);
    }

    public async Task<Result<WhiteboardDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<WhiteboardDto>.Failure(loaded.Error!);
        }

        var favorited = await db.WhiteboardFavorites.AsNoTracking().AnyAsync(f => f.WhiteboardId == id && f.UserId == current.UserId, ct).ConfigureAwait(false);
        return Result<WhiteboardDto>.Success(await ToDtoAsync(loaded.Value.Board, loaded.Value.Space, includeElements: true, favorited, ct).ConfigureAwait(false));
    }

    public async Task<Result<WhiteboardDto>> CreateAsync(string spaceKey, WhiteboardRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var space = await spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
        if (space is null)
        {
            return Result<WhiteboardDto>.Failure(WikiErrors.SpaceNotFound);
        }

        if (!space.CanWrite(PermissionAction.Edit))
        {
            return Result<WhiteboardDto>.Failure(space.Space.Status == SpaceStatus.Archived ? WikiErrors.SpaceArchived : WikiErrors.Forbidden);
        }

        var elements = Parse(request);
        if (!elements.IsSuccess)
        {
            return Result<WhiteboardDto>.Failure(elements.Error!);
        }

        var grid = ParseGrid(request.GridStyle);
        var board = Whiteboard.Create(space.Space.Id, request.Title, elements.Value, current.UserId, clock.UtcNow, request.Description, request.Background, grid);
        db.Whiteboards.Add(board);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await CaptureRevisionAsync(board, ct).ConfigureAwait(false);
        return Result<WhiteboardDto>.Success(await ToDtoAsync(board, space, includeElements: true, favorited: false, ct).ConfigureAwait(false));
    }

    public async Task<Result<WhiteboardDto>> UpdateAsync(Guid id, int expectedRevision, WhiteboardRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<WhiteboardDto>.Failure(loaded.Error!);
        }

        var (board, space) = loaded.Value;
        if (!space.CanWrite(PermissionAction.Edit))
        {
            return Result<WhiteboardDto>.Failure(WikiErrors.Forbidden);
        }

        if (board.Revision != expectedRevision)
        {
            return Result<WhiteboardDto>.Failure(ETags.PreconditionFailed);
        }

        var elements = Parse(request);
        if (!elements.IsSuccess)
        {
            return Result<WhiteboardDto>.Failure(elements.Error!);
        }

        var imageCheck = await ValidateImageAttachmentsAsync(board.Id, elements.Value, ct).ConfigureAwait(false);
        if (!imageCheck.IsSuccess)
        {
            return Result<WhiteboardDto>.Failure(imageCheck.Error!);
        }

        var grid = request.GridStyle is null ? board.GridStyle : ParseGrid(request.GridStyle);
        board.Update(request.Title, elements.Value, current.UserId, clock.UtcNow, request.Description ?? board.Description, request.Background ?? board.Background, grid);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await CaptureRevisionAsync(board, ct).ConfigureAwait(false);
        await collaboration.ResetAsync(new Raavi.Wiki.Collaboration.DocumentAddress(db.CurrentTenantId, id, Raavi.Wiki.Collaboration.DocumentKind.Board).Name, ct).ConfigureAwait(false);
        var favorited = await db.WhiteboardFavorites.AsNoTracking().AnyAsync(f => f.WhiteboardId == id && f.UserId == current.UserId, ct).ConfigureAwait(false);
        return Result<WhiteboardDto>.Success(await ToDtoAsync(board, space, includeElements: true, favorited, ct).ConfigureAwait(false));
    }

    public async Task<Result<WhiteboardDto>> RenameAsync(Guid id, BoardTitleRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<WhiteboardDto>.Failure(loaded.Error!);
        }

        var (board, space) = loaded.Value;
        if (!space.CanWrite(PermissionAction.Edit))
        {
            return Result<WhiteboardDto>.Failure(WikiErrors.Forbidden);
        }

        board.Rename(request.Title, current.UserId, clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var favorited = await db.WhiteboardFavorites.AsNoTracking().AnyAsync(f => f.WhiteboardId == id && f.UserId == current.UserId, ct).ConfigureAwait(false);
        return Result<WhiteboardDto>.Success(await ToDtoAsync(board, space, includeElements: true, favorited, ct).ConfigureAwait(false));
    }

    public async Task<Result<WhiteboardDto>> UpdateMetaAsync(Guid id, BoardMetaRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<WhiteboardDto>.Failure(loaded.Error!);
        }

        var (board, space) = loaded.Value;
        if (!space.CanWrite(PermissionAction.Edit))
        {
            return Result<WhiteboardDto>.Failure(WikiErrors.Forbidden);
        }

        if (request.Description is not null)
        {
            board.SetDescription(request.Description, current.UserId, clock.UtcNow);
        }

        if (request.Background is not null || request.GridStyle is not null)
        {
            board.SetAppearance(request.Background, request.GridStyle is null ? null : ParseGrid(request.GridStyle), current.UserId, clock.UtcNow);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var favorited = await db.WhiteboardFavorites.AsNoTracking().AnyAsync(f => f.WhiteboardId == id && f.UserId == current.UserId, ct).ConfigureAwait(false);
        return Result<WhiteboardDto>.Success(await ToDtoAsync(board, space, includeElements: true, favorited, ct).ConfigureAwait(false));
    }

    public async Task<Result<WhiteboardDto>> DuplicateAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<WhiteboardDto>.Failure(loaded.Error!);
        }

        var (board, space) = loaded.Value;
        if (!space.CanWrite(PermissionAction.Edit))
        {
            return Result<WhiteboardDto>.Failure(WikiErrors.Forbidden);
        }

        var copy = board.Duplicate(null, current.UserId, clock.UtcNow);
        db.Whiteboards.Add(copy);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await CaptureRevisionAsync(copy, ct).ConfigureAwait(false);
        return Result<WhiteboardDto>.Success(await ToDtoAsync(copy, space, includeElements: true, favorited: false, ct).ConfigureAwait(false));
    }

    public async Task<Result<WhiteboardDto>> ArchiveAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<WhiteboardDto>.Failure(loaded.Error!);
        }

        var (board, space) = loaded.Value;
        if (!space.CanWrite(PermissionAction.Edit))
        {
            return Result<WhiteboardDto>.Failure(WikiErrors.Forbidden);
        }

        board.Archive(clock.UtcNow, current.UserId);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var favorited = await db.WhiteboardFavorites.AsNoTracking().AnyAsync(f => f.WhiteboardId == id && f.UserId == current.UserId, ct).ConfigureAwait(false);
        return Result<WhiteboardDto>.Success(await ToDtoAsync(board, space, includeElements: false, favorited, ct).ConfigureAwait(false));
    }

    public async Task<Result<WhiteboardDto>> RestoreAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<WhiteboardDto>.Failure(loaded.Error!);
        }

        var (board, space) = loaded.Value;
        if (!space.CanWrite(PermissionAction.Edit))
        {
            return Result<WhiteboardDto>.Failure(WikiErrors.Forbidden);
        }

        board.Restore(current.UserId, clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var favorited = await db.WhiteboardFavorites.AsNoTracking().AnyAsync(f => f.WhiteboardId == id && f.UserId == current.UserId, ct).ConfigureAwait(false);
        return Result<WhiteboardDto>.Success(await ToDtoAsync(board, space, includeElements: false, favorited, ct).ConfigureAwait(false));
    }

    public async Task<Result> FavoriteAsync(Guid id, bool on, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result.Failure(loaded.Error!);
        }

        var existing = await db.WhiteboardFavorites.FirstOrDefaultAsync(f => f.WhiteboardId == id && f.UserId == current.UserId, ct).ConfigureAwait(false);
        if (on && existing is null)
        {
            db.WhiteboardFavorites.Add(WhiteboardFavorite.Create(current.UserId, id, clock.UtcNow));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        else if (!on && existing is not null)
        {
            db.WhiteboardFavorites.Remove(existing);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return Result.Success();
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result.Failure(loaded.Error!);
        }

        if (!loaded.Value.Space.CanWrite(PermissionAction.Edit))
        {
            return Result.Failure(WikiErrors.Forbidden);
        }

        db.Whiteboards.Remove(loaded.Value.Board);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result<WhiteboardExport>> ExportSvgAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<WhiteboardExport>.Failure(loaded.Error!);
        }

        var board = loaded.Value.Board;
        var svg = BoardSvgWriter.Write(board.Title, board.Elements, board.Background);
        var safe = new string(board.Title.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        return Result<WhiteboardExport>.Success(new WhiteboardExport($"whiteboard-{(safe.Length == 0 ? "board" : safe)}.svg", System.Text.Encoding.UTF8.GetBytes(svg)));
    }

    public async Task<Result<WhiteboardExport>> ExportJsonAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<WhiteboardExport>.Failure(loaded.Error!);
        }

        var board = loaded.Value.Board;
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            title = board.Title,
            description = board.Description,
            background = board.Background,
            gridStyle = board.GridStyle.ToString(),
            elements = board.Elements,
        }, BoardJson.Options);
        var safe = new string(board.Title.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        return Result<WhiteboardExport>.Success(new WhiteboardExport($"whiteboard-{(safe.Length == 0 ? "board" : safe)}.json", System.Text.Encoding.UTF8.GetBytes(payload)));
    }

    public async Task<Result<WhiteboardAttachmentDto>> UploadAttachmentAsync(Guid id, string fileName, Stream content, long? declaredLength, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<WhiteboardAttachmentDto>.Failure(loaded.Error!);
        }

        if (!loaded.Value.Space.CanWrite(PermissionAction.Edit))
        {
            return Result<WhiteboardAttachmentDto>.Failure(WikiErrors.Forbidden);
        }

        if (declaredLength > MaxAttachmentBytes)
        {
            return Result<WhiteboardAttachmentDto>.Failure(AttachmentTooLarge);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxAttachmentBytes)
            {
                return Result<WhiteboardAttachmentDto>.Failure(AttachmentTooLarge);
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            return Result<WhiteboardAttachmentDto>.Failure(AttachmentEmpty);
        }

        try
        {
            var data = buffer.ToArray();
            var attachment = WhiteboardAttachment.Create(id, fileName, data, current.UserId, clock.UtcNow);
            db.WhiteboardAttachments.Add(attachment);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<WhiteboardAttachmentDto>.Success(ToAttachmentDto(attachment));
        }
        catch (DomainException ex) when (ex.Code == "board.attachment_not_image")
        {
            return Result<WhiteboardAttachmentDto>.Failure(AttachmentNotImage);
        }
    }

    public async Task<Result<IReadOnlyList<WhiteboardAttachmentDto>>> ListAttachmentsAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<IReadOnlyList<WhiteboardAttachmentDto>>.Failure(loaded.Error!);
        }

        var rows = await db.WhiteboardAttachments.AsNoTracking().Where(a => a.WhiteboardId == id).OrderBy(a => a.CreatedAt)
            .Select(a => new WhiteboardAttachmentDto(a.Id, a.WhiteboardId, a.FileName, a.MediaType, a.Size, a.MediaType.StartsWith("image/"), a.CreatedAt))
            .ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<WhiteboardAttachmentDto>>.Success(rows);
    }

    public async Task<Result<WhiteboardAttachmentDownload>> DownloadAttachmentAsync(Guid attachmentId, CancellationToken ct)
    {
        var meta = await db.WhiteboardAttachments.AsNoTracking().Where(a => a.Id == attachmentId).Select(a => new { a.WhiteboardId }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (meta is null)
        {
            return Result<WhiteboardAttachmentDownload>.Failure(AttachmentNotFound);
        }

        var loaded = await LoadAsync(meta.WhiteboardId, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<WhiteboardAttachmentDownload>.Failure(AttachmentNotFound);
        }

        var a = await db.WhiteboardAttachments.AsNoTracking().FirstAsync(x => x.Id == attachmentId, ct).ConfigureAwait(false);
        return Result<WhiteboardAttachmentDownload>.Success(new WhiteboardAttachmentDownload(a.FileName, a.MediaType, a.IsImage, a.Data, a.Sha256));
    }

    public async Task<Result<IReadOnlyList<WhiteboardRevisionDto>>> ListRevisionsAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<IReadOnlyList<WhiteboardRevisionDto>>.Failure(loaded.Error!);
        }

        var rows = await db.WhiteboardRevisions.AsNoTracking().Where(r => r.WhiteboardId == id).OrderByDescending(r => r.CreatedAt).Take(WhiteboardRevision.DefaultRetention).ToListAsync(ct).ConfigureAwait(false);
        var names = await directory.GetUserNamesAsync(rows.Select(r => r.CreatedBy).Distinct().ToList(), ct).ConfigureAwait(false);
        return Result<IReadOnlyList<WhiteboardRevisionDto>>.Success(rows.Select(r => new WhiteboardRevisionDto(r.Id, r.BoardRevision, r.Title, r.CreatedAt, names.GetValueOrDefault(r.CreatedBy))).ToList());
    }

    public async Task<Result<WhiteboardRevisionDetailDto>> GetRevisionAsync(Guid id, Guid revisionId, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<WhiteboardRevisionDetailDto>.Failure(loaded.Error!);
        }

        var rev = await db.WhiteboardRevisions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == revisionId && r.WhiteboardId == id, ct).ConfigureAwait(false);
        if (rev is null)
        {
            return Result<WhiteboardRevisionDetailDto>.Failure(NotFound);
        }

        var elements = System.Text.Json.JsonSerializer.Deserialize<List<BoardElement>>(rev.ElementsJson, BoardJson.Options) ?? [];
        var names = await directory.GetUserNamesAsync([rev.CreatedBy], ct).ConfigureAwait(false);
        return Result<WhiteboardRevisionDetailDto>.Success(new WhiteboardRevisionDetailDto(
            rev.Id, rev.BoardRevision, rev.Title, rev.Description, rev.Background, rev.GridStyle.ToString(),
            elements.Select(ToElementDto).ToArray(), rev.CreatedAt, names.GetValueOrDefault(rev.CreatedBy)));
    }

    public async Task<Result<WhiteboardDto>> RestoreRevisionAsync(Guid id, Guid revisionId, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<WhiteboardDto>.Failure(loaded.Error!);
        }

        var (board, space) = loaded.Value;
        if (!space.CanWrite(PermissionAction.Edit))
        {
            return Result<WhiteboardDto>.Failure(WikiErrors.Forbidden);
        }

        var rev = await db.WhiteboardRevisions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == revisionId && r.WhiteboardId == id, ct).ConfigureAwait(false);
        if (rev is null)
        {
            return Result<WhiteboardDto>.Failure(NotFound);
        }

        var elements = System.Text.Json.JsonSerializer.Deserialize<List<BoardElement>>(rev.ElementsJson, BoardJson.Options) ?? [];
        board.Update(rev.Title, elements.Select(Whiteboard.ToInput).ToList(), current.UserId, clock.UtcNow, rev.Description, rev.Background, rev.GridStyle);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await CaptureRevisionAsync(board, ct).ConfigureAwait(false);
        await collaboration.ResetAsync(new Raavi.Wiki.Collaboration.DocumentAddress(db.CurrentTenantId, id, Raavi.Wiki.Collaboration.DocumentKind.Board).Name, ct).ConfigureAwait(false);
        return Result<WhiteboardDto>.Success(await ToDtoAsync(board, space, includeElements: true, favorited: false, ct).ConfigureAwait(false));
    }

    public async Task<Result<IReadOnlyList<WhiteboardTemplateDto>>> ListTemplatesAsync(string? spaceKey, CancellationToken ct)
    {
        Guid? spaceId = null;
        if (!string.IsNullOrWhiteSpace(spaceKey))
        {
            var space = await spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
            if (space is null)
            {
                return Result<IReadOnlyList<WhiteboardTemplateDto>>.Failure(WikiErrors.SpaceNotFound);
            }

            spaceId = space.Space.Id;
        }

        var rows = await db.WhiteboardTemplates.AsNoTracking()
            .Where(t => t.IsSystem || (spaceId != null && t.SpaceId == spaceId) || t.SpaceId == null)
            .OrderBy(t => t.Name).Take(200).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<WhiteboardTemplateDto>>.Success(rows.Select(t => new WhiteboardTemplateDto(t.Id, t.SpaceId, t.Name, t.Description, t.IsSystem, t.CreatedAt)).ToList());
    }

    public async Task<Result<WhiteboardDto>> CreateFromTemplateAsync(string spaceKey, Guid templateId, string? title, CancellationToken ct)
    {
        var space = await spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
        if (space is null)
        {
            return Result<WhiteboardDto>.Failure(WikiErrors.SpaceNotFound);
        }

        if (!space.CanWrite(PermissionAction.Edit))
        {
            return Result<WhiteboardDto>.Failure(WikiErrors.Forbidden);
        }

        var template = await db.WhiteboardTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId, ct).ConfigureAwait(false);
        if (template is null || (!template.IsSystem && template.SpaceId is { } sid && sid != space.Space.Id))
        {
            return Result<WhiteboardDto>.Failure(NotFound);
        }

        var elements = template.Elements.Select(Whiteboard.ToInput).ToList();
        var board = Whiteboard.Create(space.Space.Id, string.IsNullOrWhiteSpace(title) ? template.Name : title, elements, current.UserId, clock.UtcNow, template.Description, template.Background, template.GridStyle);
        db.Whiteboards.Add(board);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await CaptureRevisionAsync(board, ct).ConfigureAwait(false);
        return Result<WhiteboardDto>.Success(await ToDtoAsync(board, space, includeElements: true, favorited: false, ct).ConfigureAwait(false));
    }

    public async Task<Result<WhiteboardTemplateDto>> SaveAsTemplateAsync(Guid id, string? name, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<WhiteboardTemplateDto>.Failure(loaded.Error!);
        }

        var (board, space) = loaded.Value;
        if (!space.CanWrite(PermissionAction.Edit))
        {
            return Result<WhiteboardTemplateDto>.Failure(WikiErrors.Forbidden);
        }

        var template = WhiteboardTemplate.Create(space.Space.Id, string.IsNullOrWhiteSpace(name) ? board.Title : name, board.Description, board.Elements.Where(e => e.Type != BoardShape.Image).ToList(), board.Background, board.GridStyle, current.UserId, clock.UtcNow);
        db.WhiteboardTemplates.Add(template);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<WhiteboardTemplateDto>.Success(new WhiteboardTemplateDto(template.Id, template.SpaceId, template.Name, template.Description, template.IsSystem, template.CreatedAt));
    }

    public async Task<Result<IReadOnlyList<WhiteboardCommentDto>>> ListCommentsAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<IReadOnlyList<WhiteboardCommentDto>>.Failure(loaded.Error!);
        }

        var rows = await db.WhiteboardComments.AsNoTracking().Where(c => c.WhiteboardId == id).OrderBy(c => c.CreatedAt).Take(500).ToListAsync(ct).ConfigureAwait(false);
        var names = await directory.GetUserNamesAsync(rows.Select(r => r.CreatedBy).Distinct().ToList(), ct).ConfigureAwait(false);
        return Result<IReadOnlyList<WhiteboardCommentDto>>.Success(rows.Select(c => new WhiteboardCommentDto(c.Id, c.ParentId, c.ObjectId, c.X, c.Y, c.Body, names.GetValueOrDefault(c.CreatedBy), c.CreatedAt, c.ResolvedAt)).ToList());
    }

    public async Task<Result<WhiteboardCommentDto>> AddCommentAsync(Guid id, WhiteboardCommentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<WhiteboardCommentDto>.Failure(loaded.Error!);
        }

        if (!loaded.Value.Space.CanWrite(PermissionAction.Comment) && !loaded.Value.Space.CanWrite(PermissionAction.Edit))
        {
            return Result<WhiteboardCommentDto>.Failure(WikiErrors.Forbidden);
        }

        try
        {
            var comment = WhiteboardComment.Create(id, request.Body ?? string.Empty, current.UserId, clock.UtcNow, request.ParentId, request.ObjectId, request.X, request.Y);
            db.WhiteboardComments.Add(comment);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            var names = await directory.GetUserNamesAsync([current.UserId], ct).ConfigureAwait(false);
            return Result<WhiteboardCommentDto>.Success(new WhiteboardCommentDto(comment.Id, comment.ParentId, comment.ObjectId, comment.X, comment.Y, comment.Body, names.GetValueOrDefault(current.UserId), comment.CreatedAt, comment.ResolvedAt));
        }
        catch (DomainException ex)
        {
            return Result<WhiteboardCommentDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Validation));
        }
    }

    public async Task<Result> ResolveCommentAsync(Guid id, Guid commentId, bool resolve, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result.Failure(loaded.Error!);
        }

        if (!loaded.Value.Space.CanWrite(PermissionAction.Edit))
        {
            return Result.Failure(WikiErrors.Forbidden);
        }

        var comment = await db.WhiteboardComments.FirstOrDefaultAsync(c => c.Id == commentId && c.WhiteboardId == id, ct).ConfigureAwait(false);
        if (comment is null)
        {
            return Result.Failure(NotFound);
        }

        if (resolve)
        {
            comment.Resolve(current.UserId, clock.UtcNow);
        }
        else
        {
            comment.Reopen();
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    private async Task CaptureRevisionAsync(Whiteboard board, CancellationToken ct)
    {
        db.WhiteboardRevisions.Add(WhiteboardRevision.Capture(board, current.UserId, clock.UtcNow));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var keep = await db.WhiteboardRevisions.Where(r => r.WhiteboardId == board.Id).OrderByDescending(r => r.CreatedAt).Skip(WhiteboardRevision.DefaultRetention).ToListAsync(ct).ConfigureAwait(false);
        if (keep.Count > 0)
        {
            db.WhiteboardRevisions.RemoveRange(keep);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task<Result> ValidateImageAttachmentsAsync(Guid boardId, List<BoardElementInput> elements, CancellationToken ct)
    {
        var ids = elements.Where(e => e.Type == BoardShape.Image).Select(e => e.AttachmentId).Where(a => a is not null).Select(a => a!.Value).Distinct().ToList();
        if (ids.Count == 0)
        {
            return Result.Success();
        }

        var owned = await db.WhiteboardAttachments.AsNoTracking().Where(a => a.WhiteboardId == boardId && ids.Contains(a.Id)).Select(a => a.Id).ToListAsync(ct).ConfigureAwait(false);
        return owned.Count == ids.Count ? Result.Success() : Result.Failure(ElementInvalid);
    }

    private static BoardGridStyle ParseGrid(string? value) =>
        Enum.TryParse<BoardGridStyle>(value, ignoreCase: true, out var g) && Enum.IsDefined(g) ? g : BoardGridStyle.Dot;

    private static Result<List<BoardElementInput>> Parse(WhiteboardRequest request)
    {
        var list = new List<BoardElementInput>();
        foreach (var e in request.Elements ?? [])
        {
            if (!Enum.TryParse<BoardShape>(e.Type, ignoreCase: true, out var type))
            {
                return Result<List<BoardElementInput>>.Failure(ElementInvalid);
            }

            BoardPort? fromPort = Enum.TryParse<BoardPort>(e.FromPort, true, out var fp) ? fp : null;
            BoardPort? toPort = Enum.TryParse<BoardPort>(e.ToPort, true, out var tp) ? tp : null;
            BoardConnectorRoute? route = Enum.TryParse<BoardConnectorRoute>(e.Route, true, out var rt) ? rt : null;
            list.Add(new BoardElementInput(
                e.Id, type, e.X, e.Y, e.W, e.H, e.Rotation, e.Stroke, e.Fill, e.StrokeWidth, e.Text, e.FontSize, e.Rtl, e.Points,
                e.CornerRadius, e.AttachmentId, e.Alt, e.Locked, e.Hidden, e.ParentId, e.FromId, e.ToId, fromPort, toPort, route,
                e.StartArrow, e.EndArrow, e.Children, e.Rows, e.Cols, e.Cells, e.ColWidths));
        }

        return Result<List<BoardElementInput>>.Success(list);
    }

    private async Task<Result<(Whiteboard Board, SpaceRights Space)>> LoadAsync(Guid id, bool track, CancellationToken ct)
    {
        var board = await (track ? db.Whiteboards : db.Whiteboards.AsNoTracking()).FirstOrDefaultAsync(b => b.Id == id, ct).ConfigureAwait(false);
        var space = board is null ? null : await db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == board.SpaceId, ct).ConfigureAwait(false);
        if (board is null || space is null)
        {
            return Result<(Whiteboard, SpaceRights)>.Failure(NotFound);
        }

        var rights = await authz.GetSpaceRightsAsync(space, ct).ConfigureAwait(false);
        return rights.CanView ? Result<(Whiteboard, SpaceRights)>.Success((board, rights)) : Result<(Whiteboard, SpaceRights)>.Failure(NotFound);
    }

    private async Task<WhiteboardDto> ToDtoAsync(Whiteboard b, SpaceRights r, bool includeElements, bool favorited, CancellationToken ct)
    {
        var names = await directory.GetUserNamesAsync([b.CreatedBy, b.UpdatedBy], ct).ConfigureAwait(false);
        var elements = b.Elements;
        return new WhiteboardDto(
            b.Id, r.Space.Key, r.Space.Name, b.Title, b.Description, elements.Count,
            includeElements ? elements.Select(ToElementDto).ToArray() : [],
            b.Background, b.GridStyle.ToString(), names.GetValueOrDefault(b.CreatedBy), names.GetValueOrDefault(b.UpdatedBy),
            b.CreatedAt, b.UpdatedAt, b.ArchivedAt, b.Revision, r.CanWrite(PermissionAction.Edit), favorited);
    }

    private static BoardElementDto ToElementDto(BoardElement e) => new(
        e.Id, e.Type.ToString(), e.X, e.Y, e.W, e.H, e.Rotation, e.Stroke, e.Fill, e.StrokeWidth, e.Text, e.FontSize, e.Rtl, e.Points,
        e.CornerRadius, e.AttachmentId, e.Alt, e.Locked, e.Hidden, e.ParentId,
        e.FromId, e.ToId, e.FromPort.ToString(), e.ToPort.ToString(), e.Route.ToString(), e.StartArrow, e.EndArrow,
        e.Children, e.Rows, e.Cols, e.Cells, e.ColWidths);

    private static WhiteboardAttachmentDto ToAttachmentDto(WhiteboardAttachment a) =>
        new(a.Id, a.WhiteboardId, a.FileName, a.MediaType, a.Size, a.IsImage, a.CreatedAt);
}
