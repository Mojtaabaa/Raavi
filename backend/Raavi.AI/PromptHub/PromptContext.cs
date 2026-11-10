using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Raavi.Application.Abstractions.Ai;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Common;
using Raavi.Domain.Common;

namespace Raavi.AI.PromptHub;

/// <summary>What a prompt is about when it is used: the kind and the identifiers/text that kind needs. Providers check access to every identifier.</summary>
public sealed record PromptContextRequest(string? Kind, Guid? PageId, string? SelectedText, string? Section, IReadOnlyList<Guid>? PageIds, string? SpaceKey, string? Reference, string? Text);

public sealed record PromptSource(string Kind, string Title, string? Url, int Chars);

public sealed record ProvidedContext(Dictionary<string, string> Values, List<PromptSource> Sources);

public sealed record PromptVariableInfo(string Name, string Description, string Type, IReadOnlyList<string> Contexts);

/// <summary>A variable the prompt's author defines (<c>input.name</c>), filled in by the person who runs the prompt.</summary>
public sealed record PromptInputDefinition(string Name, string? Label, string Type, bool Required, string? Default, IReadOnlyList<string>? Options, string? Description);

/// <summary>One kind of execution context. Providers are registered in DI (<c>IPromptContextProvider</c>) and never bypass the readers that apply the current user's rights.</summary>
public interface IPromptContextProvider
{
    string Kind { get; }

    IReadOnlyList<PromptVariableInfo> Variables { get; }

    Task<Result<ProvidedContext>> ResolveAsync(PromptContextRequest request, CancellationToken ct);
}

public static class PromptContextErrors
{
    public static readonly Error NotFound = new("prompt.context_not_found", "The page, space or item for this context does not exist or you cannot access it.", ErrorKind.NotFound);
    public static readonly Error Unavailable = new("prompt.context_unavailable", "This kind of context is not available in this workspace.", ErrorKind.Conflict);
    public static readonly Error Invalid = new("prompt.context_invalid", "The context is incomplete or invalid.", ErrorKind.Validation);
    public static readonly Error Unsupported = new("prompt.context_unsupported", "This prompt cannot be used with this kind of context.", ErrorKind.Validation);
    public static readonly Error TooLarge = new("prompt.context_too_large", "The text to be sent is larger than the configured limit.", ErrorKind.Validation);
    public static readonly Error TruncationDisabled = new("prompt.truncation_disabled", "The text is too large and shortening is not enabled in this workspace.", ErrorKind.Validation);
}

public static class PromptCatalog
{
    public const string Page = "page";

    /// <summary>The built-in variables, for the editor's insertion menu and for validation. Anything else must be an <c>input.*</c> variable defined on the prompt.</summary>
    public static readonly IReadOnlyList<PromptVariableInfo> Common =
    [
        new("user.name", "Display name of the person running the prompt", "text", ["*"]),
        new("user.language", "Interface language of the person running the prompt (fa or en)", "text", ["*"]),
        new("now.date", "Today's date (UTC, yyyy-mm-dd)", "date", ["*"]),
        new("now.datetime", "Current date and time (UTC)", "text", ["*"]),
    ];

    /// <summary>Variables whose values are content written by other people: fenced as untrusted data when sent to a model, and shortened first when the text is too long.</summary>
    public static readonly IReadOnlySet<string> Heavy = new HashSet<string>(StringComparer.Ordinal) { "page.content", "section.content", "pages.content", "selected.text", "context.text", "space.pages" };

    public static bool IsInput(string name) => name.StartsWith("input.", StringComparison.Ordinal);
}

public sealed class PromptContextRegistry(IEnumerable<IPromptContextProvider> providers)
{
    private readonly Dictionary<string, IPromptContextProvider> _byKind = providers.ToDictionary(p => p.Kind, StringComparer.Ordinal);

    public IPromptContextProvider? Find(string kind) => _byKind.GetValueOrDefault(kind);

    public IReadOnlyList<string> Kinds => [.. _byKind.Keys];

