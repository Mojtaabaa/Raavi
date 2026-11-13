namespace Raavi.Application.Abstractions.Content;

/// <summary>
/// Lets Integrations (and future connectors) project external links into the wiki knowledge graph without referencing the Wiki module.
/// Edges use Origin=Integration and are not user-deletable from the page panel.
/// </summary>
public interface IKnowledgeRelationshipProjection
{
    /// <summary>Creates or refreshes an Integration edge from a page to an external key (e.g. <c>jira:{connectorId}/{issueId}</c>).</summary>
    Task UpsertExternalAsync(
        Guid sourceContentId,
        string relationshipType,
        string targetExternalKey,
        string? title,
        string sourceSystem,
        Guid actorId,
        CancellationToken cancellationToken);

    /// <summary>Removes Integration edges for the external key (does not delete Content).</summary>
    Task RemoveExternalAsync(
        Guid sourceContentId,
        string targetExternalKey,
        string sourceSystem,
        CancellationToken cancellationToken);
}
