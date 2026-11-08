using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki.Application;

/// <summary>A finished archive on disk. Disposing deletes the file.</summary>
public sealed class ExportArchive(FileStream stream, string fileName) : IAsyncDisposable
{
    public FileStream Stream { get; } = stream;

    public string FileName { get; } = fileName;

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

/// <summary>
/// Data export of the workspace (Phase 12a, ADR-037): a ZIP with JSON files of everything <b>the requesting administrator may view</b> — spaces, current pages with
/// labels and comments, attachments, tasks, calendar events, databases, slide decks and whiteboards. Built into a temporary file (deleted after the download), so
/// nothing is kept on the server and a failure never produces a half-sent response. At most one export per workspace runs at a time.
/// </summary>
public sealed class ExportService(WikiDbContext db, WikiAuthorizer authz, IPrincipalDirectory directory, ICurrentUser current, IClock clock, Raavi.Application.Abstractions.Tenancy.ITenantContext tenant)
{
    public const long MaxAttachmentBytes = 1L * 1024 * 1024 * 1024;
    public const int FormatVersion = 1;

    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Gates = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static readonly Error InProgress = new("export.in_progress", "An export of this workspace is already running. Try again in a few minutes.", ErrorKind.Conflict);

    public async Task<Result<ExportArchive>> BuildAsync(CancellationToken ct)
    {
        var gate = Gates.GetOrAdd(tenant.Tenant.TenantId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return Result<ExportArchive>.Failure(InProgress);
        }

        var path = Path.Combine(Path.GetTempPath(), $"raavi-export-{Guid.NewGuid():N}.zip");
        FileStream? file = null;
        try
        {
            file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
            {
                await WriteAsync(zip, ct).ConfigureAwait(false);
            }

            file.Position = 0;
            var stamp = clock.UtcNow.ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture);
            return Result<ExportArchive>.Success(new ExportArchive(file, $"raavi-export-{tenant.Tenant.Slug}-{stamp}.zip"));
        }
        catch
        {
            if (file is not null)
            {
                await file.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task WriteAsync(ZipArchive zip, CancellationToken ct)
    {
        var principal = await authz.PrincipalAsync(ct).ConfigureAwait(false);
        var viewable = await authz.ViewableSpaceIdsAsync(ct).ConfigureAwait(false);
        var spaces = await db.Spaces.AsNoTracking().Where(s => viewable.Contains(s.Id)).OrderBy(s => s.Key).ToListAsync(ct).ConfigureAwait(false);
        var users = new Dictionary<Guid, string?>();
        var counts = new Dictionary<string, int>();
        var omitted = new List<object>();

        async Task Entry(string name, object value)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            var stream = entry.Open();
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, value, Json, ct).ConfigureAwait(false);
            }
        }

        void Count(string what, int n) => counts[what] = counts.GetValueOrDefault(what) + n;

