using System.Text;
using Raavi.Domain.Common;

namespace Raavi.Wiki.Domain;

public enum ContentStatus
{
    Current = 1,
    Archived = 2,
    Trashed = 3,

    /// <summary>Created but never published: visible to its creator only (ADR-019).</summary>
    Draft = 4,
}

/// <summary>Values are persisted; never renumber. Phase 5 adds Draft and Restore.</summary>
public enum VersionType
{
    /// <summary>Reserved: named draft checkpoints (not used; drafts are autosaved in PageDrafts).</summary>
    Draft = 1,
    Published = 2,

    /// <summary>Published by restoring an older version (<see cref="PageVersion.RestoredFromVersionNumber"/>).</summary>
    Restore = 3,
    Import = 4,
}

/// <summary>
/// Generic content node (ADR-009): tree position, identity and lifecycle of every content type. Bodies live in
/// type-specific tables (<see cref="PageVersion"/> for pages). Ancestry is mirrored in <see cref="ContentClosure"/>.
/// </summary>
public sealed class ContentNode : Entity, ITenantOwned
{
    public const int MaxTitleLength = 255;
    public const int MaxContentTypeLength = 32;

    private ContentNode() { }

    public Guid TenantId { get; private set; }

    public Guid SpaceId { get; private set; }

    /// <summary>Null for top-level content of the space.</summary>
    public Guid? ParentId { get; private set; }

    public string ContentType { get; private set; } = string.Empty;

    public string Title { get; private set; } = string.Empty;

    public string Slug { get; private set; } = string.Empty;

    public ContentStatus Status { get; private set; } = ContentStatus.Current;

    /// <summary>Order among siblings (0-based, dense).</summary>
    public int Position { get; private set; }

    public Guid? CurrentVersionId { get; private set; }

    public int CurrentVersionNumber { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid UpdatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public Guid? DeletedBy { get; private set; }

    /// <summary>Optimistic concurrency revision (title/body/move/lifecycle). Sibling re-numbering does not change it.</summary>
    public int Revision { get; private set; } = 1;

    /// <summary>Import provenance: source system, the item's identifier there and its address (shown as "Imported from …").</summary>
    public string? SourceSystem { get; private set; }

    public string? SourceId { get; private set; }

    public string? SourceUrl { get; private set; }

    public void MarkImported(string sourceSystem, string sourceId, string? sourceUrl, DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        SourceSystem = sourceSystem;
        SourceId = sourceId;
        SourceUrl = sourceUrl is { Length: > 500 } ? sourceUrl[..500] : sourceUrl;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>A new page that exists only as a draft until it is first published.</summary>
    public static ContentNode CreateDraft(Guid spaceId, Guid? parentId, string contentType, string title, int position, Guid userId, DateTimeOffset now)
    {
        var node = Create(spaceId, parentId, contentType, title, position, userId, now);
        node.Status = ContentStatus.Draft;
        return node;
    }

    public static ContentNode Create(Guid spaceId, Guid? parentId, string contentType, string title, int position, Guid userId, DateTimeOffset now)
    {
        var node = new ContentNode
        {
            SpaceId = spaceId,
            ParentId = parentId,
            ContentType = contentType,
            Position = position,
            CreatedBy = userId,
            CreatedAt = now,
            UpdatedBy = userId,
            UpdatedAt = now,
        };
        node.SetTitle(title);
        return node;
    }

    public static string NormalizeTitle(string? title)
    {
        var t = (title ?? string.Empty).Trim();
        if (t.Length is 0 or > MaxTitleLength)
        {
            throw new DomainException("content.title_invalid", "Title is required (max 255 characters).");
        }

        if (t.Any(char.IsControl))
        {
            throw new DomainException("content.title_invalid", "Title must not contain control characters.");
        }

        return t;
    }

    /// <summary>URL decoration only (ids identify content). Keeps Persian and Latin letters/digits, joins words with '-'.</summary>
    public static string Slugify(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var sb = new StringBuilder(title.Length);
        var dash = false;
        foreach (var ch in title.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                dash = false;
            }
            else if (!dash && sb.Length > 0)
            {
                sb.Append('-');
                dash = true;
            }

            if (sb.Length >= 120)
            {
                break;
            }
        }

        return sb.ToString().TrimEnd('-');
    }

    public void SetTitle(string title)
    {
        Title = NormalizeTitle(title);
        Slug = Slugify(Title);
    }

    public void RecordVersion(PageVersion version, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(version);
        CurrentVersionId = version.Id;
        CurrentVersionNumber = version.VersionNumber;
        PublishedAt = now;
        if (Status == ContentStatus.Draft)
        {
            Status = ContentStatus.Current;
        }
        Touch(userId, now);
    }

    public void Touch(Guid userId, DateTimeOffset now)
    {
        UpdatedBy = userId;
        UpdatedAt = now;
        Revision++;
    }

    public void MoveTo(Guid spaceId, Guid? parentId, Guid userId, DateTimeOffset now)
    {
        SpaceId = spaceId;
        ParentId = parentId;
        Touch(userId, now);
    }

    /// <summary>Re-numbering among siblings: no revision change (does not conflict with concurrent edits).</summary>
    public void SetPosition(int position) => Position = position;

    /// <summary>A descendant follows its moved ancestor into another space.</summary>
    public void FollowAncestorToSpace(Guid spaceId) => SpaceId = spaceId;

    public void SetStatus(ContentStatus status, Guid userId, DateTimeOffset now)
    {
        Status = status;
        if (status == ContentStatus.Trashed)
        {
            DeletedAt = now;
            DeletedBy = userId;
        }
        else
        {
            DeletedAt = null;
            DeletedBy = null;
        }

        Touch(userId, now);
    }

    public void EnsureCurrent()
    {
        if (Status != ContentStatus.Current)
        {
            throw new DomainException("content.not_current", "Archived content is read-only.");
        }
    }
}

/// <summary>Ancestor/descendant pairs incl. the self pair (depth 0) for every node (ADR-009).</summary>
public sealed class ContentClosure : ITenantOwned
{
    private ContentClosure() { }

