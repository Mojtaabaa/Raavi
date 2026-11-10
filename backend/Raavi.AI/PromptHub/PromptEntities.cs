using System.Text;
using System.Text.Json;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Domain.Common;

namespace Raavi.AI.PromptHub;

public enum PromptVisibility
{
    Personal = 1,
    Tenant = 2,
}

public enum PromptStatus
{
    Draft = 1,
    Published = 2,
    Archived = 3,
    Deprecated = 4,
}

public enum CategoryStatus
{
    Active = 1,
    Archived = 2,
}

/// <summary>How a prompt may be used (flags): run through Managed AI, copied for an external tool, previewed.</summary>
[Flags]
public enum PromptModes
{
    None = 0,
    Automatic = 1,
    Copy = 2,
    Preview = 4,
    All = Automatic | Copy | Preview,
}

/// <summary>Node of the tenant's prompt category tree (ParentId + SortOrder). Independent of prompts: a category may exist empty.</summary>
public sealed class PromptCategory : Entity, ITenantOwned
{
    public const int MaxNameLength = 120;
    public const int MaxDepth = 8;

    private PromptCategory() { }

    public Guid TenantId { get; private set; }

    public Guid? ParentId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public int SortOrder { get; private set; }

    public CategoryStatus Status { get; private set; } = CategoryStatus.Active;

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static PromptCategory Create(Guid? parentId, string name, string? description, int sortOrder, Guid userId, DateTimeOffset now) =>
        new() { ParentId = parentId, Name = ValidName(name), Description = ValidDescription(description), SortOrder = sortOrder, CreatedBy = userId, CreatedAt = now, UpdatedAt = now };

    public void Edit(string name, string? description, DateTimeOffset now)
    {
        Name = ValidName(name);
        Description = ValidDescription(description);
        UpdatedAt = now;
    }

    public void Place(Guid? parentId, int sortOrder, DateTimeOffset now)
    {
        ParentId = parentId;
        SortOrder = sortOrder;
        UpdatedAt = now;
    }

    public void SetStatus(CategoryStatus status, DateTimeOffset now)
    {
        Status = status;
        UpdatedAt = now;
    }

    private static string ValidName(string? name)
    {
        var n = (name ?? string.Empty).Trim();
        return n.Length is 0 or > MaxNameLength || n.Any(char.IsControl) ? throw new DomainException("prompt.category_name_invalid", $"A category name needs 1–{MaxNameLength} characters.") : n;
    }

    private static string? ValidDescription(string? description)
    {
        var d = description?.Trim();
        return string.IsNullOrEmpty(d) ? null : d.Length > 1000 ? throw new DomainException("prompt.description_invalid", "The description is too long (1000 characters at most).") : d;
    }
}

/// <summary>A prompt: metadata, ownership and the pointers to its draft and published versions. Content lives in <see cref="PromptVersion"/>.</summary>
public sealed class Prompt : Entity, ITenantOwned
{
    public const int MaxTitleLength = 200;
    public const int MaxTags = 12;
    public static readonly string[] Contexts = ["page", "selection", "section", "pages", "space", "jira-issue", "jira-project", "templar-report", "generic"];
    public static readonly string[] Languages = ["fa", "en", "any"];

    private Prompt() { }

    public Guid TenantId { get; private set; }

    public Guid OwnerId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public Guid? CategoryId { get; private set; }

    public PromptVisibility Visibility { get; private set; } = PromptVisibility.Personal;

    public PromptStatus Status { get; private set; } = PromptStatus.Draft;

    /// <summary>Lower-case tags separated by commas (normalised, at most <see cref="MaxTags"/>).</summary>
    public string Tags { get; private set; } = string.Empty;

    public string Language { get; private set; } = "any";

    /// <summary>Execution contexts the prompt is meant for, comma separated (see <see cref="Contexts"/>).</summary>
    public string ContextKinds { get; private set; } = "generic";

    public PromptModes Modes { get; private set; } = PromptModes.All;

    public Guid? DraftVersionId { get; private set; }

    public Guid? PublishedVersionId { get; private set; }

    public int LatestVersionNumber { get; private set; }

    public Guid? ForkedFromId { get; private set; }

    public Guid? ForkedFromOwnerId { get; private set; }

