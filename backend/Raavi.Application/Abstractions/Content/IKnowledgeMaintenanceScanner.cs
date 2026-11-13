namespace Raavi.Application.Abstractions.Content;

/// <summary>
/// Knowledge maintenance suggestion (EKOS-8). Suggestions only — never auto-edit or publish pages.
/// </summary>
public sealed record KnowledgeMaintenanceSuggestion(
    string Kind,
    Guid ContentId,
    string Title,
    string SpaceKey,
    string SpaceName,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ReviewAt,
    Guid? RelatedContentId,
    string? RelatedTitle,
    double? Score,
    string Reason);

/// <summary>Scans viewable current pages for stale / near-duplicate maintenance candidates.</summary>
public interface IKnowledgeMaintenanceScanner
{
    Task<IReadOnlyList<KnowledgeMaintenanceSuggestion>> ScanAsync(int take, CancellationToken cancellationToken);
}
