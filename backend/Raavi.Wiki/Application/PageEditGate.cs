using Raavi.Application.Abstractions.Ai;
using Raavi.Wiki.ContentTypes;

namespace Raavi.Wiki.Application;

/// <summary>Edit check for AI editor actions: never returns draft text, only title/space when <c>CanEdit</c>.</summary>
public sealed class PageEditGate(PageService pages) : IPageEditGate
{
    public async Task<EditablePageRef?> TryGetEditableAsync(Guid contentId, CancellationToken cancellationToken)
    {
        var loaded = await pages.LoadAsync(contentId, track: false, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess || loaded.Value.Node.ContentType != PageContentProvider.Type || !loaded.Value.Rights.CanEdit)
        {
            return null;
        }

        var node = loaded.Value.Node;
        return new EditablePageRef(node.Id, node.Title, loaded.Value.Space.Space.Key);
    }
}
