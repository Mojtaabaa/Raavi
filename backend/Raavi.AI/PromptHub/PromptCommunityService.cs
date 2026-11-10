using Microsoft.EntityFrameworkCore;
using Raavi.AI.Persistence;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Domain.Common;

namespace Raavi.AI.PromptHub;

public sealed record RateRequest(int Stars, string? Feedback, string? Comment);

public sealed record RatingEntryDto(Guid UserId, string? UserName, int Stars, string? Feedback, string? Comment, DateTimeOffset UpdatedAt, bool Mine);

public sealed record RatingsDto(double? Average, int Count, int[] Distribution, int? Mine, IReadOnlyList<RatingEntryDto> Entries);

public sealed record DiffLine(string Kind, string Text);

public sealed record VersionDiffDto(int FromNumber, int ToNumber, IReadOnlyList<DiffLine> Lines, int Added, int Removed);

public sealed record RestoreRequest(bool DiscardDraft);

public sealed record AnalyticsRow(Guid PromptId, string Title, Guid OwnerId, string? OwnerName, int Automatic, int Successful, int Failed, int Copies, int ConfirmedManual, int Users, long Tokens);

public sealed record AnalyticsDto(int Automatic, int Successful, int Failed, int Copies, int ConfirmedManual, int Users, long Tokens, IReadOnlyList<AnalyticsRow> Prompts);

/// <summary>Ratings and feedback, forking, version history and workspace analytics (ADR-052).</summary>
public sealed class PromptCommunityService(AiDbContext db, ICurrentUser current, IPrincipalDirectory directory, IClock clock, PromptHubService hub)
{
    public const int MaxDiffLines = 3000;

    public static readonly Error SelfRating = new("prompt.rating_own", "You cannot rate your own prompt.", ErrorKind.Forbidden);
    public static readonly Error NotPublic = new("prompt.not_public", "Only prompts shared with the workspace can be rated or forked.", ErrorKind.Conflict);
    public static readonly Error DraftExists = new("prompt.draft_exists", "There is an unpublished draft. Discard it to restore an older version.", ErrorKind.Conflict);
    public static readonly Error VersionNotFound = new("prompt.version_not_found", "Version not found.", ErrorKind.NotFound);
    public static readonly Error DiffTooLarge = new("prompt.diff_too_large", "The versions are too large to compare.", ErrorKind.Validation);