    public Guid TenantId { get; private set; }

    public Guid AncestorId { get; private set; }

    public Guid DescendantId { get; private set; }

    public int Depth { get; private set; }

    public static ContentClosure Link(Guid ancestorId, Guid descendantId, int depth) => new() { AncestorId = ancestorId, DescendantId = descendantId, Depth = depth };
}

/// <summary>One saved version of a page body (canonical Raavi Document JSON, ADR-009/ADR-010).</summary>
public sealed class PageVersion : Entity, ITenantOwned
{
    private PageVersion() { }

    public Guid TenantId { get; private set; }

    public Guid ContentId { get; private set; }

    public int VersionNumber { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string ContentJson { get; private set; } = string.Empty;

    public int SchemaVersion { get; private set; }

    /// <summary>Lower-case hex SHA-256 of <see cref="ContentJson"/>.</summary>
    public string ContentHash { get; private set; } = string.Empty;

    public string? ChangeSummary { get; private set; }

    public VersionType VersionType { get; private set; } = VersionType.Published;

    /// <summary>Nullable for unmapped imported authors (Phase 8).</summary>
    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Version number this version was restored from (<see cref="VersionType.Restore"/> only).</summary>
    public int? RestoredFromVersionNumber { get; private set; }

    /// <summary>Version number in the source system (imports) and the original author when they could not be mapped to a user.</summary>
    public string? SourceVersionIdentifier { get; private set; }

    public string? SourceAuthorJson { get; private set; }

    /// <summary>An imported version keeps its original timestamp, author (when mapped) and comment; its number is the Raavi sequence.</summary>
    public static PageVersion Import(Guid contentId, int number, string title, RaaviDocument body, string? changeSummary, Guid? authorUserId, string sourceVersionIdentifier, string? sourceAuthorJson, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceVersionIdentifier);
        var v = Create(contentId, number, title, body, changeSummary is { Length: > 500 } ? changeSummary[..500] : changeSummary, authorUserId ?? Guid.Empty, createdAt);
        v.VersionType = VersionType.Import;
        v.CreatedBy = authorUserId;
        v.SourceVersionIdentifier = sourceVersionIdentifier;
        v.SourceAuthorJson = sourceAuthorJson;
        return v;
    }

