namespace Raavi.Application.Abstractions.Ai;

/// <summary>A page the current user may edit (title/space only — never draft body).</summary>
public sealed record EditablePageRef(Guid ContentId, string Title, string SpaceKey);

/// <summary>
/// Edit-gated page metadata for editor AI actions (EKOS Phase 2). Returns null when the page is missing,
/// not a page, or the caller cannot edit it — same opaque null as <see cref="IPageTextReader"/> for View.
/// </summary>
public interface IPageEditGate
{
    Task<EditablePageRef?> TryGetEditableAsync(Guid contentId, CancellationToken cancellationToken);
}
