using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Content;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Jobs;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Application.Abstractions.Time;
using Raavi.Domain.Common;
using Raavi.Migration.Persistence;

namespace Raavi.Migration.Confluence;

public sealed record MigrationJobArgs(Guid JobId);

/// <summary>Creates a source adapter for an address and token. Replaced by a fake in tests.</summary>
public interface IConfluenceSourceFactory
{
    IConfluenceSourceProvider Create(Uri baseUrl, string token);
}

public sealed class ConfluenceSourceFactory(IOptions<MigrationOptions> options) : IConfluenceSourceFactory
{
    public IConfluenceSourceProvider Create(Uri baseUrl, string token) =>
        new ConfluenceRestSource(new HttpClient(SourceHostPolicy.CreateHandler()), baseUrl, token, new ConfluenceRestOptions { RequestsPerSecond = options.Value.RequestsPerSecond }, ownsClient: true);
}

/// <summary>Administrator options of a job (stored as JSON on the job).</summary>
/// <param name="Reconvert">Re-download and re-convert versions that were imported before (after converter improvements); changed bodies are rewritten in place.</param>
/// <param name="MapUsersByEmail">Match source users (authors, restriction subjects) to workspace members by <b>verified</b> e-mail. Off by default: a dry run only suggests matches and the administrator confirms by turning this on.</param>
/// <param name="UserMap">Explicit mapping source user key or user name → e-mail of a workspace member (verified e-mail required).</param>
public sealed record MigrationJobOptions(IReadOnlyList<string>? Spaces, IReadOnlyDictionary<string, string>? KeyMap, bool Reconvert = false, bool MapUsersByEmail = false, IReadOnlyDictionary<string, string>? UserMap = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>Files of the source pages: found, bytes, stored, left out because they exceed the size limit, and failed downloads.</summary>
public sealed record AttachmentReport(int Found, long Bytes, int Imported, int TooLarge, int Failed, long MaxBytes, int Replaced = 0, int SizeMismatch = 0);

/// <summary>One source author and what the workspace could match: a suggestion is only applied when the administrator confirmed it.</summary>
public sealed record AuthorMapping(string SourceKey, string? UserName, string? DisplayName, string? Email, string? SuggestedUser, bool Applied);

/// <summary>Restrictions and permissions: pages that were restricted in the source, how many restrictions were applied, who could not be mapped, and the status of space permissions.</summary>
/// <summary>Comments of the source pages: found, stored, left out (empty or invalid) and the pages whose comments could not be read.</summary>
public sealed record CommentReport(int Found, int Imported, int Skipped, int Failed);

public sealed record AccessReport(int RestrictedPages, int RestrictionsApplied, IReadOnlyList<string> UnmappedSubjects, string SpacePermissions);

public sealed record SpaceReport(string SourceKey, string Name, string? RaaviKey, int Pages, int Versions, int PagesImported, int VersionsImported, int PagesFailed, bool KeyConflict);

public sealed record ValidationIssue(string SpaceKey, string Check, int Expected, int Actual);

/// <summary>Dry-run / execution report (CONFLUENCE_MIGRATION.md §2.5, §2.8). Counts only — never page content or secrets.</summary>
public sealed record MigrationReport(
    string Mode,
    string? SourceVersion,
    IReadOnlyList<SpaceReport> Spaces,
    IReadOnlyDictionary<string, int> UnsupportedMacros,
    IReadOnlyDictionary<string, int> WarningsByCode,
    int AttachmentReferences,
    int PageLinks,
    IReadOnlyList<string> UnmappedAuthors,
    int LinksResolved,
    int LinksUnresolved,
    IReadOnlyList<ValidationIssue> Validation,
    AttachmentReport Attachments,
    AccessReport Access,
    IReadOnlyList<AuthorMapping> AuthorMappings,
    CommentReport Comments);

public static class SourceKeys
{
    /// <summary>Stable short id of a source address; makes imported items' provenance unique per source.</summary>
    public static string Of(Uri baseUrl) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(baseUrl.AbsoluteUri.ToLowerInvariant())))[..12];

    public static string System(string sourceKey) => "confluence:" + sourceKey;

    /// <summary>Raavi space key from a Confluence key: upper case A–Z/0–9, starting with a letter, 2–32 characters.</summary>
    public static string SpaceKey(string confluenceKey, IReadOnlyDictionary<string, string>? keyMap)
    {
        if (keyMap is not null && keyMap.TryGetValue(confluenceKey, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
        {
            return mapped.Trim().ToUpperInvariant();
        }

        var k = new string([.. confluenceKey.ToUpperInvariant().Where(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))]);
        if (k.Length == 0 || !char.IsLetter(k[0]))
        {
            k = "S" + k;
        }

        return (k.Length < 2 ? k + "X" : k)[..Math.Min(32, Math.Max(2, k.Length))];
    }
}