    public IReadOnlyList<PromptVariableInfo> Catalog => [.. PromptCatalog.Common, .. _byKind.Values.SelectMany(p => p.Variables)];

    public bool IsKnown(string name) => PromptCatalog.IsInput(name) || Catalog.Any(v => v.Name == name);
}

public sealed class PageContextProvider(IPageTextReader reader, IPrincipalDirectory directory) : IPromptContextProvider
{
    public string Kind => "page";

    public IReadOnlyList<PromptVariableInfo> Variables { get; } =
    [
        new("page.title", "Title of the page", "text", ["page", "selection", "section"]),
        new("page.content", "Full text of the page", "text", ["page"]),
        new("page.url", "Address of the page inside Raavi", "text", ["page", "selection", "section"]),
        new("page.author", "Author of the page", "text", ["page", "selection", "section"]),
        new("page.updated", "Last update of the page (date)", "date", ["page", "selection", "section"]),
        new("space.name", "Name of the page's space", "text", ["page", "selection", "section", "space"]),
        new("space.key", "Key of the page's space", "text", ["page", "selection", "section", "space"]),
    ];

    public async Task<Result<ProvidedContext>> ResolveAsync(PromptContextRequest request, CancellationToken ct)
    {
        if (request.PageId is not { } id)
        {
            return Result<ProvidedContext>.Failure(PromptContextErrors.Invalid);
        }

        var page = await reader.ReadAsync(id, ct).ConfigureAwait(false);
        if (page is null)
        {
            return Result<ProvidedContext>.Failure(PromptContextErrors.NotFound);
        }

        var (values, sources) = await Base(page, directory, ct).ConfigureAwait(false);
        values["page.content"] = page.Text;
        return Result<ProvidedContext>.Success(new ProvidedContext(values, sources));
    }

    internal static async Task<(Dictionary<string, string>, List<PromptSource>)> Base(PageText page, IPrincipalDirectory directory, CancellationToken ct)
    {
        var author = page.CreatedBy is { } a ? (await directory.GetUserNamesAsync([a], ct).ConfigureAwait(false)).GetValueOrDefault(a) : null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["page.title"] = page.Title,
            ["page.url"] = $"/spaces/{page.SpaceKey}/pages/{page.ContentId}",
            ["page.author"] = author ?? string.Empty,
            ["page.updated"] = page.UpdatedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            ["space.name"] = page.SpaceName ?? page.SpaceKey,
            ["space.key"] = page.SpaceKey,
        };
        return (values, [new PromptSource("page", page.Title, values["page.url"], page.Text.Length)]);
    }
}

public sealed class SelectionContextProvider(IPageTextReader reader, IPrincipalDirectory directory) : IPromptContextProvider
{
    public const int MaxSelection = 50_000;

    public string Kind => "selection";

    public IReadOnlyList<PromptVariableInfo> Variables { get; } = [new("selected.text", "The text the person selected on the page", "text", ["selection"])];

    public async Task<Result<ProvidedContext>> ResolveAsync(PromptContextRequest request, CancellationToken ct)
    {
        var text = request.SelectedText?.Trim();
        if (request.PageId is not { } id || string.IsNullOrEmpty(text) || text.Length > MaxSelection)
        {
            return Result<ProvidedContext>.Failure(PromptContextErrors.Invalid);
        }

        var page = await reader.ReadAsync(id, ct).ConfigureAwait(false);
        if (page is null)
        {
            return Result<ProvidedContext>.Failure(PromptContextErrors.NotFound);
        }

        var (values, sources) = await PageContextProvider.Base(page, directory, ct).ConfigureAwait(false);
        values["selected.text"] = text;
        sources.Add(new PromptSource("selection", page.Title, values["page.url"], text.Length));
        return Result<ProvidedContext>.Success(new ProvidedContext(values, sources));
    }
}

public sealed class SectionContextProvider(IPageTextReader reader, IPrincipalDirectory directory) : IPromptContextProvider
{
    public string Kind => "section";

