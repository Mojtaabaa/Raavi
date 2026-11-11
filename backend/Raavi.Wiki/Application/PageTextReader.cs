using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Ai;
using Raavi.Wiki.ContentTypes;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;

namespace Raavi.Wiki.Application;

/// <summary>Plain text of the current published version of a page, after the normal View check of <see cref="PageService.LoadAsync"/>.</summary>
public sealed class PageTextReader(PageService pages, WikiDbContext db) : IPageTextReader
{
    public async Task<PageText?> ReadAsync(Guid contentId, CancellationToken cancellationToken)
    {
        var loaded = await pages.LoadAsync(contentId, track: false, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess || loaded.Value.Node.ContentType != PageContentProvider.Type || loaded.Value.Node.Status != ContentStatus.Current)
        {
            return null;
        }

        var node = loaded.Value.Node;
        var json = await db.PageVersions.AsNoTracking().Where(v => v.Id == node.CurrentVersionId).Select(v => v.ContentJson).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var text = json is null ? string.Empty : RaaviDocument.Parse(json).PlainText;
        return new PageText(node.Id, node.Title, loaded.Value.Space.Space.Key, text, loaded.Value.Space.Space.Name, node.CreatedBy, node.UpdatedAt, loaded.Value.Space.Space.Id);
    }
}