        long attachmentBytes = 0;
        foreach (var space in spaces)
        {
            var root = $"spaces/{space.Key}/";
            await Entry(root + "space.json", new { space.Id, space.Key, space.Name, space.Description, type = space.Type, status = space.Status, space.HomePageId, space.CreatedAt, space.UpdatedAt }).ConfigureAwait(false);
            Count("spaces", 1);

            // Pages: current content the caller may view (space View + page restrictions), without drafts, archive and trash.
            var nodes = await db.Nodes.AsNoTracking().Where(n => n.SpaceId == space.Id && n.Status == ContentStatus.Current).OrderBy(n => n.Position).ThenBy(n => n.Title).ToListAsync(ct).ConfigureAwait(false);
            var allowed = await authz.FilterViewableAsync([.. nodes.Select(n => (n.Id, n.SpaceId))], ct).ConfigureAwait(false);
            Count("pagesOmittedNoAccess", nodes.Count(n => !allowed.Contains(n.Id)));
            foreach (var node in nodes.Where(n => allowed.Contains(n.Id)))
            {
                var body = node.CurrentVersionId is { } vid ? await db.PageVersions.AsNoTracking().Where(v => v.Id == vid).Select(v => v.ContentJson).FirstOrDefaultAsync(ct).ConfigureAwait(false) : null;
                var labels = await (from cl in db.ContentLabels.AsNoTracking() join l in db.Labels.AsNoTracking() on cl.LabelId equals l.Id where cl.ContentId == node.Id orderby l.Name select l.Name).ToListAsync(ct).ConfigureAwait(false);
                var comments = await db.Comments.AsNoTracking().Where(c => c.ContentId == node.Id && c.DeletedAt == null).OrderBy(c => c.CreatedAt).ToListAsync(ct).ConfigureAwait(false);
                var attachments = await db.Attachments.AsNoTracking().Where(a => a.ContentId == node.Id).Select(a => new { a.Id, a.FileName, a.MediaType, a.Size, a.Sha256, a.CreatedAt }).ToListAsync(ct).ConfigureAwait(false);
                var files = new List<object>();
                foreach (var a in attachments)
                {
                    var included = attachmentBytes + a.Size <= MaxAttachmentBytes;
                    files.Add(new { a.Id, a.FileName, a.MediaType, a.Size, a.Sha256, included, path = included ? $"{root}attachments/{node.Id:N}/{a.Id:N}-{Safe(a.FileName)}" : null });
                    if (!included)
                    {
                        omitted.Add(new { kind = "attachment", id = a.Id, reason = "size_cap" });
                        continue;
                    }

                    var data = await db.Attachments.AsNoTracking().Where(x => x.Id == a.Id).Select(x => x.Data).FirstAsync(ct).ConfigureAwait(false);
                    var entry = zip.CreateEntry($"{root}attachments/{node.Id:N}/{a.Id:N}-{Safe(a.FileName)}", CompressionLevel.Fastest);
                    var stream = entry.Open();
                    await using (stream.ConfigureAwait(false))
                    {
                        await stream.WriteAsync(data, ct).ConfigureAwait(false);
                    }

                    attachmentBytes += a.Size;
                    Count("attachments", 1);
                }

                await Entry($"{root}pages/{node.Id:N}.json", new
                {
                    node.Id, node.ParentId, node.ContentType, node.Title, node.Slug, version = node.CurrentVersionNumber, node.CreatedAt, node.UpdatedAt, node.PublishedAt,
                    createdBy = node.CreatedBy, updatedBy = node.UpdatedBy, labels, body = body is null ? (JsonElement?)null : JsonDocument.Parse(body).RootElement.Clone(),
                    comments = comments.Select(c => new { c.Id, c.ThreadId, c.ParentId, kind = c.Kind, author = c.AuthorId, c.CreatedAt, c.EditedAt, body = JsonDocument.Parse(c.BodyJson).RootElement.Clone() }),
                    attachments = files,
                }).ConfigureAwait(false);
                Count("pages", 1);
                Count("comments", comments.Count);
                foreach (var id in new[] { node.CreatedBy, node.UpdatedBy }.Concat(comments.Where(c => c.AuthorId is not null).Select(c => c.AuthorId!.Value)))
                {
                    users.TryAdd(id, null);
                }
            }

            var tasks = await db.WorkTasks.AsNoTracking().Where(t => t.SpaceId == space.Id).OrderBy(t => t.CreatedAt).ToListAsync(ct).ConfigureAwait(false);
            if (tasks.Count > 0)
            {
                await Entry(root + "tasks.json", tasks.Select(t => new { t.Id, t.Title, t.Notes, status = t.Status, t.ContentId, assignee = t.AssigneeId, t.DueDate, t.CreatedAt, t.CompletedAt })).ConfigureAwait(false);
                Count("tasks", tasks.Count);
            }

            var events = await db.CalendarEvents.AsNoTracking().Where(e => e.SpaceId == space.Id).OrderBy(e => e.StartDate).ToListAsync(ct).ConfigureAwait(false);
            if (events.Count > 0)
            {
                await Entry(root + "events.json", events.Select(e => new
                {
                    e.Id, e.Title, e.Description, e.Location, e.IsAllDay, e.StartDate, e.StartTime, e.EndDate, e.EndTime, e.TimeZone,
                    recurrence = new { frequency = e.RecurFrequency, interval = e.RecurInterval, weekDays = e.RecurWeekDays, calendar = e.RecurCalendar, until = e.RecurUntil, count = e.RecurCount },
                    excludedDates = e.ExcludedDates,
                })).ConfigureAwait(false);
                Count("events", events.Count);
            }

            foreach (var d in await db.SpaceDatabases.AsNoTracking().Where(x => x.SpaceId == space.Id).OrderBy(x => x.Name).ToListAsync(ct).ConfigureAwait(false))
            {
                var rows = await db.DatabaseRows.AsNoTracking().Where(r => r.DatabaseId == d.Id).OrderBy(r => r.CreatedAt).ToListAsync(ct).ConfigureAwait(false);
                await Entry($"{root}databases/{d.Id:N}.json", new { d.Id, d.Name, d.Description, columns = d.Columns, rows = rows.Select(r => new { r.Id, r.Values, r.CreatedAt }) }).ConfigureAwait(false);
                Count("databases", 1);
                Count("databaseRows", rows.Count);
            }

            foreach (var deck in await db.SlideDecks.AsNoTracking().Where(x => x.SpaceId == space.Id).OrderBy(x => x.Title).ToListAsync(ct).ConfigureAwait(false))
            {
                await Entry($"{root}slides/{deck.Id:N}.json", new { deck.Id, deck.Title, direction = deck.Direction, slides = deck.Slides }).ConfigureAwait(false);
                Count("slideDecks", 1);
            }

            foreach (var board in await db.Whiteboards.AsNoTracking().Where(x => x.SpaceId == space.Id).OrderBy(x => x.Title).ToListAsync(ct).ConfigureAwait(false))
            {
                await Entry($"{root}whiteboards/{board.Id:N}.json", new { board.Id, board.Title, elements = board.Elements }).ConfigureAwait(false);
                await WriteTextAsync(zip, $"{root}whiteboards/{board.Id:N}.svg", BoardSvgWriter.Write(board.Title, board.Elements), ct).ConfigureAwait(false);
                Count("whiteboards", 1);
            }
        }

