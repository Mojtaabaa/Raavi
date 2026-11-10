namespace Raavi.Application.Abstractions.Ai;

/// <summary>A page as plain text, exactly what the current user may read.</summary>
public sealed record PageText(Guid ContentId, string Title, string SpaceKey, string Text, string? SpaceName = null, Guid? CreatedBy = null, DateTimeOffset? UpdatedAt = null);

/// <summary>
/// Gives the AI module the text of a page <b>for the current user</b> (implemented by the Wiki module). Returns null when the page
/// does not exist, is not a readable page, or the user may not view it — the AI module cannot tell these apart and never sees content
/// that did not pass the permission evaluator (SECURITY_ARCHITECTURE.md, AI context leakage).
/// </summary>
public interface IPageTextReader
{
    Task<PageText?> ReadAsync(Guid contentId, CancellationToken cancellationToken);
}
