using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Content;
using Raavi.Application.Abstractions.Time;
using Raavi.Domain.Common;
using Raavi.Wiki.ContentTypes;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki.Application;

/// <summary>
/// Import writer (ADR-025). Runs inside the importing job's tenant scope and acts for an explicit owner; the migration module
/// has already verified that the owner is still a tenant administrator. It deliberately emits <b>no</b> wiki events (no
/// activity-feed or notification flood); imported content is indexed by the bulk reindex at the end of an import.
/// </summary>
public sealed class ContentImporter(WikiDbContext db, IClock clock) : IContentImporter
{
    public const string LinkPlaceholder = "raavi:confluence-page?";

    public async Task<ImportedSpace> EnsureSpaceAsync(ImportSpaceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ct = cancellationToken;
        var existing = await db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.SourceSystem == request.SourceSystem && s.SourceId == request.SourceId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return new ImportedSpace(existing.Id, existing.Key, false);
        }

        var now = clock.UtcNow;
        var space = Space.Create(request.Key, request.Name, request.Description, SpaceType.Documentation, request.OwnerId, request.CreatedAt ?? now);
        if (await db.Spaces.AnyAsync(s => s.Key == space.Key, ct).ConfigureAwait(false))
        {
            throw new DomainException("space.key_conflict", $"A space with the key '{space.Key}' already exists in this workspace.");
        }

        space.MarkImported(request.SourceSystem, request.SourceId);
        db.Spaces.Add(space);
        foreach (var action in WikiAuthorizer.SpaceActions)
        {
            db.Grants.Add(PermissionGrant.ForSpace(space.Id, PermissionSubjectType.User, request.OwnerId, action, PermissionEffect.Allow));
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new ImportedSpace(space.Id, space.Key, true);
    }

    public async Task<IReadOnlySet<int>> ImportedVersionsAsync(string sourceSystem, string sourceId, CancellationToken cancellationToken)
    {
        var ids = await (from v in db.PageVersions.AsNoTracking()
                         join c in db.Nodes.AsNoTracking() on v.ContentId equals c.Id
                         where c.SourceSystem == sourceSystem && c.SourceId == sourceId && v.SourceVersionIdentifier != null
                         select v.SourceVersionIdentifier!).ToListAsync(cancellationToken).ConfigureAwait(false);
        return ids.Select(i => int.TryParse(i, out var n) ? n : -1).Where(n => n > 0).ToHashSet();
    }

    public async Task<ImportedPage> ImportPageAsync(ImportPageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Versions.Count == 0)
        {
            throw new DomainException("import.no_versions", "A page needs at least one version to be imported.");
        }

        var ct = cancellationToken;
        var warnings = new List<string>();

        // Validate everything first: a page is written completely or not at all.
        var parsed = request.Versions.OrderBy(v => v.SourceVersionNumber)
            .Select(v => (Version: v, Body: RaaviDocument.Parse($"{{\"schema\":\"raavi.doc\",\"schemaVersion\":{RaaviDocument.CurrentSchemaVersion},\"doc\":{v.DocJson}}}"), Title: ContentNode.NormalizeTitle(v.Title)))
            .ToList();

