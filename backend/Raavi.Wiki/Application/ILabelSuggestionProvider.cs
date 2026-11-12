namespace Raavi.Wiki.Application;

/// <summary>
/// Extension point for future AI-powered label suggestions. The default path uses
/// <see cref="LabelService.SuggestForPageAsync"/> (deterministic title/space heuristics).
/// Implementations must not call external providers from the browser; use tenant AI config via AiService.
/// </summary>
public interface ILabelSuggestionProvider
{
    Task<IReadOnlyList<LabelDto>> SuggestAsync(Guid pageId, CancellationToken cancellationToken);
}