/// <summary>
/// Runs one migration job inside its tenant scope (Hangfire tenant job). Idempotent and resumable: pages and versions that
/// already exist are skipped, so a restart after a pause, cancel, crash or failure continues where it stopped. A dry run
/// reads and converts everything but writes nothing to the workspace. See ADR-025.
/// </summary>
public sealed partial class ConfluenceMigrationJob : ITenantJob<MigrationJobArgs>
{
    private readonly MigrationDbContext db;
    private readonly IContentImporter importer;
    private readonly IConfluenceSourceFactory sources;
    private readonly IPrincipalDirectory directory;
    private readonly IDataProtectionProvider protection;
    private readonly ITenantContext tenant;
    private readonly IOptions<MigrationOptions> options;
    private readonly IClock clock;
    private readonly Microsoft.Extensions.Configuration.IConfiguration configuration;
    private readonly Microsoft.Extensions.Logging.ILogger<ConfluenceMigrationJob> logger;

    public ConfluenceMigrationJob(
        MigrationDbContext db,
        IContentImporter importer,
        IConfluenceSourceFactory sources,
        IPrincipalDirectory directory,
        IDataProtectionProvider protection,
        ITenantContext tenant,
        IOptions<MigrationOptions> options,
        IClock clock,
        Microsoft.Extensions.Configuration.IConfiguration configuration,
        Microsoft.Extensions.Logging.ILogger<ConfluenceMigrationJob> logger)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.db = db;
        this.importer = importer;
        this.sources = sources;
        this.directory = directory;
        this.protection = protection;
        this.tenant = tenant;
        this.options = options;
        this.clock = clock;
    }

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "Migration {JobId} failed with {Code}")]
    private static partial void LogFailed(Microsoft.Extensions.Logging.ILogger logger, Exception ex, Guid jobId, string code);

    public static string TokenPurpose(Guid tenantId) => $"Raavi.Migration.SourceToken.{tenantId:N}";

    public async Task ExecuteAsync(MigrationJobArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == args.JobId, cancellationToken).ConfigureAwait(false);
        if (job is null || job.IsFinished || job.Status == MigrationStatus.Paused)
        {
            return;
        }

        var run = new Run(this, job);
        try
        {
            await run.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // any failure ends the job with a recorded reason instead of a blind retry
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            var code = ex is ConfluenceSourceException s ? s.Code : ex is DomainException d ? d.Code : "migration.failed";
            // Unexpected errors are shown to the administrator only generically; the details (never the token) go to the operator log.
            LogFailed(logger, ex, args.JobId, code);
            await run.FailAsync(code, ex is ConfluenceSourceException or DomainException ? ex.Message : "The migration stopped because of an unexpected error.", CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed class Run(ConfluenceMigrationJob owner, MigrationJob job)
    {
        private MigrationDbContext _db => owner.db;
        private int _spacesTotal, _spacesDone, _pagesTotal, _pagesDone, _pagesFailed, _versionsDone, _warnings, _events;
        private readonly Dictionary<string, int> _macros = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _warningCodes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _authors = new(StringComparer.Ordinal);
        private readonly List<SpaceReport> _spaceReports = [];
        private int _attachmentRefs, _pageLinks, _attFound, _attImported, _attTooLarge, _attFailed, _attReplaced, _attMismatch;
        private long _attBytes;
        private bool _reconvert;
        private MigrationJobOptions _o = new(null, null);
        private int _restrictedPages, _restrictionsApplied, _comFound, _comImported, _comSkipped, _comFailed;
        private readonly HashSet<string> _unmappedSubjects = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ResolvedUser> _users = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Guid?> _groups = new(StringComparer.OrdinalIgnoreCase);

        private sealed record ResolvedUser(SourceUser? Source, DirectoryEntry? Match, bool Applied)
        {
            public Guid? RaaviId => Applied ? Match?.Id : null;
        }
        private long MaxAttachmentBytes => owner.configuration.GetValue<long?>("Raavi:Attachments:MaxBytes") ?? 10 * 1024 * 1024;

        public async Task ExecuteAsync(CancellationToken ct)
        {
            var o = JsonSerializer.Deserialize<MigrationJobOptions>(job.OptionsJson, MigrationJobOptions.Json) ?? new MigrationJobOptions(null, null);
            _reconvert = o.Reconvert;
            _o = o;
            var execute = job.Mode == MigrationMode.Execute;

            // The owner must still be an administrator of this workspace when the job runs (permissions are never cached for background work).
            var principal = await owner.directory.GetPrincipalAsync(job.CreatedBy, fresh: true, ct).ConfigureAwait(false);
            if (!principal.IsActiveMember || !principal.IsTenantAdministrator)
            {
                await FailAsync("migration.owner_not_admin", "The administrator who started this migration is no longer an administrator of the workspace.", ct).ConfigureAwait(false);
                return;
            }

            if (job.ProtectedToken is null)
            {
                await FailAsync("migration.token_missing", "The source access token is no longer available; provide it again to restart.", ct).ConfigureAwait(false);
                return;
            }

            string token;
            try
            {
                token = owner.protection.CreateProtector(TokenPurpose(owner.tenant.Tenant.TenantId)).Unprotect(job.ProtectedToken);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                await FailAsync("migration.token_unreadable", "The stored access token could not be read; provide it again to restart.", ct).ConfigureAwait(false);
                return;
            }

            var baseUrl = new Uri(job.SourceUrl);
            using var source = owner.sources.Create(baseUrl, token);
            var info = await source.DetectAsync(ct).ConfigureAwait(false);
            job.Start(owner.clock.UtcNow, info.Version + (info.BuildNumber is { } b ? $" (build {b})" : string.Empty));
            await SaveAsync("Reading the source", ct).ConfigureAwait(false);

            var system = SourceKeys.System(job.SourceKey);
            var wanted = o.Spaces is { Count: > 0 } ? o.Spaces.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
            var spaces = (await source.ListSpacesAsync(ct).ConfigureAwait(false)).Where(s => wanted is null || wanted.Contains(s.Key)).ToList();
            _spacesTotal = spaces.Count;

            // Discovery: the page tree of every selected space (cheap) gives exact totals before any content is read.
            var trees = new Dictionary<string, List<SourcePage>>(StringComparer.OrdinalIgnoreCase);
            foreach (var space in spaces)
            {
                var pages = new List<SourcePage>();
                await foreach (var p in source.StreamPagesAsync(space.Key, ct).ConfigureAwait(false))
                {
                    pages.Add(p);
                }

                trees[space.Key] = ParentFirst(pages);
                _pagesTotal += pages.Count;
            }

            await SaveAsync("Discovery finished", ct).ConfigureAwait(false);

            foreach (var space in spaces)
            {
                if (await StopRequestedAsync(ct).ConfigureAwait(false))
                {
                    return;
                }

                var pages = trees[space.Key];
                var raaviKey = SourceKeys.SpaceKey(space.Key, o.KeyMap);
                Guid spaceId = Guid.Empty;
                var conflict = false;
                if (execute)
                {
                    try
                    {
                        spaceId = (await owner.importer.EnsureSpaceAsync(new ImportSpaceRequest(system, space.Key, raaviKey, space.Name, space.Description, space.CreatedAtUtc, job.CreatedBy), ct).ConfigureAwait(false)).SpaceId;
                    }
                    catch (DomainException ex) when (ex.Code is "space.key_conflict" or "space.key_invalid")
                    {
                        conflict = true;
                        await EventAsync(MigrationEventLevel.Error, ex.Code, space.Key, ex.Message, ct).ConfigureAwait(false);
                    }
                }

                int pagesImported = 0, versionsImported = 0, failed = 0, versionsSeen = 0;
                if (conflict)
                {
                    failed = pages.Count;
                    _pagesFailed += pages.Count;
                }
                else
                {
                    foreach (var page in pages)
                    {
                        if (await StopRequestedAsync(ct).ConfigureAwait(false))
                        {
                            return;
                        }

                        try
                        {
                            var (imported, seen) = await ProcessPageAsync(source, system, spaceId, page, execute, ct).ConfigureAwait(false);
                            versionsSeen += seen;
                            versionsImported += imported;
                            pagesImported++;
                            _versionsDone += execute ? imported : seen;
                            _pagesDone++;
                        }
                        catch (ConfluenceSourceException ex) when (ex.Code is "source.unauthorized" or "source.forbidden" or "source.unreachable")
                        {
                            throw; // the whole job cannot continue
                        }
                        catch (Exception ex) when (ex is DomainException or ConfluenceSourceException or JsonException)
                        {
                            failed++;
                            _pagesFailed++;
                            await EventAsync(MigrationEventLevel.Error, ex switch { DomainException d => d.Code, ConfluenceSourceException s => s.Code, _ => "page.failed" }, $"{space.Key}/{page.SourceContentId} {page.Title}", ex.Message, ct).ConfigureAwait(false);
                        }

                        await SaveAsync($"{space.Key}: {page.Title}", ct).ConfigureAwait(false);
                    }

                    if (execute && space.HomePageSourceContentId is { } home)
                    {
                        await owner.importer.SetHomePageAsync(system, space.Key, home, job.CreatedBy, ct).ConfigureAwait(false);
                    }
                }

                _spacesDone++;
                _spaceReports.Add(new SpaceReport(space.Key, space.Name, execute ? raaviKey : null, pages.Count, pages.Sum(p => p.CurrentVersionNumber), pagesImported, versionsImported, failed, conflict));
                await SaveAsync($"{space.Key} done", ct).ConfigureAwait(false);
            }

            int resolved = 0, unresolved = 0;
            var validation = new List<ValidationIssue>();
            if (execute)
            {
                await SaveAsync("Resolving links", ct).ConfigureAwait(false);
                var links = await owner.importer.ResolveLinksAsync(system, ct).ConfigureAwait(false);
                (resolved, unresolved) = (links.Resolved, links.Unresolved);
                foreach (var u in links.Examples)
                {
                    await EventAsync(MigrationEventLevel.Warning, "link.unresolved", u.SpaceKey, $"No page '{u.Title}' was found for a link; the link was removed and its text kept.", ct).ConfigureAwait(false);
                }

                foreach (var s in _spaceReports.Where(s => !s.KeyConflict))
                {
                    var counts = await owner.importer.CountAsync(system, s.SourceKey, ct).ConfigureAwait(false);
                    var expectedPages = s.Pages - s.PagesFailed;
                    if (counts.Pages != expectedPages)
                    {
                        validation.Add(new ValidationIssue(s.SourceKey, "pages", expectedPages, counts.Pages));
                    }
                }
            }

            var report = new MigrationReport(
                job.Mode.ToString(), job.SourceVersion, _spaceReports, _macros, _warningCodes, _attachmentRefs, _pageLinks,
                [.. _authors.Order(StringComparer.Ordinal).Take(500)], resolved, unresolved, validation,
                new AttachmentReport(_attFound, _attBytes, _attImported, _attTooLarge, _attFailed, MaxAttachmentBytes, _attReplaced, _attMismatch),
                new AccessReport(_restrictedPages, _restrictionsApplied, [.. _unmappedSubjects.Order(StringComparer.Ordinal).Take(200)], "not_imported"),
                [.. _users.Values.Where(u => u.Source is not null).OrderBy(u => u.Source!.DisplayName ?? u.Source.Key, StringComparer.Ordinal).Take(200)
                    .Select(u => new AuthorMapping(u.Source!.Key, u.Source.UserName, u.Source.DisplayName, u.Source.Email, u.Match?.DisplayName, u.Applied))],
                new CommentReport(_comFound, _comImported, _comSkipped, _comFailed));
            var status = _pagesFailed > 0 || _attFailed > 0 || validation.Count > 0 ? MigrationStatus.CompletedWithErrors : MigrationStatus.Completed;
            await FinishAsync(status, JsonSerializer.Serialize(report, MigrationJobOptions.Json), ct).ConfigureAwait(false);
        }

        private async Task<(int Imported, int Seen)> ProcessPageAsync(IConfluenceSourceProvider source, string system, Guid spaceId, SourcePage page, bool execute, CancellationToken ct)
        {
            var result = await ProcessVersionsAsync(source, system, spaceId, page, execute, ct).ConfigureAwait(false);
            await ProcessAttachmentsAsync(source, system, page, execute, ct).ConfigureAwait(false);
            await ProcessRestrictionsAsync(source, system, page, execute, ct).ConfigureAwait(false);
            await ProcessCommentsAsync(source, system, page, execute, ct).ConfigureAwait(false);
            return result;
        }

        /// <summary>
        /// Files of a page. A dry run only counts them. An import downloads what is missing (ids are deterministic, see
        /// <see cref="ImportIds"/>), leaves out files above the product's upload limit and records every failure; one bad file
        /// never stops the page.
        /// </summary>
        private async Task ProcessAttachmentsAsync(IConfluenceSourceProvider source, string system, SourcePage page, bool execute, CancellationToken ct)
        {
            var files = await source.ListAttachmentsAsync(page.SourceContentId, ct).ConfigureAwait(false);
            if (files.Count == 0)
            {
                return;
            }

            _attFound += files.Count;
            _attBytes += files.Sum(f => f.Size);
            var max = MaxAttachmentBytes;
            var tooLarge = files.Where(f => f.Size > max).ToList();
            _attTooLarge += tooLarge.Count;
            foreach (var f in tooLarge)
            {
                await EventAsync(MigrationEventLevel.Warning, "attachment.too_large", $"{page.SpaceKey}/{page.SourceContentId} {f.FileName}", $"The file is larger than the allowed size ({max / (1024 * 1024)} MB) and was not imported.", ct).ConfigureAwait(false);
            }

            if (!execute)
            {
                return;
            }

            var have = await owner.importer.ImportedAttachmentIdsAsync(system, page.SourceContentId, ct).ConfigureAwait(false);
            var batch = new List<ImportAttachment>();
            // A re-conversion refreshes every file (a source can deliver different bytes later); otherwise only missing files are fetched.
            foreach (var f in files.Where(f => f.Size <= max && (_reconvert || !have.Contains(ImportIds.Attachment(owner.tenant.Tenant.TenantId, system, page.SourceContentId, f.FileName)))))
            {
                try
                {
                    var bytes = await source.DownloadAttachmentAsync(f, max, ct).ConfigureAwait(false);
                    if (f.Size > 0 && bytes.LongLength != f.Size)
                    {
                        // The source's own size disagrees with what it delivered: keep the file, tell the administrator.
                        _attMismatch++;
                        await EventAsync(MigrationEventLevel.Warning, "attachment.size_mismatch", $"{page.SpaceKey}/{page.SourceContentId} {f.FileName}", $"The source reports {f.Size} bytes but delivered {bytes.LongLength}.", ct).ConfigureAwait(false);
                    }

                    batch.Add(new ImportAttachment(f.FileName, bytes, f.CreatedAtUtc));
                }
                catch (ConfluenceSourceException ex) when (ex.Code is not ("source.unauthorized" or "source.forbidden" or "source.unreachable"))
                {
                    _attFailed++;
                    await EventAsync(MigrationEventLevel.Error, ex.Code, $"{page.SpaceKey}/{page.SourceContentId} {f.FileName}", ex.Message, ct).ConfigureAwait(false);
                }
            }

            if (batch.Count > 0)
            {
                var stored = await owner.importer.ImportAttachmentsAsync(system, page.SourceContentId, batch, job.CreatedBy, _reconvert, ct).ConfigureAwait(false);
                _attImported += stored.Created;
                _attReplaced += stored.Replaced;
            }
        }

        /// <summary>
        /// Comments of a page: threads (replies of any depth are flattened into their thread), inline comments with their quoted text,
        /// resolved state, original times. Authors that are not workspace members stay names only. Nothing is notified.
        /// </summary>
        private async Task ProcessCommentsAsync(IConfluenceSourceProvider source, string system, SourcePage page, bool execute, CancellationToken ct)
        {
            IReadOnlyList<SourceComment> comments;
            try
            {
                comments = await source.ListCommentsAsync(page.SourceContentId, ct).ConfigureAwait(false);
            }
            catch (ConfluenceSourceException ex) when (ex.Code is "source.not_found" or "source.error")
            {
                _comFailed++;
                await EventAsync(MigrationEventLevel.Warning, "comments.unreadable", $"{page.SpaceKey}/{page.SourceContentId} {page.Title}", "The page's comments could not be read from the source.", ct).ConfigureAwait(false);
                return;
            }

            if (comments.Count == 0)
            {
                return;
            }

            _comFound += comments.Count;
            var batch = new List<ImportComment>();
            foreach (var c in comments)
            {
                var converted = StorageFormatConverter.Convert(c.StorageFormatBody, new ConversionContext(owner.tenant.Tenant.TenantId, system, page.SourceContentId, page.SpaceKey));
                foreach (var w in converted.Warnings)
                {
                    _warnings++;
                    _warningCodes[w.Code] = _warningCodes.GetValueOrDefault(w.Code) + 1;
                }

                var author = c.AuthorKey is null ? null : await ResolveUserAsync(source, c.AuthorKey, ct).ConfigureAwait(false);
                batch.Add(new ImportComment(c.Id, c.ParentId, c.Inline, c.Selection, converted.Doc.ToJsonString(), c.AuthorDisplayName ?? author?.Source?.DisplayName, author?.RaaviId, c.CreatedAtUtc, c.Resolved));
            }

            if (!execute)
            {
                return;
            }

            var result = await owner.importer.ImportCommentsAsync(system, page.SourceContentId, batch, ct).ConfigureAwait(false);
            _comImported += result.Created;
            _comSkipped += result.Skipped;
            foreach (var w in result.Warnings)
            {
                await EventAsync(MigrationEventLevel.Warning, "comment.skipped", $"{page.SpaceKey}/{page.SourceContentId}", w, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Source user → workspace member. Explicit map first; then, only when the administrator opted in, the source user's e-mail
        /// against verified member e-mails. Without opt-in a match is only a <i>suggestion</i> shown in the report.
        /// </summary>
        private async Task<ResolvedUser> ResolveUserAsync(IConfluenceSourceProvider source, string key, CancellationToken ct)
        {
            if (_users.TryGetValue(key, out var known))
            {
                return known;
            }

            var user = await source.GetUserAsync(key, ct).ConfigureAwait(false);
            string? explicitEmail = null;
            if (_o.UserMap is { } map)
            {
                if (!map.TryGetValue(key, out explicitEmail) && user?.UserName is { } name)
                {
                    map.TryGetValue(name, out explicitEmail);
                }
            }

            var email = explicitEmail ?? user?.Email;
            DirectoryEntry? match = null;
            if (!string.IsNullOrWhiteSpace(email))
            {
                match = (await owner.directory.FindUsersByVerifiedEmailAsync([email], ct).ConfigureAwait(false)).GetValueOrDefault(email);
            }

            var resolved = new ResolvedUser(user ?? new SourceUser(key, null, null, null), match, match is not null && (explicitEmail is not null || _o.MapUsersByEmail));
            _users[key] = resolved;
            return resolved;
        }

        private async Task<Guid?> ResolveGroupAsync(string name, CancellationToken ct)
        {
            if (!_groups.TryGetValue(name, out var id))
            {
                id = (await owner.directory.FindGroupsByNameAsync([name], ct).ConfigureAwait(false)) is { } found && found.TryGetValue(name, out var g) ? g : null;
                _groups[name] = id;
            }

            return id;
        }

        /// <summary>
        /// Page restrictions. Mapped users and groups are restricted <i>to</i>, the importing administrator is always included, and
        /// subjects that cannot be mapped are left out and reported — so the imported restriction is never wider than the source one.
        /// </summary>
        private async Task ProcessRestrictionsAsync(IConfluenceSourceProvider source, string system, SourcePage page, bool execute, CancellationToken ct)
        {
            SourceRestrictions restrictions;
            try
            {
                restrictions = await source.GetRestrictionsAsync(page.SourceContentId, ct).ConfigureAwait(false);
            }
            catch (ConfluenceSourceException ex) when (ex.Code is "source.not_found" or "source.error")
            {
                await EventAsync(MigrationEventLevel.Warning, "restrictions.unreadable", $"{page.SpaceKey}/{page.SourceContentId} {page.Title}", "The page's restrictions could not be read from the source.", ct).ConfigureAwait(false);
                return;
            }

            if (!restrictions.Any)
            {
                return;
            }

            _restrictedPages++;
            async Task<List<ImportSubject>> MapAsync(IReadOnlyList<SourceSubject> subjects)
            {
                var mapped = new List<ImportSubject>();
                foreach (var subject in subjects)
                {
                    Guid? id;
                    if (subject.Kind == SourceSubjectKind.Group)
                    {
                        id = await ResolveGroupAsync(subject.Key, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        id = (await ResolveUserAsync(source, subject.Key, ct).ConfigureAwait(false)).RaaviId;
                    }

                    if (id is { } g)
                    {
                        mapped.Add(new ImportSubject(subject.Kind == SourceSubjectKind.Group ? ImportSubjectKind.Group : ImportSubjectKind.User, g));
                    }
                    else
                    {
                        _unmappedSubjects.Add((subject.Kind == SourceSubjectKind.Group ? "group: " : "user: ") + (subject.DisplayName ?? subject.Key));
                    }
                }

                return mapped;
            }

            var view = await MapAsync(restrictions.View).ConfigureAwait(false);
            var edit = await MapAsync(restrictions.Edit).ConfigureAwait(false);
            if (execute && await owner.importer.ApplyRestrictionsAsync(system, page.SourceContentId, restrictions.View.Count > 0, view, restrictions.Edit.Count > 0, edit, job.CreatedBy, ct).ConfigureAwait(false))
            {
                _restrictionsApplied++;
            }
        }

        private async Task<(int Imported, int Seen)> ProcessVersionsAsync(IConfluenceSourceProvider source, string system, Guid spaceId, SourcePage page, bool execute, CancellationToken ct)
        {
            var skip = execute && !_reconvert ? await owner.importer.ImportedVersionsAsync(system, page.SourceContentId, ct).ConfigureAwait(false) : null;
            var versions = new List<ImportVersion>();
            var seen = 0;
            await foreach (var v in source.StreamVersionsAsync(page.SourceContentId, ct, skip).ConfigureAwait(false))
            {
                seen++;
                var converted = StorageFormatConverter.Convert(v.StorageFormatBody, new ConversionContext(owner.tenant.Tenant.TenantId, system, page.SourceContentId, page.SpaceKey));
                foreach (var w in converted.Warnings)
                {
                    _warnings++;
                    _warningCodes[w.Code] = _warningCodes.GetValueOrDefault(w.Code) + 1;
                    if (v.IsCurrent)
                    {
                        await EventAsync(MigrationEventLevel.Warning, w.Code, $"{page.SpaceKey}/{page.SourceContentId} {page.Title}", w.Detail, ct).ConfigureAwait(false);
                    }
                }

                foreach (var (name, count) in converted.UnsupportedMacros)
                {
                    _macros[name] = _macros.GetValueOrDefault(name) + count;
                }

                if (v.IsCurrent)
                {
                    _attachmentRefs += converted.References.Count(r => r.Kind == ReferenceKind.Attachment);
                    _pageLinks += converted.References.Count(r => r.Kind == ReferenceKind.Page);
                }

                if (v.AuthorKey is not null || v.AuthorDisplayName is not null)
                {
                    _authors.Add(v.AuthorDisplayName is null ? v.AuthorKey! : $"{v.AuthorDisplayName} ({v.AuthorKey})");
                }

                var author = v.AuthorKey is null ? null : await ResolveUserAsync(source, v.AuthorKey, ct).ConfigureAwait(false);
                versions.Add(new ImportVersion(v.SourceVersionNumber, CleanTitle(v.Title), converted.Doc.ToJsonString(), v.AuthorKey, v.AuthorDisplayName, author?.RaaviId, v.CreatedAtUtc, v.VersionComment));
            }

            if (!execute)
            {
                return (0, seen);
            }

            if (versions.Count == 0)
            {
                return (0, 0); // everything was imported before
            }

            var result = await owner.importer.ImportPageAsync(new ImportPageRequest(
                system, page.SourceContentId, spaceId, page.ParentSourceContentId, page.Position,
                new Uri(new Uri(job.SourceUrl), $"pages/viewpage.action?pageId={Uri.EscapeDataString(page.SourceContentId)}").AbsoluteUri,
                versions, page.Labels, job.CreatedBy, _reconvert), ct).ConfigureAwait(false);
            foreach (var w in result.Warnings)
            {
                await EventAsync(MigrationEventLevel.Warning, "label.skipped", $"{page.SpaceKey}/{page.SourceContentId}", w, ct).ConfigureAwait(false);
            }

            return (result.VersionsImported + result.VersionsRewritten, seen);
        }

        /// <summary>Pages whose parent is already earlier in the list (a page can only be imported below an existing parent).</summary>
        private static List<SourcePage> ParentFirst(List<SourcePage> pages)
        {
            var byId = pages.ToDictionary(p => p.SourceContentId, StringComparer.Ordinal);
            int Depth(SourcePage p)
            {
                var d = 0;
                for (var cur = p; cur.ParentSourceContentId is { } parent && byId.TryGetValue(parent, out var next) && d < 200; cur = next)
                {
                    d++;
                }

                return d;
            }

            return [.. pages.OrderBy(Depth).ThenBy(p => p.ParentSourceContentId, StringComparer.Ordinal).ThenBy(p => p.Position).ThenBy(p => p.Title, StringComparer.Ordinal)];
        }

        private static string CleanTitle(string title)
        {
            var t = new string([.. title.Where(c => !char.IsControl(c))]).Trim();
            return t.Length == 0 ? "(untitled)" : t.Length > 255 ? t[..255] : t;
        }

        private async Task<bool> StopRequestedAsync(CancellationToken ct)
        {
            var control = await _db.Jobs.AsNoTracking().Where(j => j.Id == job.Id).Select(j => j.Control).FirstAsync(ct).ConfigureAwait(false);
            if (control == MigrationControl.None)
            {
                return false;
            }

            await FinishAsync(control == MigrationControl.Pause ? MigrationStatus.Paused : MigrationStatus.Cancelled, null, ct).ConfigureAwait(false);
            return true;
        }

        private async Task SaveAsync(string operation, CancellationToken ct)
        {
            job.Progress(operation, _spacesTotal, _spacesDone, _pagesTotal, _pagesDone, _pagesFailed, _versionsDone, _warnings);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        private async Task EventAsync(MigrationEventLevel level, string code, string? reference, string message, CancellationToken ct)
        {
            if (level != MigrationEventLevel.Error && ++_events > owner.options.Value.MaxEventsPerJob)
            {
                return; // still counted in the totals
            }

            _db.Events.Add(MigrationEvent.Create(job.Id, level, code, reference, message, owner.clock.UtcNow));
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        private async Task FinishAsync(MigrationStatus status, string? reportJson, CancellationToken ct)
        {
            job.Progress(status.ToString(), _spacesTotal, _spacesDone, _pagesTotal, _pagesDone, _pagesFailed, _versionsDone, _warnings);
            job.Finish(status, reportJson, owner.clock.UtcNow);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            // The administrator's pause/cancel request has been honoured.
            await _db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Control, MigrationControl.None), ct).ConfigureAwait(false);
        }

        public async Task FailAsync(string code, string message, CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            var fresh = await _db.Jobs.FirstAsync(j => j.Id == job.Id, ct).ConfigureAwait(false);
            _db.Events.Add(MigrationEvent.Create(job.Id, MigrationEventLevel.Error, code, null, message, owner.clock.UtcNow));
            fresh.Progress("Failed", _spacesTotal, _spacesDone, _pagesTotal, _pagesDone, _pagesFailed, _versionsDone, _warnings);
            fresh.Finish(MigrationStatus.Failed, null, owner.clock.UtcNow);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    public static string Culture(int n) => n.ToString(CultureInfo.InvariantCulture);
}