    public IReadOnlyList<PromptVariableInfo> Variables { get; } =
    [
        new("section.title", "Heading of the chosen section", "text", ["section"]),
        new("section.content", "Text of the chosen section (up to the next heading of the same or a higher level)", "text", ["section"]),
    ];

    public async Task<Result<ProvidedContext>> ResolveAsync(PromptContextRequest request, CancellationToken ct)
    {
        var heading = request.Section?.Trim();
        if (request.PageId is not { } id || string.IsNullOrEmpty(heading))
        {
            return Result<ProvidedContext>.Failure(PromptContextErrors.Invalid);
        }

        var page = await reader.ReadAsync(id, ct).ConfigureAwait(false);
        if (page is null)
        {
            return Result<ProvidedContext>.Failure(PromptContextErrors.NotFound);
        }

        var lines = page.Text.ReplaceLineEndings("\n").Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith('#') && l.TrimStart('#').Trim().Equals(heading, StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            return Result<ProvidedContext>.Failure(PromptContextErrors.NotFound);
        }

        var level = lines[start].TakeWhile(c => c == '#').Count();
        var end = lines.Length;
        for (var i = start + 1; i < lines.Length; i++)
        {
            if (lines[i].StartsWith('#') && lines[i].TakeWhile(c => c == '#').Count() <= level)
            {
                end = i;
                break;
            }
        }

        var body = string.Join('\n', lines[(start + 1)..end]).Trim();
        var (values, sources) = await PageContextProvider.Base(page, directory, ct).ConfigureAwait(false);
        values["section.title"] = heading;
        values["section.content"] = body;
        sources.Add(new PromptSource("section", heading, values["page.url"], body.Length));
        return Result<ProvidedContext>.Success(new ProvidedContext(values, sources));
    }
}

public sealed class PagesContextProvider(IPageTextReader reader) : IPromptContextProvider
{
    public const int MaxPages = 10;

    public string Kind => "pages";

    public IReadOnlyList<PromptVariableInfo> Variables { get; } =
    [
        new("pages.list", "Titles and addresses of the chosen pages", "text", ["pages"]),
        new("pages.content", "The text of all chosen pages, one after the other", "text", ["pages"]),
        new("pages.count", "Number of chosen pages", "number", ["pages"]),
    ];

    public async Task<Result<ProvidedContext>> ResolveAsync(PromptContextRequest request, CancellationToken ct)
    {
        var ids = (request.PageIds ?? []).Distinct().Take(MaxPages + 1).ToList();
        if (ids.Count is 0 or > MaxPages)
        {
            return Result<ProvidedContext>.Failure(PromptContextErrors.Invalid);
        }

        var list = new StringBuilder();
        var content = new StringBuilder();
        var sources = new List<PromptSource>();
        foreach (var id in ids)
        {
            var page = await reader.ReadAsync(id, ct).ConfigureAwait(false);
            if (page is null)
            {
                return Result<ProvidedContext>.Failure(PromptContextErrors.NotFound); // one inaccessible page fails the whole context: nothing is leaked by omission
            }

            var url = $"/spaces/{page.SpaceKey}/pages/{page.ContentId}";
            list.Append("- ").Append(page.Title).Append(" (").Append(url).Append(")\n");
            content.Append("## ").Append(page.Title).Append('\n').Append(page.Text).Append("\n\n");
            sources.Add(new PromptSource("page", page.Title, url, page.Text.Length));
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pages.list"] = list.ToString().Trim(),
            ["pages.content"] = content.ToString().Trim(),
            ["pages.count"] = ids.Count.ToString(CultureInfo.InvariantCulture),
        };
        return Result<ProvidedContext>.Success(new ProvidedContext(values, sources));
    }
}

public sealed class SpaceContextProvider(ISpaceInfoReader spaces) : IPromptContextProvider
{
    public string Kind => "space";

    public IReadOnlyList<PromptVariableInfo> Variables { get; } =
    [
        new("space.name", "Name of the space", "text", ["page", "selection", "section", "space"]),
        new("space.key", "Key of the space", "text", ["page", "selection", "section", "space"]),
        new("space.description", "Description of the space", "text", ["space"]),
        new("space.pages", "Titles of the pages of the space that you may view", "text", ["space"]),
    ];

