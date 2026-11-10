using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Content;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Application.Common;
using Raavi.Domain.Common;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki.Application;

public sealed record SpaceImportResultDto(string Key, string Name, int Pages, int Attachments, int Comments, IReadOnlyList<string> Warnings, IReadOnlyDictionary<string, int> NotImported);

/// <summary>
/// Creates a <b>new</b> space from an archive made by the export (ADR-051): pages in their tree with labels, attachments and comments. The archive is read
/// entry by entry with size limits and is never extracted to disk. Everything goes through <see cref="IContentImporter"/> (the same writer the Confluence
/// migration uses, so versions, closure rows and validation are identical), keyed by a fresh source id per run: importing the same archive twice makes two
/// spaces, never duplicates. Links between pages and attachment references are rewritten to the new ids afterwards.
/// </summary>
public sealed class SpaceImportService(WikiDbContext db, IContentImporter importer, WikiAuthorizer authz, ICurrentUser current, IPrincipalDirectory directory, ITenantContext tenant)
{
    public const int MaxEntries = 100_000;
    public const long MaxJsonBytes = 25L * 1024 * 1024;
    public const long MaxFileBytes = 100L * 1024 * 1024;
    public const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;

    public static readonly Error NotAnArchive = new("import.archive_invalid", "This file is not a Raavi export archive.", ErrorKind.Validation);
    public static readonly Error NoSpace = new("import.no_space", "The archive contains no space.", ErrorKind.Validation);
    public static readonly Error ManySpaces = new("import.many_spaces", "The archive contains several spaces; export a single space instead.", ErrorKind.Validation);
    public static readonly Error TooBig = new("import.too_big", "The archive is larger than the allowed size.", ErrorKind.Validation);

    private const string EmptyDoc = "{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\"}]}";

    private sealed record PageRef(string Name, Guid Id, Guid? ParentId, int Order);