    private async Task<(Prompt Prompt, PrincipalSnapshot Me)?> VisibleAsync(Guid id, CancellationToken ct, bool track = false)
    {
        var me = await directory.GetPrincipalAsync(current.UserId, cancellationToken: ct).ConfigureAwait(false);
        if (!me.IsActiveMember)
        {
            return null;
        }

        var prompt = track ? await db.Prompts.FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false) : await db.Prompts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false);
        return prompt is null || !PromptHubService.CanSee(prompt, me) ? null : (prompt, me);
    }

    private static bool IsPublic(Prompt p) => p.Visibility == PromptVisibility.Tenant && p.Status is PromptStatus.Published or PromptStatus.Deprecated && p.PublishedVersionId != null;

    // ── Ratings ────────────────────────────────────────────────────────────────────────────────

    public async Task<Result<RatingsDto>> RateAsync(Guid id, RateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var visible = await VisibleAsync(id, ct).ConfigureAwait(false);
        if (visible is null)
        {
            return Result<RatingsDto>.Failure(PromptErrors.NotFound);
        }

        var (prompt, me) = visible.Value;
        if (!IsPublic(prompt))
        {
            return Result<RatingsDto>.Failure(NotPublic);
        }

        if (prompt.OwnerId == me.UserId)
        {
            return Result<RatingsDto>.Failure(SelfRating);
        }

        var now = clock.UtcNow;
        try
        {
            var existing = await db.PromptRatings.FirstOrDefaultAsync(r => r.PromptId == id && r.UserId == me.UserId, ct).ConfigureAwait(false);
            if (existing is null)
            {
                db.PromptRatings.Add(PromptRating.Create(id, me.UserId, request.Stars, request.Feedback, request.Comment, now));
            }
            else
            {
                existing.Set(request.Stars, request.Feedback, request.Comment, now);
            }

            db.PromptEvents.Add(PromptEvent.Create(PromptUsageEventTypes.Rated, id, prompt.PublishedVersionId, me.UserId, now, details: request.Stars.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return Result<RatingsDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Validation));
        }
        catch (DbUpdateException)
        {
            return Result<RatingsDto>.Failure(new Error("prompt.rating_conflict", "Your rating was submitted twice at the same time. Try again.", ErrorKind.Conflict)); // unique (prompt, user) index
        }

        return await RatingsAsync(id, ct).ConfigureAwait(false);
    }

    public async Task<Result<RatingsDto>> RemoveRatingAsync(Guid id, CancellationToken ct)
    {
        var visible = await VisibleAsync(id, ct).ConfigureAwait(false);
        if (visible is null)
        {
            return Result<RatingsDto>.Failure(PromptErrors.NotFound);
        }

        await db.PromptRatings.Where(r => r.PromptId == id && r.UserId == visible.Value.Me.UserId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return await RatingsAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>Summary for everybody who can see the prompt; the individual comments only for the prompt's owner and workspace administrators (and your own).</summary>
    public async Task<Result<RatingsDto>> RatingsAsync(Guid id, CancellationToken ct)
    {
        var visible = await VisibleAsync(id, ct).ConfigureAwait(false);
        if (visible is null)
        {
            return Result<RatingsDto>.Failure(PromptErrors.NotFound);
        }

        var (prompt, me) = visible.Value;
        var all = await db.PromptRatings.AsNoTracking().Where(r => r.PromptId == id).OrderByDescending(r => r.UpdatedAt).ToListAsync(ct).ConfigureAwait(false);
        var distribution = new int[5];
        foreach (var r in all)
        {
            distribution[r.Stars - 1]++;
        }

        var shown = prompt.OwnerId == me.UserId || me.IsTenantAdministrator ? all : all.Where(r => r.UserId == me.UserId).ToList();
        var names = await directory.GetUserNamesAsync([.. shown.Select(r => r.UserId)], ct).ConfigureAwait(false);
        var entries = shown.Take(100).Select(r => new RatingEntryDto(r.UserId, names.GetValueOrDefault(r.UserId), r.Stars, r.FeedbackKind, r.Comment, r.UpdatedAt, r.UserId == me.UserId)).ToList();
        return Result<RatingsDto>.Success(new RatingsDto(all.Count == 0 ? null : Math.Round(all.Average(r => (double)r.Stars), 2), all.Count, distribution, all.FirstOrDefault(r => r.UserId == me.UserId)?.Stars, entries));
    }

    // ── Forking ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Copies the published version of a public prompt into your own library as an independent personal draft; the original is never touched or followed afterwards.</summary>
    public async Task<Result<PromptDetailDto>> ForkAsync(Guid id, CancellationToken ct)
    {
        var visible = await VisibleAsync(id, ct).ConfigureAwait(false);
        if (visible is null)
        {
            return Result<PromptDetailDto>.Failure(PromptErrors.NotFound);
        }

        var (source, me) = visible.Value;
        if (!IsPublic(source))
        {
            return Result<PromptDetailDto>.Failure(NotPublic);
        }

        var version = await db.PromptVersions.AsNoTracking().FirstAsync(v => v.Id == source.PublishedVersionId, ct).ConfigureAwait(false);
        var now = clock.UtcNow;
        var fork = Prompt.Create(me.UserId, source.Title, source.Description, source.CategoryId, source.TagList, source.Language, source.ContextList, source.Modes, now);
        fork.RecordFork(source);
        var draft = PromptVersion.CreateDraft(fork.Id, 1, version.ContentJson, null, me.UserId, now, version.VariablesJson);
        db.Prompts.Add(fork);
        db.PromptVersions.Add(draft);
        fork.SetDraft(draft.Id, 1, now);
        db.PromptEvents.Add(PromptEvent.Create(PromptUsageEventTypes.Forked, source.Id, version.Id, me.UserId, now, details: fork.Id.ToString()));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await hub.GetAsync(fork.Id, ct).ConfigureAwait(false);
    }

    // ── Versions ───────────────────────────────────────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<PromptVersionDto>>> VersionsAsync(Guid id, CancellationToken ct)
    {
        var visible = await VisibleAsync(id, ct).ConfigureAwait(false);
        if (visible is null)
        {
            return Result<IReadOnlyList<PromptVersionDto>>.Failure(PromptErrors.NotFound);
        }

        var (prompt, me) = visible.Value;
        var isOwner = prompt.OwnerId == me.UserId;
        var rows = await db.PromptVersions.AsNoTracking().Where(v => v.PromptId == id && (isOwner || v.IsPublished)).OrderByDescending(v => v.Number).Take(200).ToListAsync(ct).ConfigureAwait(false);
        var names = await directory.GetUserNamesAsync([.. rows.Select(r => r.CreatedBy).Distinct()], ct).ConfigureAwait(false);
        return Result<IReadOnlyList<PromptVersionDto>>.Success(rows.Select(v => new PromptVersionDto(v.Id, v.Number, v.IsPublished, v.ChangeNote, v.CreatedBy, names.GetValueOrDefault(v.CreatedBy), v.CreatedAt, v.PublishedAt)).ToList());
    }

    private async Task<PromptVersion?> VersionAsync(Prompt prompt, PrincipalSnapshot me, Guid versionId, CancellationToken ct)
    {
        var v = await db.PromptVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == versionId && x.PromptId == prompt.Id, ct).ConfigureAwait(false);
        return v is null || (!v.IsPublished && prompt.OwnerId != me.UserId) ? null : v;
    }

    public async Task<Result<PromptDetailDto>> GetVersionAsync(Guid id, Guid versionId, CancellationToken ct)
    {
        var visible = await VisibleAsync(id, ct).ConfigureAwait(false);
        var version = visible is null ? null : await VersionAsync(visible.Value.Prompt, visible.Value.Me, versionId, ct).ConfigureAwait(false);
        if (visible is null || version is null)
        {
            return Result<PromptDetailDto>.Failure(visible is null ? PromptErrors.NotFound : VersionNotFound);
        }

        var summary = (await hub.ListOneAsync(visible.Value.Prompt, visible.Value.Me, ct).ConfigureAwait(false));
        var names = await directory.GetUserNamesAsync([version.CreatedBy], ct).ConfigureAwait(false);
        var info = new PromptVersionDto(version.Id, version.Number, version.IsPublished, version.ChangeNote, version.CreatedBy, names.GetValueOrDefault(version.CreatedBy), version.CreatedAt, version.PublishedAt);
        return Result<PromptDetailDto>.Success(new PromptDetailDto(summary, System.Text.Json.JsonDocument.Parse(version.ContentJson).RootElement.Clone(), version.ContentText, info, null, null, PromptInputs.Parse(version.VariablesJson)));
    }

    /// <summary>Line diff of the template text of two versions of the same prompt (longest common subsequence; the order of the arguments is old → new).</summary>
    public async Task<Result<VersionDiffDto>> DiffAsync(Guid id, Guid fromId, Guid toId, CancellationToken ct)
    {
        var visible = await VisibleAsync(id, ct).ConfigureAwait(false);
        if (visible is null)
        {
            return Result<VersionDiffDto>.Failure(PromptErrors.NotFound);
        }

        var from = await VersionAsync(visible.Value.Prompt, visible.Value.Me, fromId, ct).ConfigureAwait(false);
        var to = await VersionAsync(visible.Value.Prompt, visible.Value.Me, toId, ct).ConfigureAwait(false);
        if (from is null || to is null)
        {
            return Result<VersionDiffDto>.Failure(VersionNotFound);
        }

        var a = from.ContentText.ReplaceLineEndings("\n").Split('\n');
        var b = to.ContentText.ReplaceLineEndings("\n").Split('\n');
        if (a.Length > MaxDiffLines || b.Length > MaxDiffLines)
        {
            return Result<VersionDiffDto>.Failure(DiffTooLarge);
        }

        var lines = Diff(a, b);
        return Result<VersionDiffDto>.Success(new VersionDiffDto(from.Number, to.Number, lines, lines.Count(l => l.Kind == "add"), lines.Count(l => l.Kind == "del")));
    }

    public static List<DiffLine> Diff(string[] a, string[] b)
    {
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var result = new List<DiffLine>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y])
            {
                result.Add(new DiffLine("same", a[x]));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                result.Add(new DiffLine("del", a[x++]));
            }
            else
            {
                result.Add(new DiffLine("add", b[y++]));
            }
        }

        while (x < a.Length)
        {
            result.Add(new DiffLine("del", a[x++]));
        }

        while (y < b.Length)
        {
            result.Add(new DiffLine("add", b[y++]));
        }

        return result;
    }

    /// <summary>Puts the text of an older version into a NEW draft. History is never rewritten; an existing draft is only replaced when the caller says so.</summary>
    public async Task<Result<PromptDetailDto>> RestoreAsync(Guid id, Guid versionId, RestoreRequest request, CancellationToken ct)
    {
        var visible = await VisibleAsync(id, ct, track: true).ConfigureAwait(false);
        if (visible is null)
        {
            return Result<PromptDetailDto>.Failure(PromptErrors.NotFound);
        }

        var (prompt, me) = visible.Value;
        if (prompt.OwnerId != me.UserId)
        {
            return Result<PromptDetailDto>.Failure(PromptErrors.Forbidden);
        }

        var source = await VersionAsync(prompt, me, versionId, ct).ConfigureAwait(false);
        if (source is null)
        {
            return Result<PromptDetailDto>.Failure(VersionNotFound);
        }

        var now = clock.UtcNow;
        if (prompt.DraftVersionId is { } did)
        {
            if (request is null || !request.DiscardDraft)
            {
                return Result<PromptDetailDto>.Failure(DraftExists);
            }

            var draft = await db.PromptVersions.FirstAsync(v => v.Id == did, ct).ConfigureAwait(false);
            draft.ReplaceContent(source.ContentJson, $"Restored from version {source.Number}", source.VariablesJson);
            prompt.SetDraft(draft.Id, draft.Number, now);
        }
        else
        {
            var draft = PromptVersion.CreateDraft(prompt.Id, prompt.LatestVersionNumber + 1, source.ContentJson, $"Restored from version {source.Number}", me.UserId, now, source.VariablesJson);
            db.PromptVersions.Add(draft);
            prompt.SetDraft(draft.Id, draft.Number, now);
        }

        db.PromptEvents.Add(PromptEvent.Create(PromptUsageEventTypes.VersionRestored, prompt.Id, source.Id, me.UserId, now, details: $"v{source.Number}"));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await hub.GetAsync(id, ct).ConfigureAwait(false);
    }

    // ── Workspace analytics (administrators) ───────────────────────────────────────────────────

    public async Task<Result<AnalyticsDto>> AnalyticsAsync(DateTimeOffset? from, DateTimeOffset? to, Guid? categoryId, Guid? userId, string? mode, CancellationToken ct)
    {
        var me = await directory.GetPrincipalAsync(current.UserId, cancellationToken: ct).ConfigureAwait(false);
        if (!me.IsActiveMember || !me.IsTenantAdministrator)
        {
            return Result<AnalyticsDto>.Failure(PromptErrors.Forbidden);
        }

        var types = new[] { PromptUsageEventTypes.ExecutionStarted, PromptUsageEventTypes.ExecutionCompleted, PromptUsageEventTypes.ExecutionFailed, PromptUsageEventTypes.Copied, PromptUsageEventTypes.ManualUseConfirmed };
        var events = db.PromptEvents.AsNoTracking().Where(e => e.PromptId != null && types.Contains(e.Type) && (from == null || e.At >= from) && (to == null || e.At <= to) && (userId == null || e.UserId == userId));
        if (!string.IsNullOrEmpty(mode))
        {
            var m = mode.ToLowerInvariant();
            events = events.Where(e => e.Mode == m);
        }

        if (categoryId is { } cat)
        {
            events = events.Where(e => db.Prompts.Any(p => p.Id == e.PromptId && p.CategoryId == cat));
        }

        var grouped = await events.GroupBy(e => new { e.PromptId, e.Type }).Select(g => new { g.Key.PromptId, g.Key.Type, N = g.Count(), Tokens = g.Sum(x => (long?)((x.PromptTokens ?? 0) + (x.CompletionTokens ?? 0))) ?? 0 }).ToListAsync(ct).ConfigureAwait(false);
        var users = await events.Select(e => e.UserId).Distinct().CountAsync(ct).ConfigureAwait(false);
        var perPrompt = grouped.GroupBy(g => g.PromptId!.Value).ToList();
        var ids = perPrompt.Select(g => g.Key).ToList();
        var prompts = await db.Prompts.AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.Title, p.OwnerId }).ToDictionaryAsync(p => p.Id, ct).ConfigureAwait(false);
        var names = await directory.GetUserNamesAsync([.. prompts.Values.Select(p => p.OwnerId).Distinct()], ct).ConfigureAwait(false);
        var rows = new List<AnalyticsRow>();
        foreach (var g in perPrompt)
        {
            if (!prompts.TryGetValue(g.Key, out var p))
            {
                continue;
            }

            var items = g.ToList();
            int C(string t) => items.Where(x => x.Type == t).Sum(x => x.N);
            var promptUsers = await events.Where(e => e.PromptId == g.Key).Select(e => e.UserId).Distinct().CountAsync(ct).ConfigureAwait(false);
            rows.Add(new AnalyticsRow(g.Key, p.Title, p.OwnerId, names.GetValueOrDefault(p.OwnerId), C(PromptUsageEventTypes.ExecutionStarted), C(PromptUsageEventTypes.ExecutionCompleted), C(PromptUsageEventTypes.ExecutionFailed), C(PromptUsageEventTypes.Copied), C(PromptUsageEventTypes.ManualUseConfirmed), promptUsers, items.Sum(x => x.Tokens)));
        }

        rows = [.. rows.OrderByDescending(r => r.Automatic + r.Copies + r.ConfirmedManual).Take(100)];
        return Result<AnalyticsDto>.Success(new AnalyticsDto(grouped.Where(x => x.Type == PromptUsageEventTypes.ExecutionStarted).Sum(x => x.N), grouped.Where(x => x.Type == PromptUsageEventTypes.ExecutionCompleted).Sum(x => x.N),
            grouped.Where(x => x.Type == PromptUsageEventTypes.ExecutionFailed).Sum(x => x.N), grouped.Where(x => x.Type == PromptUsageEventTypes.Copied).Sum(x => x.N), grouped.Where(x => x.Type == PromptUsageEventTypes.ManualUseConfirmed).Sum(x => x.N), users, grouped.Sum(x => x.Tokens), rows));
    }
}