        var node = await db.Nodes.FirstOrDefaultAsync(n => n.SourceSystem == request.SourceSystem && n.SourceId == request.SourceId, ct).ConfigureAwait(false);
        var created = node is null;
        var lastNumber = 0;
        var rewritten = 0;
        var have = new HashSet<int>();
        if (node is null)
        {
            Guid? parentId = null;
            if (request.ParentSourceId is { } parentSource)
            {
                parentId = await db.Nodes.Where(n => n.SourceSystem == request.SourceSystem && n.SourceId == parentSource).Select(n => (Guid?)n.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false)
                    ?? throw new DomainException("import.parent_missing", $"The parent page '{parentSource}' has not been imported yet.");
            }

            var first = parsed[0];
            node = ContentNode.Create(request.SpaceId, parentId, PageContentProvider.Type, first.Title, request.Position, request.ActorId, first.Version.CreatedAt);
            node.MarkImported(request.SourceSystem, request.SourceId, request.SourceUrl, first.Version.CreatedAt, parsed[^1].Version.CreatedAt);
            db.Nodes.Add(node);
            db.Closure.Add(ContentClosure.Link(node.Id, node.Id, 0));
            if (parentId is { } p)
            {
                var ancestors = await db.Closure.AsNoTracking().Where(c => c.DescendantId == p).ToListAsync(ct).ConfigureAwait(false);
                foreach (var a in ancestors)
                {
                    db.Closure.Add(ContentClosure.Link(a.AncestorId, node.Id, a.Depth + 1));
                }
            }
        }
        else
        {
            lastNumber = await db.PageVersions.Where(v => v.ContentId == node.Id).Select(v => (int?)v.VersionNumber).MaxAsync(ct).ConfigureAwait(false) ?? 0;
            var identifiers = await db.PageVersions.AsNoTracking().Where(v => v.ContentId == node.Id && v.SourceVersionIdentifier != null).Select(v => v.SourceVersionIdentifier!).ToListAsync(ct).ConfigureAwait(false);
            have = identifiers.Select(i => int.TryParse(i, out var n) ? n : -1).ToHashSet();
            if (request.Reconvert)
            {
                // Pages imported by an older converter: refresh the stored bodies when the conversion now differs.
                var existing = await db.PageVersions.Where(v => v.ContentId == node.Id && v.VersionType == VersionType.Import && v.SourceVersionIdentifier != null).ToListAsync(ct).ConfigureAwait(false);
                foreach (var (version, body, _) in parsed)
                {
                    var current = existing.FirstOrDefault(v => v.SourceVersionIdentifier == version.SourceVersionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    if (current is not null && current.ContentHash != body.Hash)
                    {
                        current.RewriteImportedBody(body);
                        rewritten++;
                    }

                    if (current is not null && version.AuthorUserId is { } author)
                    {
                        current.AssignImportedAuthor(author);
                    }
                }
            }
        }

        PageVersion? newest = null;
        var imported = 0;
        foreach (var (version, body, title) in parsed)
        {
            if (have.Contains(version.SourceVersionNumber))
            {
                continue;
            }

            var author = version.AuthorKey is null && version.AuthorDisplayName is null
                ? null
                : JsonSerializer.Serialize(new { key = version.AuthorKey, name = version.AuthorDisplayName });
            var pv = PageVersion.Import(node.Id, ++lastNumber, title, body, version.Comment, version.AuthorUserId, version.SourceVersionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), author, version.CreatedAt);
            db.PageVersions.Add(pv);
            newest = pv;
            imported++;
        }

        if (newest is not null)
        {
            node.SetTitle(newest.Title);
            node.RecordVersion(newest, request.ActorId, newest.CreatedAt);
        }