    /// <summary>Links an imported version to a Raavi user once the source author could be mapped (never overwrites a known author).</summary>
    public void AssignImportedAuthor(Guid userId)
    {
        if (VersionType == VersionType.Import && CreatedBy is null && userId != Guid.Empty)
        {
            CreatedBy = userId;
        }
    }

    /// <summary>Link resolution after an import rewrites addresses inside the imported body; nothing else about the version changes.</summary>
    public void RewriteImportedBody(RaaviDocument body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (VersionType != VersionType.Import)
        {
            throw new DomainException("version.immutable", "Only imported versions can be rewritten.");
        }

        ContentJson = body.Json;
        SchemaVersion = body.SchemaVersion;
        ContentHash = body.Hash;
    }

    public static PageVersion Restore(Guid contentId, int number, PageVersion source, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(source);
        var v = Create(contentId, number, source.Title, RaaviDocument.Parse(source.ContentJson), null, userId, now);
        v.VersionType = VersionType.Restore;
        v.RestoredFromVersionNumber = source.VersionNumber;
        return v;
    }

    public static PageVersion Create(Guid contentId, int number, string title, RaaviDocument body, string? changeSummary, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(body);
        var summary = changeSummary?.Trim();
        if (summary is { Length: > 500 })
        {
            throw new DomainException("content.change_summary_too_long", "Change summary is too long (max 500 characters).");
        }

        return new PageVersion
        {
            ContentId = contentId,
            VersionNumber = number,
            Title = title,
            ContentJson = body.Json,
            SchemaVersion = body.SchemaVersion,
            ContentHash = body.Hash,
            ChangeSummary = string.IsNullOrEmpty(summary) ? null : summary,
            CreatedBy = userId,
            CreatedAt = now,
        };
    }
}

public sealed class Label : Entity, ITenantOwned
{
    public const int MaxLength = 100;
    public const int MaxDisplayLength = 200;
    public const int MaxDescriptionLength = 1000;
    public const int MaxColorLength = 32;
    public const int MaxDepth = 5;
    public const int MaxAliases = 20;

    private Label() { }

    public Guid TenantId { get; private set; }

