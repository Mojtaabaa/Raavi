using Raavi.Application.Abstractions.Content;

namespace Raavi.Wiki.Application;

/// <summary>Deterministic title/space heuristics (default EKOS-9 path).</summary>
public sealed class HeuristicLabelSuggestionProvider(LabelService labels) : ILabelSuggestionProvider, ILabelCatalog
{
    public async Task<IReadOnlyList<SuggestedLabel>> SuggestAsync(Guid pageId, CancellationToken cancellationToken)
    {
        var rows = await labels.SuggestForPageAsync(pageId, cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new SuggestedLabel(r.Slug, r.DisplayName, "heuristic")).ToList();
    }

    public async Task<IReadOnlyList<SuggestedLabel>> ListAssignableAsync(Guid pageId, int take, CancellationToken cancellationToken)
    {
        var rows = await labels.ListAssignableForSuggestionsAsync(pageId, Math.Clamp(take, 1, 80), cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new SuggestedLabel(r.Slug, r.DisplayName, "catalog")).ToList();
    }
}
