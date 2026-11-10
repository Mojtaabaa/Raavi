using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Raavi.AI.Persistence;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Domain.Common;

namespace Raavi.AI.PromptHub;

public sealed record PromptSummaryDto(
    Guid Id, string Title, string? Description, Guid? CategoryId, PromptVisibility Visibility, PromptStatus Status, IReadOnlyList<string> Tags, string Language,
    IReadOnlyList<string> Contexts, PromptModes Modes, Guid OwnerId, string? OwnerName, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt, DateTimeOffset? LastUsedAt,
    bool IsOwner, bool CanEdit, bool HasDraft, int LatestVersion, Guid? ForkedFromId, string? ForkedFromTitle, int Revision,
    double? RatingAverage = null, int RatingCount = 0, int? MyRating = null, int Uses = 0);

public sealed record PromptVersionDto(Guid Id, int Number, bool IsPublished, string? ChangeNote, Guid CreatedBy, string? CreatedByName, DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt);

/// <summary>A prompt with the text the caller may see: owners get their draft (else the published version), everybody else the published version.</summary>
public sealed record PromptDetailDto(PromptSummaryDto Prompt, System.Text.Json.JsonElement Content, string ContentText, PromptVersionDto? Shown, PromptVersionDto? Draft, PromptVersionDto? Published, IReadOnlyList<PromptInputDefinition> Variables);

public sealed record PagedPrompts(IReadOnlyList<PromptSummaryDto> Items, int Total, int Skip, int Take);

public sealed record PromptInput(string? Title, string? Description, Guid? CategoryId, IReadOnlyList<string>? Tags, string? Language, IReadOnlyList<string>? Contexts, PromptModes? Modes, System.Text.Json.JsonElement? Content, string? ChangeNote, int? Revision, IReadOnlyList<PromptInputDefinition>? Variables = null);

public sealed record PromptQuery(string Scope, string? Q, Guid? CategoryId, bool IncludeSubcategories, string? Context, PromptStatus? Status, string? Language, string? Tag, string? Sort, int Skip, int Take);

public sealed record CategoryDto(Guid Id, Guid? ParentId, string Name, string? Description, int SortOrder, CategoryStatus Status, int MinePrompts, int PublicPrompts);

public sealed record CategoryInput(string? Name, string? Description, Guid? ParentId);

public sealed record MoveCategoryRequest(Guid? ParentId, int? Index);

public sealed record DeleteCategoryRequest(string? Strategy, Guid? TargetCategoryId);

public sealed record MovePromptsRequest(IReadOnlyList<Guid>? Ids, Guid? CategoryId);

public static class PromptErrors
{
    public static readonly Error NotFound = new("prompt.not_found", "Prompt not found.", ErrorKind.NotFound);
    public static readonly Error Forbidden = new("prompt.forbidden", "You do not have permission to do this.", ErrorKind.Forbidden);
    public static readonly Error Conflict = new("prompt.version_conflict", "The prompt was changed by someone else. Reload it and try again.", ErrorKind.Conflict);
    public static readonly Error CategoryNotFound = new("prompt.category_not_found", "Category not found.", ErrorKind.NotFound);
    public static readonly Error CategoryInvalidMove = new("prompt.category_invalid_move", "A category cannot be moved into itself or below its own children, or deeper than the tree allows.", ErrorKind.Validation);
    public static readonly Error StrategyRequired = new("prompt.category_strategy_required", "Choose what happens to the prompts of the category.", ErrorKind.Validation);
    public static readonly Error NotPublished = new("prompt.not_published", "Publish a version of the prompt first.", ErrorKind.Conflict);
    public static readonly Error NothingToPublish = new("prompt.nothing_to_publish", "There is no draft to publish.", ErrorKind.Conflict);
    public static readonly Error PublishDenied = new("prompt.publish_denied", "Sharing prompts with the whole workspace is restricted to administrators here.", ErrorKind.Forbidden);
    public static readonly Error NotArchived = new("prompt.not_archived", "Archive the prompt before deleting it for good.", ErrorKind.Conflict);
}

/// <summary>
/// Prompt Hub library (ADR-052): personal and workspace-public prompts with a category tree, draft/published versions and an append-only event log.
/// Authorization is decided here on the server for every call: owners manage their prompts, everybody in the workspace reads published public prompts,
/// workspace administrators moderate (archive, unpublish, deprecate) but cannot edit other people's text. The tenant boundary is enforced by the
/// database context (query filter + row-level security), so ids of another workspace simply do not exist.
/// </summary>
public sealed class PromptHubService(AiDbContext db, ICurrentUser current, IPrincipalDirectory directory, IClock clock, IConfiguration configuration, PromptContextRegistry registry)
{
    public const int MaxPageSize = 100;

    private bool MembersMayManageCategories => configuration.GetValue("Raavi:Prompts:MembersMayManageCategories", false);

    private bool MembersMayPublish => configuration.GetValue("Raavi:Prompts:MembersMayPublish", true);