    /// <summary>Confluence-safe token (= <see cref="Slug"/>). Kept for import, workflows and contentByLabel.</summary>
    public string Name { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>Folded uniqueness key (Persian/Arabic letter folding via SearchText).</summary>
    public string NormalizedName { get; private set; } = string.Empty;

    /// <summary>Stable URL key; frozen after create.</summary>
    public string Slug { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public string? Color { get; private set; }

    public Guid? ParentId { get; private set; }

    public int DisplayOrder { get; private set; }

    public bool IsSystem { get; private set; }

    public bool IsActive { get; private set; } = true;

    public DateTimeOffset? ArchivedAt { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid UpdatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Confluence-style token: lower-case, no whitespace or , # / \ ? &amp;.</summary>
    public static string Normalize(string? name)
    {
        var n = (name ?? string.Empty).Trim().ToLowerInvariant();
        if (n.Length is 0 or > MaxLength || n.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is ',' or '#' or '/' or '\\' or '?' or '&'))
        {
            throw new DomainException("label.invalid", "Labels are 1–100 characters without spaces or , # / \\ ? &.");
        }

        return n;
    }

    public static string FoldDisplay(string? display)
    {
        var folded = Raavi.Application.Common.SearchText.Normalize(display).Trim();
        while (folded.Contains("  ", StringComparison.Ordinal))
        {
            folded = folded.Replace("  ", " ", StringComparison.Ordinal);
        }

        return folded;
    }

    /// <summary>Legacy create from a Confluence-style name (import, page assign, workflows).</summary>
    public static Label Create(string name, DateTimeOffset now) => Create(name, Guid.Empty, now);

    public static Label Create(string name, Guid userId, DateTimeOffset now)
    {
        var token = Normalize(name);
        return new()
        {
            Name = token,
            DisplayName = token,
            NormalizedName = FoldDisplay(token),
            Slug = token,
            IsActive = true,
            CreatedBy = userId,
            CreatedAt = now,
            UpdatedBy = userId,
            UpdatedAt = now,
        };
    }

    /// <summary>Admin create with a display name; slug/name derived and frozen.</summary>
    public static Label CreateFromDisplay(string displayName, string? description, string? color, Guid? parentId, int displayOrder, Guid userId, DateTimeOffset now)
    {
        var display = RequireDisplay(displayName);
        var slug = SlugFromDisplay(display);
        var label = new Label
        {
            Name = slug,
            DisplayName = display,
            NormalizedName = FoldDisplay(display),
            Slug = slug,
            ParentId = parentId is { } p && p != Guid.Empty ? p : null,
            DisplayOrder = displayOrder,
            IsActive = true,
            CreatedBy = userId,
            CreatedAt = now,
            UpdatedBy = userId,
            UpdatedAt = now,
        };
        label.SetDescription(description);
        label.SetColor(color);
        return label;
    }

    public void UpdateMeta(string displayName, string? description, string? color, int displayOrder, Guid userId, DateTimeOffset now)
    {
        DisplayName = RequireDisplay(displayName);
        NormalizedName = FoldDisplay(DisplayName);
        SetDescription(description);
        SetColor(color);
        DisplayOrder = displayOrder;
        Touch(userId, now);
    }

    public void Move(Guid? parentId, int displayOrder, Guid userId, DateTimeOffset now)
    {
        ParentId = parentId is { } p && p != Guid.Empty ? p : null;
        DisplayOrder = displayOrder;
        Touch(userId, now);
    }

    public void Archive(Guid userId, DateTimeOffset now)
    {
        if (ArchivedAt is not null)
        {
            return;
        }

        ArchivedAt = now;
        IsActive = false;
        Touch(userId, now);
    }

    public void Restore(Guid userId, DateTimeOffset now)
    {
        ArchivedAt = null;
        IsActive = true;
        Touch(userId, now);
    }

    private void Touch(Guid userId, DateTimeOffset now)
    {
        UpdatedBy = userId;
        UpdatedAt = now;
    }

    private void SetDescription(string? description)
    {
        var d = description?.Trim();
        if (d is { Length: > MaxDescriptionLength })
        {
            throw new DomainException("label.description_too_long", "Description is too long.");
        }

        Description = string.IsNullOrEmpty(d) ? null : d;
    }

    private void SetColor(string? color)
    {
        var c = color?.Trim();
        if (c is { Length: > MaxColorLength })
        {
            throw new DomainException("label.color_invalid", "Colour is not valid.");
        }

        Color = string.IsNullOrEmpty(c) ? null : c;
    }

    private static string RequireDisplay(string? displayName)
    {
        var d = (displayName ?? string.Empty).Trim();
        if (d.Length is 0 or > MaxDisplayLength || d.Any(char.IsControl))
        {
            throw new DomainException("label.display_invalid", "Display name is required (max 200 characters).");
        }

        return d;
    }

    internal static string SlugFromDisplay(string display)
    {
        // Prefer letter/digit slug; if Normalize would fail on dashes, keep dashes (allowed).
        var sb = new StringBuilder(display.Length);
        var dash = false;
        foreach (var ch in display.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                dash = false;
            }
            else if (!dash && sb.Length > 0)
            {
                sb.Append('-');
                dash = true;
            }

            if (sb.Length >= MaxLength)
            {
                break;
            }
        }

        var slug = sb.ToString().TrimEnd('-');
        if (slug.Length == 0 || slug.Any(c => c is ',' or '#' or '/' or '\\' or '?' or '&' || char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw new DomainException("label.invalid", "Could not derive a URL slug from the display name.");
        }

        return slug;
    }
}

public sealed class LabelAlias : Entity, ITenantOwned
{
    public const int MaxAliasLength = 200;

    private LabelAlias() { }

    public Guid TenantId { get; private set; }

    public Guid LabelId { get; private set; }

    public string Alias { get; private set; } = string.Empty;

    public string NormalizedAlias { get; private set; } = string.Empty;

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static LabelAlias Create(Guid labelId, string alias, Guid userId, DateTimeOffset now)
    {
        var a = (alias ?? string.Empty).Trim();
        if (a.Length is 0 or > MaxAliasLength || a.Any(char.IsControl))
        {
            throw new DomainException("label.alias_invalid", "Alias is required (max 200 characters).");
        }

        var folded = Label.FoldDisplay(a);
        if (folded.Length == 0)
        {
            throw new DomainException("label.alias_invalid", "Alias is required (max 200 characters).");
        }

        return new()
        {
            LabelId = labelId,
            Alias = a,
            NormalizedAlias = folded,
            CreatedBy = userId,
            CreatedAt = now,
        };
    }
}

public sealed class ContentLabel : ITenantOwned
{
    private ContentLabel() { }

    public Guid TenantId { get; private set; }

    public Guid ContentId { get; private set; }

    public Guid LabelId { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static ContentLabel Create(Guid contentId, Guid labelId, Guid userId, DateTimeOffset now) =>
        new() { ContentId = contentId, LabelId = labelId, CreatedBy = userId, CreatedAt = now };
}

/// <summary>A page template of one space, or of the whole tenant when <see cref="SpaceId"/> is null.</summary>
public sealed class Template : Entity, ITenantOwned
{
    public const int MaxNameLength = 200;

    private Template() { }

    public Guid TenantId { get; private set; }

    public Guid? SpaceId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public string ContentJson { get; private set; } = string.Empty;

    public int SchemaVersion { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid UpdatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public int Revision { get; private set; } = 1;

    public static Template Create(Guid? spaceId, string name, string? description, RaaviDocument body, Guid userId, DateTimeOffset now)
    {
        var t = new Template { SpaceId = spaceId, CreatedBy = userId, CreatedAt = now };
        t.Update(name, description, body, userId, now);
        t.Revision = 1;
        return t;
    }

    public void Update(string name, string? description, RaaviDocument body, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(body);
        var n = (name ?? string.Empty).Trim();
        if (n.Length is 0 or > MaxNameLength)
        {
            throw new DomainException("template.name_invalid", "Template name is required (max 200 characters).");
        }

        var d = description?.Trim();
        if (d is { Length: > 1000 })
        {
            throw new DomainException("template.description_too_long", "Description is too long.");
        }

        Name = n;
        Description = string.IsNullOrEmpty(d) ? null : d;
        ContentJson = body.Json;
        SchemaVersion = body.SchemaVersion;
        UpdatedBy = userId;
        UpdatedAt = now;
        Revision++;
    }
}

/// <summary>Activity feed entry, projected from outbox events (id = outbox event id, so replays are idempotent).</summary>
public sealed class ActivityEntry : ITenantOwned
{
    private ActivityEntry() { }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid SpaceId { get; private set; }

    public Guid? ContentId { get; private set; }

    public Guid ActorId { get; private set; }

    /// <summary>Event type, e.g. <c>page.updated</c>.</summary>
    public string Kind { get; private set; } = string.Empty;

    /// <summary>Title at the time of the event (content may be renamed later).</summary>
    public string Title { get; private set; } = string.Empty;

    public int? VersionNumber { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public static ActivityEntry Create(Guid id, Guid spaceId, Guid? contentId, Guid actorId, string kind, string title, int? version, DateTimeOffset at) =>
        new() { Id = id, SpaceId = spaceId, ContentId = contentId, ActorId = actorId, Kind = kind, Title = title, VersionNumber = version, OccurredAt = at };
}