    public async Task<Result<ProvidedContext>> ResolveAsync(PromptContextRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SpaceKey))
        {
            return Result<ProvidedContext>.Failure(PromptContextErrors.Invalid);
        }

        var space = await spaces.ReadAsync(request.SpaceKey.Trim(), ct).ConfigureAwait(false);
        if (space is null)
        {
            return Result<ProvidedContext>.Failure(PromptContextErrors.NotFound);
        }

        var pages = string.Join('\n', space.PageTitles.Select(t => "- " + t)) + (space.PageCount > space.PageTitles.Count ? $"\n(+{space.PageCount - space.PageTitles.Count} more)" : string.Empty);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["space.name"] = space.Name,
            ["space.key"] = space.Key,
            ["space.description"] = space.Description ?? string.Empty,
            ["space.pages"] = pages,
        };
        return Result<ProvidedContext>.Success(new ProvidedContext(values, [new PromptSource("space", space.Name, $"/spaces/{space.Key}", pages.Length)]));
    }
}

public sealed class JiraIssueContextProvider(IIssueContextReader issues) : IPromptContextProvider
{
    public string Kind => "jira-issue";

    public IReadOnlyList<PromptVariableInfo> Variables { get; } =
    [
        new("jira.issue", "The issue as one line: key, title, status and address", "text", ["jira-issue"]),
        new("jira.issue.key", "Issue key", "text", ["jira-issue"]),
        new("jira.issue.title", "Issue title", "text", ["jira-issue"]),
        new("jira.issue.status", "Issue status", "text", ["jira-issue"]),
        new("jira.issue.url", "Issue address", "text", ["jira-issue"]),
    ];

    public async Task<Result<ProvidedContext>> ResolveAsync(PromptContextRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reference))
        {
            return Result<ProvidedContext>.Failure(PromptContextErrors.Invalid);
        }

        var result = await issues.ReadAsync("Jira", request.Reference.Trim(), ct).ConfigureAwait(false);
        if (result.Outcome == IssueContextOutcome.NoConnector)
        {
            return Result<ProvidedContext>.Failure(PromptContextErrors.Unavailable);
        }

        if (result.Issue is not { } i)
        {
            return Result<ProvidedContext>.Failure(result.Outcome == IssueContextOutcome.NotFound ? PromptContextErrors.NotFound : PromptContextErrors.Unavailable);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["jira.issue"] = $"{i.Reference}: {i.Title} [{i.Status}] {i.Url}",
            ["jira.issue.key"] = i.Reference,
            ["jira.issue.title"] = i.Title,
            ["jira.issue.status"] = i.Status,
            ["jira.issue.url"] = i.Url,
        };
        return Result<ProvidedContext>.Success(new ProvidedContext(values, [new PromptSource("jira-issue", $"{i.Reference} {i.Title}", i.Url, values["jira.issue"].Length)]));
    }
}

/// <summary>Registered so the kind is visible, but the Jira connector reads single issues only (no project data): always reports "not available".</summary>
public sealed class JiraProjectContextProvider : IPromptContextProvider
{
    public string Kind => "jira-project";

    public IReadOnlyList<PromptVariableInfo> Variables { get; } = [new("jira.project", "Jira project (not available: the connector reads single issues only)", "text", ["jira-project"])];

    public Task<Result<ProvidedContext>> ResolveAsync(PromptContextRequest request, CancellationToken ct) => Task.FromResult(Result<ProvidedContext>.Failure(PromptContextErrors.Unavailable));
}

/// <summary>Raavi has no Templar integration yet; the provider is registered so prompts can declare the context and a future integration only has to replace this class.</summary>
public sealed class TemplarReportContextProvider : IPromptContextProvider
{
    public string Kind => "templar-report";

    public IReadOnlyList<PromptVariableInfo> Variables { get; } = [new("templar.report", "Templar report (not available: no Templar integration is installed)", "text", ["templar-report"])];