    private async Task<PrincipalSnapshot> MeAsync(CancellationToken ct) => await directory.GetPrincipalAsync(current.UserId, cancellationToken: ct).ConfigureAwait(false);

    // ── Prompts: reading ───────────────────────────────────────────────────────────────────────

    private static IQueryable<Prompt> PublicOnes(IQueryable<Prompt> q) =>
        q.Where(p => p.Visibility == PromptVisibility.Tenant && (p.Status == PromptStatus.Published || p.Status == PromptStatus.Deprecated) && p.PublishedVersionId != null);

    public async Task<Result<PagedPrompts>> ListAsync(PromptQuery query, CancellationToken ct)
    {
        var me = await MeAsync(ct).ConfigureAwait(false);
        if (!me.IsActiveMember)
        {
            return Result<PagedPrompts>.Failure(PromptErrors.Forbidden);
        }

        var take = Math.Clamp(query.Take <= 0 ? 25 : query.Take, 1, MaxPageSize);
        var skip = Math.Max(0, query.Skip);
        var mine = string.Equals(query.Scope, "mine", StringComparison.OrdinalIgnoreCase);
        IQueryable<Prompt> q = db.Prompts.AsNoTracking();
        if (mine)
        {
            q = q.Where(p => p.OwnerId == me.UserId);
            q = query.Status is { } s ? q.Where(p => p.Status == s) : q.Where(p => p.Status != PromptStatus.Archived);
        }
        else
        {
            q = PublicOnes(q);
            if (query.Status is PromptStatus.Deprecated or PromptStatus.Published)
            {
                var s = query.Status.Value;
                q = q.Where(p => p.Status == s);
            }
        }

        if (query.CategoryId is { } cat)
        {
            var ids = new List<Guid> { cat };
            if (query.IncludeSubcategories)
            {
                ids = Descendants(await db.PromptCategories.AsNoTracking().Select(c => new CatNode(c.Id, c.ParentId)).ToListAsync(ct).ConfigureAwait(false), cat);
            }

            q = q.Where(p => p.CategoryId != null && ids.Contains(p.CategoryId.Value));
        }

        if (!string.IsNullOrWhiteSpace(query.Context))
        {
            var ctx = query.Context.Trim().ToLowerInvariant();
            q = q.Where(p => ("," + p.ContextKinds + ",").Contains("," + ctx + ","));
        }

        if (!string.IsNullOrWhiteSpace(query.Language) && query.Language != "any")
        {
            var lang = query.Language.Trim().ToLowerInvariant();
            q = q.Where(p => p.Language == lang || p.Language == "any");
        }

        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            var tag = query.Tag.Trim().ToLowerInvariant();
            q = q.Where(p => ("," + p.Tags + ",").Contains("," + tag + ","));
        }

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var variants = SearchText.Variants(query.Q.Trim()).Take(4).ToList();
            q = q.Where(p => variants.Any(v => p.Title.Contains(v) || (p.Description != null && p.Description.Contains(v)) || p.Tags.Contains(v))
                || db.PromptVersions.Any(v => v.PromptId == p.Id && (v.Id == p.PublishedVersionId || (mine && v.Id == p.DraftVersionId)) && variants.Any(x => v.ContentText.Contains(x))));
        }

        var total = await q.CountAsync(ct).ConfigureAwait(false);
        q = query.Sort switch
        {
            "title" => q.OrderBy(p => p.Title).ThenBy(p => p.Id),
            "updated" => q.OrderByDescending(p => p.UpdatedAt).ThenBy(p => p.Id),
            "rated" => q.OrderByDescending(p => db.PromptRatings.Where(r => r.PromptId == p.Id).Average(r => (double?)r.Stars) ?? 0).ThenByDescending(p => db.PromptRatings.Count(r => r.PromptId == p.Id)).ThenBy(p => p.Id),
            "popular" => q.OrderByDescending(p => db.PromptEvents.Count(e => e.PromptId == p.Id && (e.Type == PromptUsageEventTypes.ExecutionStarted || e.Type == PromptUsageEventTypes.Copied || e.Type == PromptUsageEventTypes.ManualUseConfirmed))).ThenBy(p => p.Id),
            "used" => q.OrderByDescending(p => db.PromptEvents.Count(e => e.PromptId == p.Id && e.Type == PromptUsageEventTypes.ExecutionStarted)).ThenBy(p => p.Id),
            _ => q.OrderByDescending(p => p.PublishedAt ?? p.CreatedAt).ThenBy(p => p.Id),
        };
        var rows = await q.Skip(skip).Take(take).ToListAsync(ct).ConfigureAwait(false);
        return Result<PagedPrompts>.Success(new PagedPrompts(await ToSummariesAsync(rows, me, ct).ConfigureAwait(false), total, skip, take));
    }

    private static List<Guid> Descendants(List<CatNode> rows, Guid root)
    {
        var result = new List<Guid> { root };
        for (var i = 0; i < result.Count && i < 10_000; i++)
        {
            result.AddRange(rows.Where(x => x.ParentId == result[i]).Select(x => x.Id));
        }

        return result;
    }

    public async Task<Result<PromptDetailDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var me = await MeAsync(ct).ConfigureAwait(false);
        var prompt = await db.Prompts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false);
        if (prompt is null || !me.IsActiveMember || !CanSee(prompt, me))
        {
            return Result<PromptDetailDto>.Failure(PromptErrors.NotFound);
        }

        var ids = new[] { prompt.DraftVersionId, prompt.PublishedVersionId }.Where(x => x != null).Select(x => x!.Value).ToList();
        var versions = await db.PromptVersions.AsNoTracking().Where(v => ids.Contains(v.Id)).ToListAsync(ct).ConfigureAwait(false);
        var draft = versions.FirstOrDefault(v => v.Id == prompt.DraftVersionId);
        var published = versions.FirstOrDefault(v => v.Id == prompt.PublishedVersionId);
        var isOwner = prompt.OwnerId == me.UserId;
        var shown = isOwner ? draft ?? published : published;
        var names = await directory.GetUserNamesAsync([.. versions.Select(v => v.CreatedBy), prompt.OwnerId], ct).ConfigureAwait(false);
        PromptVersionDto? Dto(PromptVersion? v) => v is null ? null : new PromptVersionDto(v.Id, v.Number, v.IsPublished, v.ChangeNote, v.CreatedBy, names.GetValueOrDefault(v.CreatedBy), v.CreatedAt, v.PublishedAt);
        var summary = (await ToSummariesAsync([prompt], me, ct).ConfigureAwait(false))[0];
        return Result<PromptDetailDto>.Success(new PromptDetailDto(summary, System.Text.Json.JsonDocument.Parse(shown?.ContentJson ?? EmptyDocument).RootElement.Clone(), shown?.ContentText ?? string.Empty, Dto(shown), isOwner ? Dto(draft) : null, Dto(published), PromptInputs.Parse(shown?.VariablesJson)));
    }

    private const string EmptyDocument = "{\"schema\":\"raavi.doc\",\"schemaVersion\":2,\"doc\":{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\"}]}}";

    internal static bool CanSee(Prompt p, PrincipalSnapshot me) =>
        p.OwnerId == me.UserId || (p.Visibility == PromptVisibility.Tenant && p.PublishedVersionId != null && p.Status is PromptStatus.Published or PromptStatus.Deprecated)
        || (me.IsTenantAdministrator && p.Visibility == PromptVisibility.Tenant);

    internal async Task<PromptSummaryDto> ListOneAsync(Prompt prompt, PrincipalSnapshot me, CancellationToken ct) => (await ToSummariesAsync([prompt], me, ct).ConfigureAwait(false))[0];

    private async Task<IReadOnlyList<PromptSummaryDto>> ToSummariesAsync(IReadOnlyList<Prompt> rows, PrincipalSnapshot me, CancellationToken ct)
    {
        var names = await directory.GetUserNamesAsync([.. rows.Select(r => r.OwnerId).Distinct()], ct).ConfigureAwait(false);
        var ids = rows.Select(r => r.Id).ToList();
        var ratings = await db.PromptRatings.AsNoTracking().Where(r => ids.Contains(r.PromptId)).GroupBy(r => r.PromptId).Select(g => new { Id = g.Key, Avg = g.Average(x => (double)x.Stars), N = g.Count() }).ToDictionaryAsync(x => x.Id, ct).ConfigureAwait(false);
        var mine = await db.PromptRatings.AsNoTracking().Where(r => ids.Contains(r.PromptId) && r.UserId == me.UserId).ToDictionaryAsync(r => r.PromptId, r => r.Stars, ct).ConfigureAwait(false);
        var uses = await db.PromptEvents.AsNoTracking().Where(e => e.PromptId != null && ids.Contains(e.PromptId.Value) && (e.Type == PromptUsageEventTypes.ExecutionStarted || e.Type == PromptUsageEventTypes.ManualUseConfirmed))
            .GroupBy(e => e.PromptId!.Value).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct).ConfigureAwait(false);
        return rows.Select(p => new PromptSummaryDto(
            p.Id, p.Title, p.Description, p.CategoryId, p.Visibility, p.Status, p.TagList, p.Language, p.ContextList, p.Modes, p.OwnerId, names.GetValueOrDefault(p.OwnerId),
            p.CreatedAt, p.UpdatedAt, p.PublishedAt, p.LastUsedAt, p.OwnerId == me.UserId, p.OwnerId == me.UserId, p.DraftVersionId != null, p.LatestVersionNumber, p.ForkedFromId, p.ForkedFromTitle, p.Revision,
            ratings.TryGetValue(p.Id, out var rt) ? Math.Round(rt.Avg, 2) : null, ratings.TryGetValue(p.Id, out var rc) ? rc.N : 0, mine.TryGetValue(p.Id, out var my) ? my : null, uses.GetValueOrDefault(p.Id))).ToList();
    }

    // ── Prompts: writing ───────────────────────────────────────────────────────────────────────

    public async Task<Result<PromptDetailDto>> CreateAsync(PromptInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var me = await MeAsync(ct).ConfigureAwait(false);
        if (!me.IsActiveMember)
        {
            return Result<PromptDetailDto>.Failure(PromptErrors.Forbidden);
        }

        var now = clock.UtcNow;
        try
        {
            if (input.CategoryId is { } cid && !await db.PromptCategories.AnyAsync(c => c.Id == cid && c.Status == CategoryStatus.Active, ct).ConfigureAwait(false))
            {
                return Result<PromptDetailDto>.Failure(PromptErrors.CategoryNotFound);
            }

            if (input.Variables is not null)
            {
                PromptInputs.Validate(input.Variables);
            }

            var prompt = Prompt.Create(me.UserId, input.Title ?? string.Empty, input.Description, input.CategoryId, input.Tags, input.Language, input.Contexts, input.Modes, now);
            var version = PromptVersion.CreateDraft(prompt.Id, 1, input.Content?.GetRawText() ?? EmptyDocument, input.ChangeNote, me.UserId, now, input.Variables is null ? null : PromptInputs.Serialize(input.Variables));
            db.Prompts.Add(prompt);
            db.PromptVersions.Add(version);
            prompt.SetDraft(version.Id, 1, now);
            db.PromptEvents.Add(PromptEvent.Create(PromptEventTypes.Created, prompt.Id, version.Id, me.UserId, now));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return await GetAsync(prompt.Id, ct).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return Fail<PromptDetailDto>(ex);
        }
    }

    /// <summary>Edits details and, when content is sent, the draft (a new draft is started from the published text if there is none). Owner only.</summary>
    public async Task<Result<PromptDetailDto>> UpdateAsync(Guid id, PromptInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var (prompt, me, error) = await OwnedAsync<PromptDetailDto>(id, ct).ConfigureAwait(false);
        if (prompt is null)
        {
            return error!;
        }

        if (input.Revision is { } rev && rev != prompt.Revision)
        {
            return Result<PromptDetailDto>.Failure(PromptErrors.Conflict);
        }

        var now = clock.UtcNow;
        try
        {
            if (input.CategoryId is { } cid && cid != prompt.CategoryId && !await db.PromptCategories.AnyAsync(c => c.Id == cid && c.Status == CategoryStatus.Active, ct).ConfigureAwait(false))
            {
                return Result<PromptDetailDto>.Failure(PromptErrors.CategoryNotFound);
            }

            prompt.SetDetails(input.Title ?? prompt.Title, input.Description ?? prompt.Description, input.CategoryId ?? prompt.CategoryId, input.Tags ?? prompt.TagList, input.Language ?? prompt.Language, input.Contexts ?? prompt.ContextList, input.Modes ?? prompt.Modes, now);
            if (input.Content is not null || input.Variables is not null)
            {
                if (input.Variables is not null)
                {
                    PromptInputs.Validate(input.Variables);
                }

                var variablesJson = input.Variables is null ? null : PromptInputs.Serialize(input.Variables);
                PromptVersion? draft = prompt.DraftVersionId is { } did ? await db.PromptVersions.FirstOrDefaultAsync(v => v.Id == did, ct).ConfigureAwait(false) : null;
                if (draft is null)
                {
                    var basis = prompt.PublishedVersionId is { } pid ? await db.PromptVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == pid, ct).ConfigureAwait(false) : null;
                    draft = PromptVersion.CreateDraft(prompt.Id, prompt.LatestVersionNumber + 1, input.Content?.GetRawText() ?? basis?.ContentJson ?? EmptyDocument, input.ChangeNote, me.UserId, now, variablesJson ?? basis?.VariablesJson);
                    db.PromptVersions.Add(draft);
                    prompt.SetDraft(draft.Id, draft.Number, now);
                }
                else
                {
                    draft.ReplaceContent(input.Content?.GetRawText() ?? draft.ContentJson, input.ChangeNote ?? draft.ChangeNote, variablesJson);
                }
            }

            db.PromptEvents.Add(PromptEvent.Create(PromptEventTypes.Updated, prompt.Id, null, me.UserId, now));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return Fail<PromptDetailDto>(ex);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result<PromptDetailDto>.Failure(PromptErrors.Conflict);
        }

        return await GetAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>Publishes the draft: it becomes an immutable version and the one everybody (who may see the prompt) reads.</summary>
    public async Task<Result<PromptDetailDto>> PublishAsync(Guid id, string? changeNote, CancellationToken ct)
    {
        var (prompt, me, error) = await OwnedAsync<PromptDetailDto>(id, ct).ConfigureAwait(false);
        if (prompt is null)
        {
            return error!;
        }

        if (prompt.DraftVersionId is not { } did)
        {
            return Result<PromptDetailDto>.Failure(PromptErrors.NothingToPublish);
        }

        var draft = await db.PromptVersions.FirstAsync(v => v.Id == did, ct).ConfigureAwait(false);
        var now = clock.UtcNow;
        try
        {
            // a published text must be a valid template that only uses variables Raavi knows (or the author defined)
            var parsed = PromptTemplate.Parse(draft.ContentText);
            if (parsed.Errors.Count > 0)
            {
                return Result<PromptDetailDto>.Failure(new Error("prompt.template_invalid", "The prompt text has a template error: " + string.Join("; ", parsed.Errors), ErrorKind.Validation));
            }

            var defined = PromptInputs.Parse(draft.VariablesJson).Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
            var unsupported = parsed.Names.Where(n => !registry.IsKnown(n) || (PromptCatalog.IsInput(n) && !defined.Contains(n))).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (unsupported.Count > 0)
            {
                return Result<PromptDetailDto>.Failure(new Error("prompt.variable_unsupported", "Unsupported variables: " + string.Join(", ", unsupported), ErrorKind.Validation));
            }

            if (!string.IsNullOrWhiteSpace(changeNote))
            {
                draft.ReplaceContent(draft.ContentJson, changeNote);
            }

            draft.Publish(now);
            prompt.Publish(draft, now);
            db.PromptEvents.Add(PromptEvent.Create(PromptEventTypes.Published, prompt.Id, draft.Id, me.UserId, now, details: $"v{draft.Number}"));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return Fail<PromptDetailDto>(ex);
        }

        return await GetAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>Personal ↔ workspace-public. Making a prompt public needs a published version and (configurably) administrator rights; hiding it again is always allowed to the owner and to administrators.</summary>
    public async Task<Result<PromptDetailDto>> SetVisibilityAsync(Guid id, PromptVisibility visibility, CancellationToken ct)
    {
        var me = await MeAsync(ct).ConfigureAwait(false);
        var prompt = await db.Prompts.FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false);
        if (prompt is null || !me.IsActiveMember || !CanSee(prompt, me))
        {
            return Result<PromptDetailDto>.Failure(PromptErrors.NotFound);
        }

        var isOwner = prompt.OwnerId == me.UserId;
        if (!isOwner && !(me.IsTenantAdministrator && visibility == PromptVisibility.Personal))
        {
            return Result<PromptDetailDto>.Failure(PromptErrors.Forbidden);
        }

        if (visibility == PromptVisibility.Tenant)
        {
            if (!MembersMayPublish && !me.IsTenantAdministrator)
            {
                return Result<PromptDetailDto>.Failure(PromptErrors.PublishDenied);
            }

            if (prompt.PublishedVersionId is null || prompt.Status is PromptStatus.Archived)
            {
                return Result<PromptDetailDto>.Failure(PromptErrors.NotPublished);
            }
        }

        if (prompt.Visibility != visibility)
        {
            var now = clock.UtcNow;
            prompt.SetVisibility(visibility, now);
            db.PromptEvents.Add(PromptEvent.Create(PromptEventTypes.VisibilityChanged, prompt.Id, null, me.UserId, now, details: visibility.ToString()));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return await GetAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>Archive / restore / deprecate. The owner may do all three; administrators may archive and deprecate public prompts (moderation).</summary>
    public async Task<Result<PromptDetailDto>> SetStatusAsync(Guid id, PromptStatus status, CancellationToken ct)
    {
        var me = await MeAsync(ct).ConfigureAwait(false);
        var prompt = await db.Prompts.FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false);
        if (prompt is null || !me.IsActiveMember || !CanSee(prompt, me))
        {
            return Result<PromptDetailDto>.Failure(PromptErrors.NotFound);
        }

        var isOwner = prompt.OwnerId == me.UserId;
        var moderation = me.IsTenantAdministrator && prompt.Visibility == PromptVisibility.Tenant && status is PromptStatus.Archived or PromptStatus.Deprecated;
        if (!isOwner && !moderation)
        {
            return Result<PromptDetailDto>.Failure(PromptErrors.Forbidden);
        }

        if (status == PromptStatus.Draft)
        {
            return Result<PromptDetailDto>.Failure(PromptErrors.Forbidden);
        }

        var wasArchived = prompt.Status == PromptStatus.Archived;
        // Restoring brings back "Published" when a published version exists, else "Draft".
        var target = status == PromptStatus.Published && prompt.PublishedVersionId is null ? PromptStatus.Draft : status;
        var now = clock.UtcNow;
        prompt.SetStatus(target, now);
        if (target == PromptStatus.Archived && prompt.Visibility == PromptVisibility.Tenant && isOwner)
        {
            prompt.SetVisibility(PromptVisibility.Personal, now); // the owner archiving takes the prompt out of the library; a moderator's archive keeps it visible to administrators
        }

        db.PromptEvents.Add(PromptEvent.Create(wasArchived && target != PromptStatus.Archived ? PromptEventTypes.Restored : PromptEventTypes.Archived, prompt.Id, null, me.UserId, now, details: target.ToString()));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await GetAsync(id, ct).ConfigureAwait(false);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct)
    {
        var me = await MeAsync(ct).ConfigureAwait(false);
        var prompt = await db.Prompts.FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false);
        if (prompt is null || !me.IsActiveMember || !CanSee(prompt, me))
        {
            return Result.Failure(PromptErrors.NotFound);
        }

        if (prompt.OwnerId != me.UserId && !me.IsTenantAdministrator)
        {
            return Result.Failure(PromptErrors.Forbidden);
        }

        if (prompt.Status != PromptStatus.Archived)
        {
            return Result.Failure(PromptErrors.NotArchived);
        }

        await db.PromptVersions.Where(v => v.PromptId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        db.Prompts.Remove(prompt);
        db.PromptEvents.Add(PromptEvent.Create(PromptEventTypes.Deleted, id, null, me.UserId, clock.UtcNow, details: prompt.Title));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result<PromptDetailDto>> DuplicateAsync(Guid id, CancellationToken ct)
    {
        var (prompt, me, error) = await OwnedAsync<PromptDetailDto>(id, ct).ConfigureAwait(false);
        if (prompt is null)
        {
            return error!;
        }

        var src = prompt.DraftVersionId ?? prompt.PublishedVersionId;
        var content = src is { } sid ? await db.PromptVersions.AsNoTracking().Where(v => v.Id == sid).Select(v => v.ContentJson).FirstOrDefaultAsync(ct).ConfigureAwait(false) : null;
        var copyTitle = prompt.Title.Length > Prompt.MaxTitleLength - 7 ? prompt.Title[..(Prompt.MaxTitleLength - 7)] : prompt.Title;
        return await CreateAsync(new PromptInput(copyTitle + " (copy)", prompt.Description, prompt.CategoryId, prompt.TagList, prompt.Language, prompt.ContextList, prompt.Modes, content is null ? null : System.Text.Json.JsonDocument.Parse(content).RootElement.Clone(), null, null), ct).ConfigureAwait(false);
    }

    public async Task<Result<int>> MoveManyAsync(MovePromptsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var me = await MeAsync(ct).ConfigureAwait(false);
        if (!me.IsActiveMember)
        {
            return Result<int>.Failure(PromptErrors.Forbidden);
        }

        if (request.CategoryId is { } cid && !await db.PromptCategories.AnyAsync(c => c.Id == cid && c.Status == CategoryStatus.Active, ct).ConfigureAwait(false))
        {
            return Result<int>.Failure(PromptErrors.CategoryNotFound);
        }

        var ids = (request.Ids ?? []).Distinct().Take(500).ToList();
        var rows = await db.Prompts.Where(p => ids.Contains(p.Id) && p.OwnerId == me.UserId).ToListAsync(ct).ConfigureAwait(false); // only your own prompts
        var now = clock.UtcNow;
        foreach (var p in rows)
        {
            p.MoveTo(request.CategoryId, now);
            db.PromptEvents.Add(PromptEvent.Create(PromptEventTypes.Moved, p.Id, null, me.UserId, now));
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<int>.Success(rows.Count);
    }

    private async Task<(Prompt? Prompt, PrincipalSnapshot Me, Result<T>? Error)> OwnedAsync<T>(Guid id, CancellationToken ct)
    {
        var me = await MeAsync(ct).ConfigureAwait(false);
        var prompt = me.IsActiveMember ? await db.Prompts.FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false) : null;
        if (prompt is null || !CanSee(prompt, me))
        {
            return (null, me, Result<T>.Failure(PromptErrors.NotFound));
        }

        return prompt.OwnerId == me.UserId ? (prompt, me, null) : (null, me, Result<T>.Failure(PromptErrors.Forbidden));
    }

    private static Result<T> Fail<T>(DomainException ex) => Result<T>.Failure(new Error(ex.Code, ex.Message, ex.Code.EndsWith("immutable", StringComparison.Ordinal) ? ErrorKind.Conflict : ErrorKind.Validation));

    // ── Categories ─────────────────────────────────────────────────────────────────────────────

    private async Task<(PrincipalSnapshot Me, bool CanManage)> CategoryRightsAsync(CancellationToken ct)
    {
        var me = await MeAsync(ct).ConfigureAwait(false);
        return (me, me.IsActiveMember && (me.IsTenantAdministrator || MembersMayManageCategories));
    }

    public async Task<Result<IReadOnlyList<CategoryDto>>> CategoriesAsync(bool includeArchived, CancellationToken ct)
    {
        var me = await MeAsync(ct).ConfigureAwait(false);
        if (!me.IsActiveMember)
        {
            return Result<IReadOnlyList<CategoryDto>>.Failure(PromptErrors.Forbidden);
        }

        var cats = await db.PromptCategories.AsNoTracking().Where(c => includeArchived || c.Status == CategoryStatus.Active).OrderBy(c => c.ParentId).ThenBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct).ConfigureAwait(false);
        var mineCounts = await db.Prompts.AsNoTracking().Where(p => p.OwnerId == me.UserId && p.Status != PromptStatus.Archived && p.CategoryId != null).GroupBy(p => p.CategoryId!.Value).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct).ConfigureAwait(false);
        var publicCounts = await PublicOnes(db.Prompts.AsNoTracking()).Where(p => p.CategoryId != null).GroupBy(p => p.CategoryId!.Value).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct).ConfigureAwait(false);
        return Result<IReadOnlyList<CategoryDto>>.Success(cats.Select(c => new CategoryDto(c.Id, c.ParentId, c.Name, c.Description, c.SortOrder, c.Status, mineCounts.GetValueOrDefault(c.Id), publicCounts.GetValueOrDefault(c.Id))).ToList());
    }

    public async Task<Result<CategoryDto>> CreateCategoryAsync(CategoryInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var (me, can) = await CategoryRightsAsync(ct).ConfigureAwait(false);
        if (!can)
        {
            return Result<CategoryDto>.Failure(PromptErrors.Forbidden);
        }

        var all = await db.PromptCategories.Select(c => new CatNode(c.Id, c.ParentId)).ToListAsync(ct).ConfigureAwait(false);
        if (input.ParentId is { } pid && (all.All(c => c.Id != pid) || DepthOf(all, pid) + 1 >= PromptCategory.MaxDepth))
        {
            return Result<CategoryDto>.Failure(input.ParentId is null || all.Any(c => c.Id == pid) ? PromptErrors.CategoryInvalidMove : PromptErrors.CategoryNotFound);
        }

        try
        {
            var now = clock.UtcNow;
            var siblings = await db.PromptCategories.CountAsync(c => c.ParentId == input.ParentId, ct).ConfigureAwait(false);
            var cat = PromptCategory.Create(input.ParentId, input.Name ?? string.Empty, input.Description, siblings, me.UserId, now);
            db.PromptCategories.Add(cat);
            db.PromptEvents.Add(PromptEvent.Create(PromptEventTypes.CategoryCreated, null, null, me.UserId, now, details: cat.Name));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<CategoryDto>.Success(new CategoryDto(cat.Id, cat.ParentId, cat.Name, cat.Description, cat.SortOrder, cat.Status, 0, 0));
        }
        catch (DomainException ex)
        {
            return Fail<CategoryDto>(ex);
        }
    }

    public async Task<Result<CategoryDto>> EditCategoryAsync(Guid id, CategoryInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var (me, can) = await CategoryRightsAsync(ct).ConfigureAwait(false);
        if (!can)
        {
            return Result<CategoryDto>.Failure(PromptErrors.Forbidden);
        }

        var cat = await db.PromptCategories.FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false);
        if (cat is null)
        {
            return Result<CategoryDto>.Failure(PromptErrors.CategoryNotFound);
        }

        try
        {
            var now = clock.UtcNow;
            cat.Edit(input.Name ?? cat.Name, input.Description, now);
            db.PromptEvents.Add(PromptEvent.Create(PromptEventTypes.CategoryEdited, null, null, me.UserId, now, details: cat.Name));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<CategoryDto>.Success(new CategoryDto(cat.Id, cat.ParentId, cat.Name, cat.Description, cat.SortOrder, cat.Status, 0, 0));
        }
        catch (DomainException ex)
        {
            return Fail<CategoryDto>(ex);
        }
    }

    private sealed record CatNode(Guid Id, Guid? ParentId);

    private static int DepthOf(List<CatNode> all, Guid id)
    {
        var depth = 0;
        Guid? cur = id;
        while (cur is { } c && depth < 100)
        {
            cur = all.FirstOrDefault(x => x.Id == c)?.ParentId;
            if (cur is not null)
            {
                depth++;
            }
        }

        return depth;
    }

    private static int Height(List<CatNode> all, Guid id) => 1 + all.Where(x => x.ParentId == id).Select(x => Height(all, x.Id)).DefaultIfEmpty(0).Max();

    /// <summary>Moves a category under another parent (or to the root) at a position among its siblings; refuses cycles and trees deeper than the limit.</summary>
    public async Task<Result<IReadOnlyList<CategoryDto>>> MoveCategoryAsync(Guid id, MoveCategoryRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (me, can) = await CategoryRightsAsync(ct).ConfigureAwait(false);
        if (!can)
        {
            return Result<IReadOnlyList<CategoryDto>>.Failure(PromptErrors.Forbidden);
        }

        var cats = await db.PromptCategories.ToListAsync(ct).ConfigureAwait(false);
        var node = cats.FirstOrDefault(c => c.Id == id);
        if (node is null || (request.ParentId is { } p && cats.All(c => c.Id != p)))
        {
            return Result<IReadOnlyList<CategoryDto>>.Failure(PromptErrors.CategoryNotFound);
        }

        var tree = cats.Select(c => new CatNode(c.Id, c.ParentId)).ToList();
        if (request.ParentId is { } parent)
        {
            // the new parent must not be the node itself or one of its descendants
            for (Guid? cur = parent; cur is not null;)
            {
                if (cur == id)
                {
                    return Result<IReadOnlyList<CategoryDto>>.Failure(PromptErrors.CategoryInvalidMove);
                }

                cur = tree.First(x => x.Id == cur).ParentId;
            }

            if (DepthOf(tree, parent) + 1 + Height(tree, id) > PromptCategory.MaxDepth)
            {
                return Result<IReadOnlyList<CategoryDto>>.Failure(PromptErrors.CategoryInvalidMove);
            }
        }

        var now = clock.UtcNow;
        var siblings = cats.Where(c => c.ParentId == request.ParentId && c.Id != id).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToList();
        var at = Math.Clamp(request.Index ?? siblings.Count, 0, siblings.Count);
        siblings.Insert(at, node);
        for (var i = 0; i < siblings.Count; i++)
        {
            if (siblings[i].Id == id || siblings[i].SortOrder != i)
            {
                siblings[i].Place(request.ParentId, i, now);
            }
        }

        db.PromptEvents.Add(PromptEvent.Create(PromptEventTypes.CategoryMoved, null, null, me.UserId, now, details: node.Name));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await CategoriesAsync(true, ct).ConfigureAwait(false);
    }

    public async Task<Result<IReadOnlyList<CategoryDto>>> SetCategoryArchivedAsync(Guid id, bool archived, CancellationToken ct)
    {
        var (me, can) = await CategoryRightsAsync(ct).ConfigureAwait(false);
        if (!can)
        {
            return Result<IReadOnlyList<CategoryDto>>.Failure(PromptErrors.Forbidden);
        }

        var cat = await db.PromptCategories.FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false);
        if (cat is null)
        {
            return Result<IReadOnlyList<CategoryDto>>.Failure(PromptErrors.CategoryNotFound);
        }

        cat.SetStatus(archived ? CategoryStatus.Archived : CategoryStatus.Active, clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await CategoriesAsync(true, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes a category. Its prompts and child categories are never deleted: the caller must choose where they go (parent, another category, or uncategorized).</summary>
    public async Task<Result<IReadOnlyList<CategoryDto>>> DeleteCategoryAsync(Guid id, DeleteCategoryRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (me, can) = await CategoryRightsAsync(ct).ConfigureAwait(false);
        if (!can)
        {
            return Result<IReadOnlyList<CategoryDto>>.Failure(PromptErrors.Forbidden);
        }

        var cats = await db.PromptCategories.ToListAsync(ct).ConfigureAwait(false);
        var node = cats.FirstOrDefault(c => c.Id == id);
        if (node is null)
        {
            return Result<IReadOnlyList<CategoryDto>>.Failure(PromptErrors.CategoryNotFound);
        }

        Guid? target;
        switch ((request.Strategy ?? string.Empty).ToLowerInvariant())
        {
            case "parent":
                target = node.ParentId;
                break;
            case "uncategorized":
                target = null;
                break;
            case "category":
                if (request.TargetCategoryId is not { } t || t == id || cats.All(c => c.Id != t))
                {
                    return Result<IReadOnlyList<CategoryDto>>.Failure(PromptErrors.CategoryNotFound);
                }

                // not into the category's own subtree, which is about to be reattached
                var tree = cats.Select(c => new CatNode(c.Id, c.ParentId)).ToList();
                for (Guid? cur = t; cur is not null; cur = tree.First(x => x.Id == cur).ParentId)
                {
                    if (cur == id)
                    {
                        return Result<IReadOnlyList<CategoryDto>>.Failure(PromptErrors.CategoryInvalidMove);
                    }
                }

                target = t;
                break;
            default:
                return Result<IReadOnlyList<CategoryDto>>.Failure(PromptErrors.StrategyRequired);
        }

        var now = clock.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await db.Prompts.Where(p => p.CategoryId == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.CategoryId, target), ct).ConfigureAwait(false);
        var order = cats.Count(c => c.ParentId == (request.Strategy == "category" ? target : node.ParentId) && c.Id != id);
        foreach (var child in cats.Where(c => c.ParentId == id).OrderBy(c => c.SortOrder))
        {
            child.Place(request.Strategy == "uncategorized" ? null : request.Strategy == "category" ? target : node.ParentId, order++, now);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.PromptCategories.Remove(node);
        db.PromptEvents.Add(PromptEvent.Create(PromptEventTypes.CategoryDeleted, null, null, me.UserId, now, details: $"{node.Name}:{request.Strategy}"));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return await CategoriesAsync(true, ct).ConfigureAwait(false);
    }
}
