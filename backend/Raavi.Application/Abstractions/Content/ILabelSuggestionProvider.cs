namespace Raavi.Application.Abstractions.Content;

/// <summary>A label the caller may apply to a page (slug is the assignable name).</summary>
public sealed record SuggestedLabel(string Slug, string DisplayName, string Source);

/// <summary>
/// Page label suggestions (EKOS-9). Heuristic implementation lives in Wiki; AI augmentation is a separate endpoint
/// that may only propose slugs from <see cref="ILabelCatalog"/> (never invents labels).
/// </summary>
public interface ILabelSuggestionProvider
{
    Task<IReadOnlyList<SuggestedLabel>> SuggestAsync(Guid pageId, CancellationToken cancellationToken);
}

/// <summary>Assignable active labels for a page the caller can view (excludes already assigned).</summary>
public interface ILabelCatalog
{
    Task<IReadOnlyList<SuggestedLabel>> ListAssignableAsync(Guid pageId, int take, CancellationToken cancellationToken);
}
