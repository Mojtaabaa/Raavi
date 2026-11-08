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

public sealed record SlideDto(string Id, string Layout, string Title, string Body, string Notes);

public sealed record SlideDeckDto(
    Guid Id, string SpaceKey, string SpaceName, string Title, string Direction, int SlideCount, SlideDto[] Slides, string? CreatedByName, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int Revision, bool CanEdit);

public sealed record SlideRequest(string? Id, string? Layout, string? Title, string? Body, string? Notes);

public sealed record SlideDeckRequest(string? Title, string? Direction, SlideRequest[]? Slides);

public sealed record SlideDeckExport(string FileName, byte[] Data);

/// <summary>Space slide decks (Phase 9d, ADR-029): validated text slides with speaker notes, ETag concurrency, PPTX export.</summary>
public sealed class SlideService(WikiDbContext db, SpaceService spaces, WikiAuthorizer authz, IPrincipalDirectory directory, ICurrentUser current, IClock clock)
{
    public const string PptxContentType = "application/vnd.openxmlformats-officedocument.presentationml.presentation";

    public static readonly Error NotFound = new("slides.not_found", "Slide deck not found.", ErrorKind.NotFound);
    public static readonly Error SlideInvalid = new("slides.slide_invalid", "A slide is not valid.", ErrorKind.Validation);

    public async Task<Result<IReadOnlyList<SlideDeckDto>>> ListAsync(string spaceKey, CancellationToken ct)
    {
        var space = await spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
        if (space is null)
        {
            return Result<IReadOnlyList<SlideDeckDto>>.Failure(WikiErrors.SpaceNotFound);
        }

        var decks = await db.SlideDecks.AsNoTracking().Where(d => d.SpaceId == space.Space.Id).OrderBy(d => d.Title).Take(500).ToListAsync(ct).ConfigureAwait(false);
        var list = new List<SlideDeckDto>();
        foreach (var d in decks)
        {
            list.Add(await ToDtoAsync(d, space, includeSlides: false, ct).ConfigureAwait(false));
        }

        return Result<IReadOnlyList<SlideDeckDto>>.Success(list);
    }

    public async Task<Result<SlideDeckDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        return loaded.IsSuccess
            ? Result<SlideDeckDto>.Success(await ToDtoAsync(loaded.Value.Deck, loaded.Value.Space, includeSlides: true, ct).ConfigureAwait(false))
            : Result<SlideDeckDto>.Failure(loaded.Error!);
    }

    public async Task<Result<SlideDeckDto>> CreateAsync(string spaceKey, SlideDeckRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var space = await spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
        if (space is null)
        {
            return Result<SlideDeckDto>.Failure(WikiErrors.SpaceNotFound);
        }

        if (!space.CanWrite(PermissionAction.Edit))
        {
            return Result<SlideDeckDto>.Failure(space.Space.Status == SpaceStatus.Archived ? WikiErrors.SpaceArchived : WikiErrors.Forbidden);
        }

        var parsed = Parse(request);
        if (!parsed.IsSuccess)
        {
            return Result<SlideDeckDto>.Failure(parsed.Error!);
        }

        var deck = SlideDeck.Create(space.Space.Id, request.Title, parsed.Value.Direction, parsed.Value.Slides, current.UserId, clock.UtcNow);
        db.SlideDecks.Add(deck);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<SlideDeckDto>.Success(await ToDtoAsync(deck, space, includeSlides: true, ct).ConfigureAwait(false));
    }

    public async Task<Result<SlideDeckDto>> UpdateAsync(Guid id, int expectedRevision, SlideDeckRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<SlideDeckDto>.Failure(loaded.Error!);
        }

        var (deck, space) = loaded.Value;
        if (!space.CanWrite(PermissionAction.Edit))
        {
            return Result<SlideDeckDto>.Failure(WikiErrors.Forbidden);
        }

        if (deck.Revision != expectedRevision)
        {
            return Result<SlideDeckDto>.Failure(ETags.PreconditionFailed);
        }

        var parsed = Parse(request);
        if (!parsed.IsSuccess)
        {
            return Result<SlideDeckDto>.Failure(parsed.Error!);
        }

        deck.Update(request.Title, parsed.Value.Direction, parsed.Value.Slides, clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<SlideDeckDto>.Success(await ToDtoAsync(deck, space, includeSlides: true, ct).ConfigureAwait(false));
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

        db.SlideDecks.Remove(loaded.Value.Deck);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result<SlideDeckExport>> ExportPptxAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<SlideDeckExport>.Failure(loaded.Error!);
        }

        var deck = loaded.Value.Deck;
        var data = PptxWriter.Write(deck.Title, deck.Direction, deck.Slides, "Raavi");
        var safe = new string(deck.Title.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        return Result<SlideDeckExport>.Success(new SlideDeckExport($"slides-{(safe.Length == 0 ? "deck" : safe)}.pptx", data));
    }

    private static Result<(TextDirection Direction, List<SlideInput> Slides)> Parse(SlideDeckRequest request)
    {
        var direction = TextDirection.Ltr;
        if (!string.IsNullOrWhiteSpace(request.Direction) && !Enum.TryParse(request.Direction, ignoreCase: true, out direction))
        {
            return Result<(TextDirection, List<SlideInput>)>.Failure(new Error("slides.direction_invalid", "The direction must be Ltr or Rtl.", ErrorKind.Validation));
        }

        var slides = new List<SlideInput>();
        foreach (var s in request.Slides ?? [])
        {
            if (!Enum.TryParse<SlideLayout>(s.Layout, ignoreCase: true, out var layout))
            {
                return Result<(TextDirection, List<SlideInput>)>.Failure(SlideInvalid);
            }

            slides.Add(new SlideInput(s.Id, layout, s.Title, s.Body, s.Notes));
        }

        return Result<(TextDirection, List<SlideInput>)>.Success((direction, slides));
    }

    private async Task<Result<(SlideDeck Deck, SpaceRights Space)>> LoadAsync(Guid id, bool track, CancellationToken ct)
    {
        var deck = await (track ? db.SlideDecks : db.SlideDecks.AsNoTracking()).FirstOrDefaultAsync(d => d.Id == id, ct).ConfigureAwait(false);
        var space = deck is null ? null : await db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == deck.SpaceId, ct).ConfigureAwait(false);
        if (deck is null || space is null)
        {
            return Result<(SlideDeck, SpaceRights)>.Failure(NotFound);
        }

        // A deck of a space the caller cannot view does not exist for them.
        var rights = await authz.GetSpaceRightsAsync(space, ct).ConfigureAwait(false);
        return rights.CanView ? Result<(SlideDeck, SpaceRights)>.Success((deck, rights)) : Result<(SlideDeck, SpaceRights)>.Failure(NotFound);
    }

    private async Task<SlideDeckDto> ToDtoAsync(SlideDeck d, SpaceRights r, bool includeSlides, CancellationToken ct)
    {
        var names = await directory.GetUserNamesAsync([d.CreatedBy], ct).ConfigureAwait(false);
        var slides = d.Slides;
        return new SlideDeckDto(
            d.Id, r.Space.Key, r.Space.Name, d.Title, d.Direction.ToString(), slides.Count,
            includeSlides ? slides.Select(s => new SlideDto(s.Id, s.Layout.ToString(), s.Title, s.Body, s.Notes)).ToArray() : [],
            names.GetValueOrDefault(d.CreatedBy), d.CreatedAt, d.UpdatedAt, d.Revision, r.CanWrite(PermissionAction.Edit));
    }
}