    public string? ForkedFromTitle { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    /// <summary>Optimistic concurrency token: bumped by every change to the prompt.</summary>
    public int Revision { get; private set; } = 1;

    public static Prompt Create(Guid ownerId, string title, string? description, Guid? categoryId, IEnumerable<string>? tags, string? language, IEnumerable<string>? contexts, PromptModes? modes, DateTimeOffset now)
    {
        var p = new Prompt { OwnerId = ownerId, CreatedAt = now, UpdatedAt = now };
        p.SetDetails(title, description, categoryId, tags, language, contexts, modes ?? PromptModes.All, now, bump: false);
        return p;
    }

    public void SetDetails(string title, string? description, Guid? categoryId, IEnumerable<string>? tags, string? language, IEnumerable<string>? contexts, PromptModes modes, DateTimeOffset now, bool bump = true)
    {
        var t = (title ?? string.Empty).Trim();
        if (t.Length is 0 or > MaxTitleLength || t.Any(char.IsControl))
        {
            throw new DomainException("prompt.title_invalid", $"A prompt title needs 1–{MaxTitleLength} characters.");
        }

        var d = description?.Trim();
        if (d is { Length: > 1000 })
        {
            throw new DomainException("prompt.description_invalid", "The description is too long (1000 characters at most).");
        }

        var lang = (language ?? "any").Trim().ToLowerInvariant();
        if (!Languages.Contains(lang))
        {
            throw new DomainException("prompt.language_invalid", "The language must be fa, en or any.");
        }

        var ctx = (contexts ?? ["generic"]).Select(c => (c ?? string.Empty).Trim().ToLowerInvariant()).Where(c => c.Length > 0).Distinct().ToList();
        if (ctx.Count == 0)
        {
            ctx.Add("generic");
        }

        if (ctx.Any(c => !Contexts.Contains(c)))
        {
            throw new DomainException("prompt.context_invalid", "One of the execution contexts is not supported.");
        }

        if (modes == PromptModes.None || (modes & ~PromptModes.All) != 0)
        {
            throw new DomainException("prompt.modes_invalid", "Choose at least one way to use the prompt.");
        }

        Title = t;
        Description = string.IsNullOrEmpty(d) ? null : d;
        CategoryId = categoryId;
        Tags = NormalizeTags(tags);
        Language = lang;
        ContextKinds = string.Join(',', ctx);
        Modes = modes;
        Touch(now, bump);
    }

    public static string NormalizeTags(IEnumerable<string>? tags)
    {
        var list = (tags ?? []).Select(t => (t ?? string.Empty).Trim().ToLowerInvariant().Replace(',', ' ')).Where(t => t.Length > 0).Distinct().ToList();
        if (list.Count > MaxTags || list.Any(t => t.Length > 40 || t.Any(char.IsControl)))
        {
            throw new DomainException("prompt.tags_invalid", $"At most {MaxTags} tags of up to 40 characters.");
        }

        return string.Join(',', list);
    }

    public IReadOnlyList<string> TagList => Tags.Length == 0 ? [] : Tags.Split(',');

    public IReadOnlyList<string> ContextList => ContextKinds.Split(',');

    public void SetDraft(Guid versionId, int number, DateTimeOffset now)
    {
        DraftVersionId = versionId;
        LatestVersionNumber = Math.Max(LatestVersionNumber, number);
        Touch(now);
    }

    public void Publish(PromptVersion version, DateTimeOffset now)
    {
        PublishedVersionId = version.Id;
        DraftVersionId = DraftVersionId == version.Id ? null : DraftVersionId;
        Status = PromptStatus.Published;
        PublishedAt ??= now;
        Touch(now);
    }

    public void SetVisibility(PromptVisibility visibility, DateTimeOffset now)
    {
        Visibility = visibility;
        Touch(now);
    }

    public void SetStatus(PromptStatus status, DateTimeOffset now)
    {
        Status = status;
        Touch(now);
    }

    public void MoveTo(Guid? categoryId, DateTimeOffset now)
    {
        CategoryId = categoryId;
        Touch(now);
    }

    public void MarkUsed(DateTimeOffset now) => LastUsedAt = now;

    public void RecordFork(Prompt source)
    {
        ForkedFromId = source.Id;
        ForkedFromOwnerId = source.OwnerId;
        ForkedFromTitle = source.Title;
    }

    private void Touch(DateTimeOffset now, bool bump = true)
    {
        UpdatedAt = now;
        if (bump)
        {
            Revision++;
        }
    }
}

/// <summary>
/// One version of a prompt's content. A draft may be edited in place; once <see cref="IsPublished"/> the row is never changed again
/// (the entity offers no way to do it), so executions that reference it always see the text they ran.
/// </summary>
public sealed class PromptVersion : Entity, ITenantOwned
{
    public const int MaxContentBytes = 200_000;

    private PromptVersion() { }

    public Guid TenantId { get; private set; }

    public Guid PromptId { get; private set; }

    public int Number { get; private set; }

    public bool IsPublished { get; private set; }

    /// <summary>The editor document (Raavi document envelope, JSON).</summary>
    public string ContentJson { get; private set; } = string.Empty;

    /// <summary>The template text derived from the document (variables written as <c>{{name}}</c>); this is what is rendered and searched.</summary>
    public string ContentText { get; private set; } = string.Empty;

    public string? ChangeNote { get; private set; }

    /// <summary>The author's input-variable definitions (JSON list of <see cref="PromptInputDefinition"/>); part of the version, so they are immutable once published.</summary>
    public string VariablesJson { get; private set; } = "[]";

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public static PromptVersion CreateDraft(Guid promptId, int number, string contentJson, string? changeNote, Guid userId, DateTimeOffset now, string? variablesJson = null)
    {
        var v = new PromptVersion { PromptId = promptId, Number = number, CreatedBy = userId, CreatedAt = now };
        v.ReplaceContent(contentJson, changeNote, variablesJson);
        return v;
    }

