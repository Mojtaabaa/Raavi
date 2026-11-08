using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Infrastructure.Http;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki.Application;

public sealed record BoardElementDto(string Id, string Type, double X, double Y, double W, double H, double Rotation, string Stroke, string? Fill, int StrokeWidth, string Text, int FontSize, bool Rtl, double[] Points);

public sealed record WhiteboardDto(
    Guid Id, string SpaceKey, string SpaceName, string Title, int ElementCount, BoardElementDto[] Elements, string? CreatedByName, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int Revision, bool CanEdit);

public sealed record BoardElementRequest(string? Id, string? Type, double? X, double? Y, double? W, double? H, double? Rotation, string? Stroke, string? Fill, int? StrokeWidth, string? Text, int? FontSize, bool? Rtl, double[]? Points);

public sealed record WhiteboardRequest(string? Title, BoardElementRequest[]? Elements);

public sealed record BoardTitleRequest(string? Title);

public sealed record WhiteboardExport(string FileName, byte[] Data);

/// <summary>Space whiteboards (Phase 9e, ADR-030): validated elements, ETag concurrency, SVG export.</summary>
public sealed class WhiteboardService(WikiDbContext db, SpaceService spaces, WikiAuthorizer authz, IPrincipalDirectory directory, ICurrentUser current, IClock clock, Raavi.Wiki.Collaboration.CollaborationClient collaboration)
{
    public static readonly Error NotFound = new("board.not_found", "Whiteboard not found.", ErrorKind.NotFound);
    public static readonly Error ElementInvalid = new("board.element_invalid", "An element is not valid.", ErrorKind.Validation);

    public async Task<Result<IReadOnlyList<WhiteboardDto>>> ListAsync(string spaceKey, CancellationToken ct)
    {
        var space = await spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
        if (space is null)
        {
            return Result<IReadOnlyList<WhiteboardDto>>.Failure(WikiErrors.SpaceNotFound);
        }

        var boards = await db.Whiteboards.AsNoTracking().Where(b => b.SpaceId == space.Space.Id).OrderBy(b => b.Title).Take(500).ToListAsync(ct).ConfigureAwait(false);
        var list = new List<WhiteboardDto>();
        foreach (var b in boards)
        {
            list.Add(await ToDtoAsync(b, space, includeElements: false, ct).ConfigureAwait(false));
        }

        return Result<IReadOnlyList<WhiteboardDto>>.Success(list);
    }

    public async Task<Result<WhiteboardDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        return loaded.IsSuccess
            ? Result<WhiteboardDto>.Success(await ToDtoAsync(loaded.Value.Board, loaded.Value.Space, includeElements: true, ct).ConfigureAwait(false))
            : Result<WhiteboardDto>.Failure(loaded.Error!);
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

        var board = Whiteboard.Create(space.Space.Id, request.Title, elements.Value, current.UserId, clock.UtcNow);
        db.Whiteboards.Add(board);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<WhiteboardDto>.Success(await ToDtoAsync(board, space, includeElements: true, ct).ConfigureAwait(false));
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

        board.Update(request.Title, elements.Value, clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // The live state is gone with this save: people editing live are disconnected and re-join from the new content.
        await collaboration.ResetAsync(new Raavi.Wiki.Collaboration.DocumentAddress(db.CurrentTenantId, id, Raavi.Wiki.Collaboration.DocumentKind.Board).Name, ct).ConfigureAwait(false);
        return Result<WhiteboardDto>.Success(await ToDtoAsync(board, space, includeElements: true, ct).ConfigureAwait(false));
    }

    /// <summary>Renames without touching elements or live sessions (live editors cannot use the whole-board save).</summary>
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

        board.Rename(request.Title, clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<WhiteboardDto>.Success(await ToDtoAsync(board, space, includeElements: true, ct).ConfigureAwait(false));
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
        var svg = BoardSvgWriter.Write(board.Title, board.Elements);
        var safe = new string(board.Title.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        return Result<WhiteboardExport>.Success(new WhiteboardExport($"whiteboard-{(safe.Length == 0 ? "board" : safe)}.svg", System.Text.Encoding.UTF8.GetBytes(svg)));
    }

    private static Result<List<BoardElementInput>> Parse(WhiteboardRequest request)
    {
        var list = new List<BoardElementInput>();
        foreach (var e in request.Elements ?? [])
        {
            if (!Enum.TryParse<BoardShape>(e.Type, ignoreCase: true, out var type))
            {
                return Result<List<BoardElementInput>>.Failure(ElementInvalid);
            }

            list.Add(new BoardElementInput(e.Id, type, e.X, e.Y, e.W, e.H, e.Rotation, e.Stroke, e.Fill, e.StrokeWidth, e.Text, e.FontSize, e.Rtl, e.Points));
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

        // A board of a space the caller cannot view does not exist for them.
        var rights = await authz.GetSpaceRightsAsync(space, ct).ConfigureAwait(false);
        return rights.CanView ? Result<(Whiteboard, SpaceRights)>.Success((board, rights)) : Result<(Whiteboard, SpaceRights)>.Failure(NotFound);
    }

    private async Task<WhiteboardDto> ToDtoAsync(Whiteboard b, SpaceRights r, bool includeElements, CancellationToken ct)
    {
        var names = await directory.GetUserNamesAsync([b.CreatedBy], ct).ConfigureAwait(false);
        var elements = b.Elements;
        return new WhiteboardDto(
            b.Id, r.Space.Key, r.Space.Name, b.Title, elements.Count,
            includeElements ? elements.Select(e => new BoardElementDto(e.Id, e.Type.ToString(), e.X, e.Y, e.W, e.H, e.Rotation, e.Stroke, e.Fill, e.StrokeWidth, e.Text, e.FontSize, e.Rtl, e.Points)).ToArray() : [],
            names.GetValueOrDefault(b.CreatedBy), b.CreatedAt, b.UpdatedAt, b.Revision, r.CanWrite(PermissionAction.Edit));
    }
}