        var names = await directory.GetUserNamesAsync([.. users.Keys], ct).ConfigureAwait(false);
        await Entry("users.json", names.Select(n => new { id = n.Key, name = n.Value }).OrderBy(n => n.name)).ConfigureAwait(false);
        await Entry("manifest.json", new
        {
            formatVersion = FormatVersion,
            tenant = tenant.Tenant.Slug,
            exportedAt = clock.UtcNow,
            exportedBy = current.UserId,
            scope = "Everything the exporting administrator may view: spaces with View access, current pages that pass page restrictions, no drafts, archive or trash, no permissions or history.",
            counts,
            attachmentBytes,
            omitted,
            principal = principal.UserId,
        }).ConfigureAwait(false);
    }

    private static async Task WriteTextAsync(ZipArchive zip, string name, string text, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        var stream = entry.Open();
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct).ConfigureAwait(false);
        }
    }

    /// <summary>File name safe inside a ZIP path (no separators, no traversal, no control characters, bounded length).</summary>
    public static string Safe(string name)
    {
        var cleaned = new string(name.Select(c => char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c).ToArray()).Trim().Trim('.');
        while (cleaned.Contains("..", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("..", "_", StringComparison.Ordinal);
        }

        cleaned = cleaned.Trim().Trim('.');
        return cleaned.Length == 0 ? "file" : cleaned.Length > 120 ? cleaned[..120] : cleaned;
    }
}
