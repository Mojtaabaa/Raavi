namespace Raavi.Application.Abstractions.Ai;

/// <summary>A space as the Prompt Hub may show it to the current user: name, description and the titles of pages they may read.</summary>
public sealed record SpaceInfo(string Key, string Name, string? Description, IReadOnlyList<string> PageTitles, int PageCount);

/// <summary>Space metadata <b>for the current user</b> (implemented by the Wiki module); null when the space does not exist or may not be viewed.</summary>
public interface ISpaceInfoReader
{
    Task<SpaceInfo?> ReadAsync(string key, CancellationToken cancellationToken);
}

/// <summary>An issue of a connected tracker. Only what the connector reads: no description or comments.</summary>
public sealed record IssueContext(string Kind, string ConnectorName, string Reference, string Title, string Status, string Url, string Type);

public enum IssueContextOutcome
{
    Found = 1,
    NoConnector = 2,
    NotFound = 3,
    Unavailable = 4,
}

public sealed record IssueContextResult(IssueContextOutcome Outcome, IssueContext? Issue);

/// <summary>Looks an issue up through the workspace's enabled connector of the given kind (implemented by the Integrations module).</summary>
public interface IIssueContextReader
{
    Task<IssueContextResult> ReadAsync(string kind, string reference, CancellationToken cancellationToken);
}