    public void ReplaceContent(string contentJson, string? changeNote, string? variablesJson = null)
    {
        if (IsPublished)
        {
            throw new DomainException("prompt.version_immutable", "A published version cannot be changed.");
        }

        if (string.IsNullOrWhiteSpace(contentJson) || Encoding.UTF8.GetByteCount(contentJson) > MaxContentBytes)
        {
            throw new DomainException("prompt.content_invalid", "The prompt content is empty or too large.");
        }

        ContentText = PromptText.FromDocument(contentJson);
        ContentJson = contentJson;
        if (variablesJson is not null)
        {
            VariablesJson = variablesJson;
        }

        var note = changeNote?.Trim();
        ChangeNote = string.IsNullOrEmpty(note) ? null : note.Length > 500 ? note[..500] : note;
    }

    public void Publish(DateTimeOffset now)
    {
        if (IsPublished)
        {
            throw new DomainException("prompt.version_immutable", "This version is already published.");
        }

        if (string.IsNullOrWhiteSpace(ContentText))
        {
            throw new DomainException("prompt.content_empty", "A prompt needs some text before it can be published.");
        }

        IsPublished = true;
        PublishedAt = now;
    }
}

/// <summary>Append-only record of what happened to prompts: usage (Phase 3) and administrative actions (publication, visibility, deletion, restoration, category changes).</summary>
public sealed class PromptEvent : Entity, ITenantOwned
{
    private PromptEvent() { }

    public Guid TenantId { get; private set; }

    public string Type { get; private set; } = string.Empty;

    public Guid? PromptId { get; private set; }

    public Guid? VersionId { get; private set; }

    public Guid UserId { get; private set; }

    public string? Context { get; private set; }

    public string? Mode { get; private set; }

    public string Status { get; private set; } = "ok";

    public int? DurationMs { get; private set; }

    public string? Model { get; private set; }

    public int? PromptTokens { get; private set; }

    public int? CompletionTokens { get; private set; }

    public string? ErrorClass { get; private set; }

    /// <summary>Short, non-sensitive details (never prompt text or context).</summary>
    public string? Details { get; private set; }

    public DateTimeOffset At { get; private set; }

    public static PromptEvent Create(string type, Guid? promptId, Guid? versionId, Guid userId, DateTimeOffset now, string? context = null, string? mode = null, string status = "ok", int? durationMs = null, string? model = null, int? promptTokens = null, int? completionTokens = null, string? errorClass = null, string? details = null) =>
        new() { Type = type, PromptId = promptId, VersionId = versionId, UserId = userId, At = now, Context = context, Mode = mode, Status = status, DurationMs = durationMs, Model = model, PromptTokens = promptTokens, CompletionTokens = completionTokens, ErrorClass = errorClass, Details = details is { Length: > 500 } ? details[..500] : details };
}

public static class PromptEventTypes
{
    public const string Created = "PromptCreated";
    public const string Updated = "PromptUpdated";
    public const string Published = "PromptPublished";
    public const string VisibilityChanged = "PromptVisibilityChanged";
    public const string Archived = "PromptArchived";
    public const string Restored = "PromptRestored";
    public const string Deleted = "PromptDeleted";
    public const string Moved = "PromptMoved";
    public const string CategoryCreated = "CategoryCreated";
    public const string CategoryEdited = "CategoryEdited";
    public const string CategoryMoved = "CategoryMoved";
    public const string CategoryDeleted = "CategoryDeleted";
}

/// <summary>Plain-text extraction of a Raavi editor document (paragraphs on their own lines, list items and code kept readable).</summary>
public static class PromptText
{
    public static string FromDocument(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new DomainException("prompt.content_invalid", "The prompt content is not a valid document.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new DomainException("prompt.content_invalid", "The prompt content is not a valid document.");
            }

            var node = root.TryGetProperty("doc", out var d) ? d : root;
            var sb = new StringBuilder();
            Walk(node, sb, 0);
            return sb.ToString().Trim();
        }
    }

    private static readonly HashSet<string> Blocks = ["paragraph", "heading", "listItem", "codeBlock", "blockquote", "tableRow", "bulletList", "orderedList", "taskItem"];

    private static void Walk(JsonElement node, StringBuilder sb, int depth)
    {
        if (depth > 64 || node.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var type = node.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type == "text" && node.TryGetProperty("text", out var text))
        {
            sb.Append(text.GetString());
            return;
        }

        if (type == "hardBreak")
        {
            sb.Append('\n');
            return;
        }

        if (type == "heading")
        {
            var level = node.TryGetProperty("attrs", out var a) && a.TryGetProperty("level", out var l) && l.TryGetInt32(out var n) ? Math.Clamp(n, 1, 6) : 1;
            sb.Append(new string('#', level)).Append(' ');
        }
        else if (type == "listItem")
        {
            sb.Append("- ");
        }

        if (node.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in content.EnumerateArray())
            {
                Walk(child, sb, depth + 1);
            }
        }

        if (type is not null && Blocks.Contains(type) && sb.Length > 0 && sb[^1] != '\n')
        {
            sb.Append('\n');
        }
    }
}