        await AddLabelsAsync(node, request, warnings, ct).ConfigureAwait(false);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException) when (created)
        {
            // A concurrent run of the same import created this page first: the unique source key protected us. Treat as done.
            db.ChangeTracker.Clear();
            var winner = await db.Nodes.AsNoTracking().FirstAsync(n => n.SourceSystem == request.SourceSystem && n.SourceId == request.SourceId, ct).ConfigureAwait(false);
            return new ImportedPage(winner.Id, false, 0, request.Versions.Count, warnings, 0);
        }

        return new ImportedPage(node.Id, created, imported, parsed.Count - imported, warnings, rewritten);
    }

    private async Task AddLabelsAsync(ContentNode node, ImportPageRequest request, List<string> warnings, CancellationToken ct)
    {
        foreach (var raw in request.Labels.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string name;
            try
            {
                name = Label.Normalize(raw);
            }
            catch (DomainException)
            {
                warnings.Add($"The label '{raw}' is not valid in Raavi and was skipped.");
                continue;
            }

            var label = db.Labels.Local.FirstOrDefault(l => l.Name == name) ?? await db.Labels.FirstOrDefaultAsync(l => l.Name == name, ct).ConfigureAwait(false);
            if (label is null)
            {
                label = Label.Create(name, clock.UtcNow);
                db.Labels.Add(label);
            }

            var linked = db.Entry(node).State != EntityState.Added
                && db.Entry(label).State != EntityState.Added
                && await db.ContentLabels.AnyAsync(l => l.ContentId == node.Id && l.LabelId == label.Id, ct).ConfigureAwait(false);
            if (!linked)
            {
                db.ContentLabels.Add(ContentLabel.Create(node.Id, label.Id, request.ActorId, clock.UtcNow));
            }
        }
    }

    public async Task<IReadOnlySet<Guid>> ImportedAttachmentIdsAsync(string sourceSystem, string pageSourceId, CancellationToken cancellationToken)
    {
        var ids = await (from a in db.Attachments.AsNoTracking()
                         join c in db.Nodes.AsNoTracking() on a.ContentId equals c.Id
                         where c.SourceSystem == sourceSystem && c.SourceId == pageSourceId
                         select a.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return ids.ToHashSet();
    }

    public async Task<AttachmentImportResult> ImportAttachmentsAsync(string sourceSystem, string pageSourceId, IReadOnlyList<ImportAttachment> files, Guid actorId, bool replaceChanged, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        var ct = cancellationToken;
        var contentId = await db.Nodes.Where(n => n.SourceSystem == sourceSystem && n.SourceId == pageSourceId).Select(n => (Guid?)n.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new DomainException("import.page_missing", $"The page '{pageSourceId}' has not been imported.");
        var existing = await db.Attachments.Where(a => a.ContentId == contentId).ToListAsync(ct).ConfigureAwait(false);
        var have = existing.Select(a => a.Id).ToHashSet();
        var created = 0;
        var replaced = 0;
        foreach (var f in files)
        {
            if (f.Data.Length == 0)
            {
                continue;
            }

            var id = ImportIds.Attachment(db.CurrentTenantId, sourceSystem, pageSourceId, f.FileName);
            var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(f.Data));
            if (!have.Add(id))
            {
                var current = existing.FirstOrDefault(a => a.Id == id);
                if (replaceChanged && current is not null && current.Sha256 != sha)
                {
                    current.ReplaceImportedContent(f.Data, sha);
                    replaced++;
                }

                continue;
            }

            db.Attachments.Add(Attachment.Import(id, contentId, f.FileName, f.Data, sha, actorId, f.CreatedAt));
            created++;
        }

        if (created + replaced > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return new AttachmentImportResult(created, replaced);
    }

    public async Task<bool> ApplyRestrictionsAsync(string sourceSystem, string pageSourceId, bool restrictView, IReadOnlyList<ImportSubject> view, bool restrictEdit, IReadOnlyList<ImportSubject> edit, Guid ownerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(edit);
        var ct = cancellationToken;
        var contentId = await db.Nodes.Where(n => n.SourceSystem == sourceSystem && n.SourceId == pageSourceId).Select(n => (Guid?)n.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (contentId is null)
        {
            return false;
        }

        var existing = await db.Restrictions.AsNoTracking().Where(r => r.ContentId == contentId).Select(r => r.Kind).Distinct().ToListAsync(ct).ConfigureAwait(false);
        var changed = false;
        foreach (var (kind, wanted, list) in new[] { (RestrictionKind.View, restrictView, view), (RestrictionKind.Edit, restrictEdit, edit) })
        {
            if (!wanted || existing.Contains(kind))
            {
                continue;
            }

            var subjects = new HashSet<(PermissionSubjectType, Guid)> { (PermissionSubjectType.User, ownerId) };
            foreach (var s in list)
            {
                subjects.Add((s.Kind == ImportSubjectKind.User ? PermissionSubjectType.User : PermissionSubjectType.Group, s.Id));
            }

            foreach (var (type, id) in subjects)
            {
                db.Restrictions.Add(ContentRestriction.Create(contentId.Value, kind, type, id));
            }

            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return changed;
    }

    public async Task<CommentImportResult> ImportCommentsAsync(string sourceSystem, string pageSourceId, IReadOnlyList<ImportComment> comments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(comments);
        var ct = cancellationToken;
        var node = await db.Nodes.AsNoTracking().Where(n => n.SourceSystem == sourceSystem && n.SourceId == pageSourceId).Select(n => new { n.Id, n.CurrentVersionNumber }).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new DomainException("import.page_missing", $"The page '{pageSourceId}' has not been imported.");
        var existing = await db.Comments.Where(c => c.ContentId == node.Id && c.SourceSystem == sourceSystem).ToListAsync(ct).ConfigureAwait(false);
        var bySource = existing.Where(c => c.SourceId != null).ToDictionary(c => c.SourceId!, StringComparer.Ordinal);
        var warnings = new List<string>();
        var created = 0;
        var skipped = 0;

        // Parents before replies (a reply may only be created once its thread root exists).
        var pending = comments.OrderBy(c => c.CreatedAt).ThenBy(c => c.SourceId, StringComparer.Ordinal).ToList();
        var byId = pending.ToDictionary(c => c.SourceId, StringComparer.Ordinal);
        Comment? RootOf(ImportComment c)
        {
            var current = c;
            for (var guard = 0; guard < 100; guard++)
            {
                if (current.ParentSourceId is null || !byId.TryGetValue(current.ParentSourceId, out var parent))
                {
                    return bySource.TryGetValue(current.ParentSourceId ?? current.SourceId, out var known) ? (known.IsRoot ? known : bySource.Values.FirstOrDefault(x => x.Id == known.ThreadId)) : null;
                }

                current = parent;
            }

            return null;
        }

        foreach (var c in pending)
        {
            if (bySource.ContainsKey(c.SourceId))
            {
                skipped++;
                continue;
            }

            try
            {
                var body = RaaviDocument.Parse($"{{\"schema\":\"raavi.doc\",\"schemaVersion\":{RaaviDocument.CurrentSchemaVersion},\"doc\":{c.DocJson}}}");
                Comment? root = null;
                if (c.ParentSourceId is not null)
                {
                    root = RootOf(c);
                    if (root is null)
                    {
                        warnings.Add($"The reply '{c.SourceId}' was imported as a new thread because its parent is missing.");
                    }
                }

                var kind = c.Inline && !string.IsNullOrWhiteSpace(c.Selection) ? CommentKind.Inline : CommentKind.Page;
                if (c.Inline && kind == CommentKind.Page && root is null)
                {
                    warnings.Add($"The inline comment '{c.SourceId}' has no quoted text and was imported as a page comment.");
                }

                var anchor = kind == CommentKind.Inline ? CommentAnchor.Create(c.Selection, string.Empty, string.Empty) : null;
                var imported = Comment.Import(node.Id, root, kind, anchor, body, node.CurrentVersionNumber, c.AuthorUserId, c.AuthorName, c.CreatedAt, sourceSystem, c.SourceId);
                if (root is null && c.Resolved)
                {
                    imported.MarkImportedResolved(c.CreatedAt);
                }

                db.Comments.Add(imported);
                bySource[c.SourceId] = imported;
                created++;
            }
            catch (DomainException ex)
            {
                skipped++;
                warnings.Add($"The comment '{c.SourceId}' was skipped: {ex.Message}");
            }
        }

        if (created > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return new CommentImportResult(created, skipped, warnings);
    }

    public async Task SetHomePageAsync(string sourceSystem, string sourceSpaceId, string sourcePageId, Guid actorId, CancellationToken cancellationToken)
    {
        var space = await db.Spaces.FirstOrDefaultAsync(s => s.SourceSystem == sourceSystem && s.SourceId == sourceSpaceId, cancellationToken).ConfigureAwait(false);
        var page = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.SourceSystem == sourceSystem && n.SourceId == sourcePageId, cancellationToken).ConfigureAwait(false);
        if (space is null || page is null || page.SpaceId != space.Id || space.HomePageId == page.Id)
        {
            return;
        }

        space.SetHomePage(page.Id, actorId, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImportedCounts> CountAsync(string sourceSystem, string sourceSpaceId, CancellationToken cancellationToken)
    {
        var spaceId = await db.Spaces.Where(s => s.SourceSystem == sourceSystem && s.SourceId == sourceSpaceId).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (spaceId is null)
        {
            return new ImportedCounts(0, 0);
        }

        var pages = await db.Nodes.CountAsync(n => n.SourceSystem == sourceSystem && n.SpaceId == spaceId, cancellationToken).ConfigureAwait(false);
        var versions = await (from v in db.PageVersions join c in db.Nodes on v.ContentId equals c.Id where c.SourceSystem == sourceSystem && c.SpaceId == spaceId select v.Id).CountAsync(cancellationToken).ConfigureAwait(false);
        return new ImportedCounts(pages, versions);
    }

    public async Task<LinkPassResult> ResolveLinksAsync(string sourceSystem, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var spaces = await db.Spaces.AsNoTracking().Where(s => s.SourceSystem == sourceSystem).Select(s => new { s.Id, s.Key, s.SourceId }).ToListAsync(ct).ConfigureAwait(false);
        var spaceById = spaces.ToDictionary(s => s.Id);
        var spaceBySource = spaces.ToDictionary(s => s.SourceId!, StringComparer.OrdinalIgnoreCase);
        var pages = await db.Nodes.AsNoTracking().Where(n => n.SourceSystem == sourceSystem).Select(n => new { n.Id, n.SpaceId, n.Title }).ToListAsync(ct).ConfigureAwait(false);
        var byTitle = pages.GroupBy(p => (p.SpaceId, p.Title)).ToDictionary(g => g.Key, g => g.First().Id);

        var resolved = 0;
        var unresolved = 0;
        var examples = new List<UnresolvedLink>();
        var contentOf = pages.ToDictionary(p => p.Id);
        var ids = await db.PageVersions.AsNoTracking()
            .Where(v => v.VersionType == VersionType.Import && v.ContentJson.Contains(LinkPlaceholder))
            .Join(db.Nodes.Where(n => n.SourceSystem == sourceSystem), v => v.ContentId, n => n.Id, (v, n) => v.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var batch in ids.Chunk(50))
        {
            var versions = await db.PageVersions.Where(v => batch.Contains(v.Id)).ToListAsync(ct).ConfigureAwait(false);
            foreach (var v in versions)
            {
                var root = JsonNode.Parse(v.ContentJson)!.AsObject();
                var changed = false;
                Rewrite(root["doc"]);
                if (changed)
                {
                    v.RewriteImportedBody(RaaviDocument.Parse(root.ToJsonString()));
                }

                void Rewrite(JsonNode? node)
                {
                    if (node is not JsonObject o)
                    {
                        return;
                    }

                    if (o["marks"] is JsonArray marks)
                    {
                        for (var i = marks.Count - 1; i >= 0; i--)
                        {
                            var href = (string?)marks[i]?["attrs"]?["href"];
                            if (href is null || !href.StartsWith(LinkPlaceholder, StringComparison.Ordinal))
                            {
                                continue;
                            }

                            changed = true;
                            var (space, title) = ParseTarget(href);
                            Guid? targetSpace = string.IsNullOrEmpty(space)
                                ? contentOf.TryGetValue(v.ContentId, out var own) ? own.SpaceId : null
                                : spaceBySource.TryGetValue(space, out var s) ? s.Id : null;
                            if (targetSpace is { } ts && byTitle.TryGetValue((ts, title), out var target))
                            {
                                marks[i]!["attrs"]!["href"] = $"/spaces/{spaceById[ts].Key}/pages/{target:D}";
                                resolved++;
                            }
                            else
                            {
                                marks.RemoveAt(i); // keep the text, drop the dead link
                                unresolved++;
                                if (examples.Count < 50)
                                {
                                    examples.Add(new UnresolvedLink(space, title, v.ContentId));
                                }
                            }
                        }

                        if (marks.Count == 0)
                        {
                            o.Remove("marks");
                        }
                    }

                    if (o["content"] is JsonArray children)
                    {
                        foreach (var child in children)
                        {
                            Rewrite(child);
                        }
                    }
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return new LinkPassResult(resolved, unresolved, examples);
    }

    private static (string Space, string Title) ParseTarget(string href)
    {
        string space = string.Empty, title = string.Empty;
        foreach (var pair in href[LinkPlaceholder.Length..].Split('&'))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                continue;
            }

            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            if (pair.StartsWith("space=", StringComparison.Ordinal))
            {
                space = value;
            }
            else if (pair.StartsWith("title=", StringComparison.Ordinal))
            {
                title = value;
            }
        }

        return (space, title);
    }
}