    public async Task<Result<SpaceImportResultDto>> ImportAsync(Stream archive, string? key, string? name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (!await authz.CanCreateSpacesAsync(ct).ConfigureAwait(false))
        {
            return Result<SpaceImportResultDto>.Failure(WikiErrors.Forbidden);
        }

        ZipArchive zip;
        try
        {
            zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException or NotSupportedException)
        {
            return Result<SpaceImportResultDto>.Failure(NotAnArchive);
        }

        using (zip)
        {
            if (zip.Entries.Count > MaxEntries || zip.Entries.Sum(e => e.Length) > MaxTotalBytes)
            {
                return Result<SpaceImportResultDto>.Failure(TooBig);
            }

            var entries = zip.Entries.Where(e => e.Length > 0).GroupBy(e => e.FullName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            if (!entries.ContainsKey("manifest.json"))
            {
                return Result<SpaceImportResultDto>.Failure(NotAnArchive);
            }

            var spaceFiles = entries.Keys.Where(k => k.StartsWith("spaces/", StringComparison.Ordinal) && k.EndsWith("/space.json", StringComparison.Ordinal) && k.Count(c => c == '/') == 2).ToList();
            if (spaceFiles.Count == 0)
            {
                return Result<SpaceImportResultDto>.Failure(NoSpace);
            }

            if (spaceFiles.Count > 1)
            {
                return Result<SpaceImportResultDto>.Failure(ManySpaces);
            }

            try
            {
                return await RunAsync(entries, spaceFiles[0], key, name, ct).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return Result<SpaceImportResultDto>.Failure(NotAnArchive);
            }
            catch (InvalidDataException)
            {
                return Result<SpaceImportResultDto>.Failure(NotAnArchive);
            }
            catch (DomainException ex)
            {
                return Result<SpaceImportResultDto>.Failure(new Error(ex.Code, ex.Message, ex.Code == "space.key_conflict" ? ErrorKind.Conflict : ErrorKind.Validation));
            }
        }
    }

    private async Task<Result<SpaceImportResultDto>> RunAsync(Dictionary<string, ZipArchiveEntry> entries, string spaceFile, string? keyOverride, string? nameOverride, CancellationToken ct)
    {
        var warnings = new List<string>();
        var actor = current.UserId;
        var root = spaceFile[..(spaceFile.LastIndexOf('/') + 1)]; // "spaces/KEY/"
        using var spaceDoc = JsonDocument.Parse(await ReadAsync(entries[spaceFile], MaxJsonBytes, ct).ConfigureAwait(false));
        var sourceKey = Str(spaceDoc.RootElement, "key") ?? root.Split('/')[1];
        var title = string.IsNullOrWhiteSpace(nameOverride) ? Str(spaceDoc.RootElement, "name") ?? sourceKey : nameOverride.Trim();
        var key = Space.NormalizeKey(string.IsNullOrWhiteSpace(keyOverride) ? sourceKey : keyOverride);
        if (await db.Spaces.AnyAsync(s => s.Key == key, ct).ConfigureAwait(false))
        {
            return Result<SpaceImportResultDto>.Failure(WikiErrors.KeyTaken);
        }

        var names = new Dictionary<Guid, string>();
        if (entries.TryGetValue("users.json", out var usersEntry))
        {
            using var users = JsonDocument.Parse(await ReadAsync(usersEntry, MaxJsonBytes, ct).ConfigureAwait(false));
            foreach (var u in users.RootElement.EnumerateArray())
            {
                if (Guid.TryParse(Str(u, "id"), out var id) && Str(u, "name") is { } n)
                {
                    names[id] = n;
                }
            }
        }

        var members = names.Count == 0 ? new Dictionary<Guid, string>() : (await directory.GetUserNamesAsync([.. names.Keys], ct).ConfigureAwait(false)).ToDictionary(k => k.Key, k => k.Value);
        Guid? Member(Guid? id) => id is { } g && members.ContainsKey(g) ? g : null;

        var sourceSystem = "raavi:" + Guid.NewGuid().ToString("N")[..12];
        var space = await importer.EnsureSpaceAsync(new ImportSpaceRequest(sourceSystem, sourceKey, key, title, Str(spaceDoc.RootElement, "description"), DateOf(spaceDoc.RootElement, "createdAt"), actor), ct).ConfigureAwait(false);

        // Pass 1: ids, parents and the archive order only.
        var refs = new List<PageRef>();
        var pagePrefix = root + "pages/";
        var order = 0;
        foreach (var e in entries.Values.Where(e => e.FullName.StartsWith(pagePrefix, StringComparison.Ordinal) && e.FullName.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var doc = JsonDocument.Parse(await ReadAsync(e, MaxJsonBytes, ct).ConfigureAwait(false));
            if (Guid.TryParse(Str(doc.RootElement, "id"), out var id))
            {
                refs.Add(new PageRef(e.FullName, id, Guid.TryParse(Str(doc.RootElement, "parentId"), out var p) ? p : null, order++));
            }
        }

        var known = refs.Select(r => r.Id).ToHashSet();
        var byId = refs.ToDictionary(r => r.Id);
        int Depth(PageRef r)
        {
            var d = 0;
            var cur = r;
            while (cur.ParentId is { } pid && byId.TryGetValue(pid, out var up) && d < 200)
            {
                cur = up;
                d++;
            }

            return d;
        }

        var depth = refs.ToDictionary(r => r.Id, Depth);
        var siblings = new Dictionary<Guid, int>();
        var newIds = new Dictionary<Guid, Guid>();
        var attachmentIds = new Dictionary<Guid, Guid>();
        int pages = 0, attachments = 0, comments = 0;
        var failed = new HashSet<Guid>();

        foreach (var r in refs.OrderBy(r => depth[r.Id]).ThenBy(r => r.Order))
        {
            Guid? parent = r.ParentId is { } pid && known.Contains(pid) ? pid : null;
            if (parent is { } pp && failed.Contains(pp))
            {
                failed.Add(r.Id);
                warnings.Add($"A page below a skipped page was skipped too ({r.Id:N}).");
                continue;
            }

            using var doc = JsonDocument.Parse(await ReadAsync(entries[r.Name], MaxJsonBytes, ct).ConfigureAwait(false));
            var page = doc.RootElement;
            var docRaw = page.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object && body.TryGetProperty("doc", out var d) ? d.GetRawText() : EmptyDoc;
            var pageTitle = Str(page, "title") ?? "Untitled";
            var created = DateOf(page, "createdAt") ?? DateTimeOffset.UtcNow;
            var updated = DateOf(page, "updatedAt") ?? created;
            Guid? updatedBy = Guid.TryParse(Str(page, "updatedBy"), out var ub) ? ub : null;
            var authorId = Member(updatedBy);
            var authorName = authorId is null && updatedBy is { } uid && names.TryGetValue(uid, out var an) ? an : null;
            var siblingKey = parent ?? Guid.Empty;
            var position = siblings.GetValueOrDefault(siblingKey);
            siblings[siblingKey] = position + 1;
            var labels = page.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array ? l.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToList() : [];
            var sourceId = r.Id.ToString("D");
            try
            {
                var version = new ImportVersion(1, pageTitle, docRaw, null, authorName, authorId, updated, null);
                var imported = await importer.ImportPageAsync(new ImportPageRequest(sourceSystem, sourceId, space.SpaceId, parent?.ToString("D"), position, null, [version], labels, actor), ct).ConfigureAwait(false);
                newIds[r.Id] = imported.ContentId;
                warnings.AddRange(imported.Warnings);
                pages++;
            }
            catch (DomainException ex)
            {
                failed.Add(r.Id);
                db.ChangeTracker.Clear();
                warnings.Add($"The page '{pageTitle}' was skipped: {ex.Message}");
                continue;
            }

            // Attachments of the page (files that the export included).
            var files = new List<ImportAttachment>();
            if (page.TryGetProperty("attachments", out var att) && att.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in att.EnumerateArray())
                {
                    var file = Str(a, "fileName");
                    if (file is null || Str(a, "path") is not { } path || !path.StartsWith(root + "attachments/", StringComparison.Ordinal) || !entries.TryGetValue(path, out var fe))
                    {
                        if (file is not null)
                        {
                            warnings.Add($"The file '{file}' of '{pageTitle}' is not in the archive.");
                        }

                        continue;
                    }

                    files.Add(new ImportAttachment(file, await ReadAsync(fe, MaxFileBytes, ct).ConfigureAwait(false), created));
                    if (Guid.TryParse(Str(a, "id"), out var oldAtt))
                    {
                        attachmentIds[oldAtt] = ImportIds.Attachment(tenant.Tenant.TenantId, sourceSystem, sourceId, file);
                    }
                }
            }

            if (files.Count > 0)
            {
                attachments += (await importer.ImportAttachmentsAsync(sourceSystem, sourceId, files, actor, false, ct).ConfigureAwait(false)).Created;
            }

            if (page.TryGetProperty("comments", out var cs) && cs.ValueKind == JsonValueKind.Array)
            {
                var list = new List<ImportComment>();
                foreach (var c in cs.EnumerateArray())
                {
                    string? cDoc = null;
                    if (c.TryGetProperty("body", out var cb) && cb.ValueKind == JsonValueKind.Object)
                    {
                        cDoc = cb.TryGetProperty("doc", out var cd) ? cd.GetRawText() : cb.GetRawText();
                    }

                    if (Str(c, "id") is not { } cid || cDoc is null)
                    {
                        continue;
                    }

                    Guid? aid = Guid.TryParse(Str(c, "author"), out var ag) ? ag : null;
                    var cn = aid is { } x && names.TryGetValue(x, out var found) ? found : null;
                    list.Add(new ImportComment(cid, Str(c, "parentId"), false, null, cDoc, cn, Member(aid), DateOf(c, "createdAt") ?? created, false));
                }

                if (list.Count > 0)
                {
                    var result = await importer.ImportCommentsAsync(sourceSystem, sourceId, list, ct).ConfigureAwait(false);
                    comments += result.Created;
                    warnings.AddRange(result.Warnings);
                }
            }
        }

        // Home page: the archive's, else the first top-level page.
        Guid? home = Guid.TryParse(Str(spaceDoc.RootElement, "homePageId"), out var h) && newIds.ContainsKey(h)
            ? h
            : refs.Where(r => newIds.ContainsKey(r.Id) && (r.ParentId is null || !known.Contains(r.ParentId.Value))).OrderBy(r => r.Order).Select(r => (Guid?)r.Id).FirstOrDefault();
        if (home is { } hid)
        {
            await importer.SetHomePageAsync(sourceSystem, sourceKey, hid.ToString("D"), actor, ct).ConfigureAwait(false);
        }

        await RewriteReferencesAsync(space.SpaceId, sourceKey, key, newIds, attachmentIds, ct).ConfigureAwait(false);

        var skipped = new Dictionary<string, int>();
        void Skipped(string what, int n)
        {
            if (n > 0)
            {
                skipped[what] = n;
            }
        }

        Skipped("tasks", entries.ContainsKey(root + "tasks.json") ? 1 : 0);
        Skipped("events", entries.ContainsKey(root + "events.json") ? 1 : 0);
        Skipped("databases", entries.Keys.Count(k => k.StartsWith(root + "databases/", StringComparison.Ordinal)));
        Skipped("slideDecks", entries.Keys.Count(k => k.StartsWith(root + "slides/", StringComparison.Ordinal)));
        Skipped("whiteboards", entries.Keys.Count(k => k.StartsWith(root + "whiteboards/", StringComparison.Ordinal) && k.EndsWith(".json", StringComparison.Ordinal)));
        return Result<SpaceImportResultDto>.Success(new SpaceImportResultDto(space.Key, title, pages, attachments, comments, warnings.Take(50).ToList(), skipped));
    }

    /// <summary>Page links (<c>/spaces/KEY/pages/ID</c>) and attachment references still point at the ids of the source workspace; point them at the new ones.</summary>
    private async Task RewriteReferencesAsync(Guid spaceId, string sourceKey, string newKey, Dictionary<Guid, Guid> pages, Dictionary<Guid, Guid> attachments, CancellationToken ct)
    {
        var map = pages.Concat(attachments).ToDictionary(k => k.Key.ToString("D"), k => k.Value.ToString("D"), StringComparer.OrdinalIgnoreCase);
        var ids = await db.PageVersions.AsNoTracking().Where(v => v.VersionType == VersionType.Import && db.Nodes.Any(n => n.Id == v.ContentId && n.SpaceId == spaceId)).Select(v => v.Id).ToListAsync(ct).ConfigureAwait(false);
        foreach (var batch in ids.Chunk(50))
        {
            foreach (var v in await db.PageVersions.Where(x => batch.Contains(x.Id)).ToListAsync(ct).ConfigureAwait(false))
            {
                var json = v.ContentJson;
                var rewritten = json;
                foreach (var (from, to) in map)
                {
                    if (rewritten.Contains(from, StringComparison.OrdinalIgnoreCase))
                    {
                        rewritten = rewritten.Replace(from, to, StringComparison.OrdinalIgnoreCase);
                    }
                }

                if (!string.Equals(sourceKey, newKey, StringComparison.Ordinal))
                {
                    rewritten = rewritten.Replace($"/spaces/{sourceKey}/", $"/spaces/{newKey}/", StringComparison.Ordinal);
                }

                if (!string.Equals(json, rewritten, StringComparison.Ordinal))
                {
                    v.RewriteImportedBody(RaaviDocument.Parse(rewritten));
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> ReadAsync(ZipArchiveEntry entry, long max, CancellationToken ct)
    {
        if (entry.Length > max)
        {
            throw new DomainException("import.too_big", $"'{entry.Name}' is larger than the allowed size.");
        }

        var stream = entry.Open();
        await using (stream.ConfigureAwait(false))
        {
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int n;
            while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                ms.Write(buffer, 0, n);
                if (ms.Length > max)
                {
                    throw new DomainException("import.too_big", $"'{entry.Name}' is larger than the allowed size.");
                }
            }

            return ms.ToArray();
        }
    }

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? DateOf(JsonElement e, string name) => Str(e, name) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