    public Task<Result<ProvidedContext>> ResolveAsync(PromptContextRequest request, CancellationToken ct) => Task.FromResult(Result<ProvidedContext>.Failure(PromptContextErrors.Unavailable));
}

public sealed class GenericContextProvider : IPromptContextProvider
{
    public string Kind => "generic";

    public IReadOnlyList<PromptVariableInfo> Variables { get; } = [new("context.text", "Text the person typed or pasted when running the prompt", "text", ["generic"])];

    public Task<Result<ProvidedContext>> ResolveAsync(PromptContextRequest request, CancellationToken ct)
    {
        var text = request.Text?.Trim() ?? string.Empty;
        if (text.Length > SelectionContextProvider.MaxSelection)
        {
            return Task.FromResult(Result<ProvidedContext>.Failure(PromptContextErrors.Invalid));
        }

        return Task.FromResult(Result<ProvidedContext>.Success(new ProvidedContext(new Dictionary<string, string>(StringComparer.Ordinal) { ["context.text"] = text }, text.Length == 0 ? [] : [new PromptSource("generic", "Text", null, text.Length)])));
    }
}

public static class PromptInputs
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static readonly string[] Types = ["text", "number", "boolean", "date", "select", "multiselect"];
    public const int MaxDefinitions = 30;

    public static string Serialize(IReadOnlyList<PromptInputDefinition>? defs) => JsonSerializer.Serialize(defs ?? [], Web);

    public static IReadOnlyList<PromptInputDefinition> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<PromptInputDefinition>>(json, Web) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Checks the author's definitions: unique <c>input.*</c> names, known types, options for select types.</summary>
    public static void Validate(IReadOnlyList<PromptInputDefinition> defs)
    {
        if (defs.Count > MaxDefinitions)
        {
            throw new DomainException("prompt.variables_invalid", $"At most {MaxDefinitions} input variables.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in defs)
        {
            if (!PromptTemplate.IsValidName(d.Name ?? string.Empty) || !PromptCatalog.IsInput(d.Name!) || !seen.Add(d.Name!) || !Types.Contains(d.Type))
            {
                throw new DomainException("prompt.variables_invalid", "Input variables are named input.something, once each, with a supported type.");
            }

            if (d.Type is "select" or "multiselect" && (d.Options is null || d.Options.Count is 0 or > 50 || d.Options.Any(o => string.IsNullOrWhiteSpace(o) || o.Length > 100)))
            {
                throw new DomainException("prompt.variables_invalid", "A choice variable needs 1–50 options.");
            }

            if ((d.Label?.Length ?? 0) > 100 || (d.Description?.Length ?? 0) > 300 || (d.Default?.Length ?? 0) > 2000)
            {
                throw new DomainException("prompt.variables_invalid", "A label, description or default value is too long.");
            }
        }
    }

    /// <summary>Converts what the person entered into the text that goes into the prompt; null = invalid for the type.</summary>
    public static string? Coerce(PromptInputDefinition d, JsonElement? raw)
    {
        if (raw is not { } v || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        switch (d.Type)
        {
            case "number":
                return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? PromptTemplate.Number(n)
                    : v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? PromptTemplate.Number(s) : null;
            case "boolean":
                return v.ValueKind switch { JsonValueKind.True => "true", JsonValueKind.False => "false", JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b ? "true" : "false", _ => null };
            case "date":
                return v.ValueKind == JsonValueKind.String && DateOnly.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
            case "select":
                return v.ValueKind == JsonValueKind.String && d.Options!.Contains(v.GetString()!) ? v.GetString() : v.ValueKind == JsonValueKind.String && v.GetString() is { Length: 0 } ? string.Empty : null;
            case "multiselect":
                if (v.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var items = v.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null).ToList();
                return items.All(i => i is not null && d.Options!.Contains(i)) ? string.Join(", ", items) : null;
            default:
                var text = v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
                return text is { Length: > 20_000 } ? null : text;
        }
    }
}
