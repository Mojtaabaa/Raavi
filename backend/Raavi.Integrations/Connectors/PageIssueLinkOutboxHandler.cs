using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Ai;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Application.Abstractions.Time;
using Raavi.Infrastructure.Messaging;
using Raavi.Integrations.Persistence;
using System.Text.Json;

namespace Raavi.Integrations.Connectors;

/// <summary>
/// Keeps Jira Remote Link titles/URLs aligned when a Raavi page is renamed or moved (wiki outbox events).
/// Soft-unlinked rows are ignored. Failures mark sync state without dropping the relationship.
/// </summary>
public sealed class PageIssueLinkOutboxHandler(
    IntegrationsDbContext db,
    JiraIssueClient jira,
    IPageTextReader pages,
    ITenantUrlResolver urls,
    ITenantContext tenant,
    IClock clock) : IOutboxHandler
{
    public bool CanHandle(string eventType) =>
        eventType is "content.updated" or "content.published" or "content.moved" or "content.trashed" or "content.archived";

    public async Task HandleAsync(OutboxEvent outboxEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outboxEvent);
        Guid? contentId = null;
        try
        {
            using var doc = JsonDocument.Parse(outboxEvent.PayloadJson);
            if (doc.RootElement.TryGetProperty("contentId", out var c) && Guid.TryParse(c.GetString(), out var id))
            {
                contentId = id;
            }
        }
        catch (JsonException)
        {
            return;
        }

        if (contentId is null)
        {
            return;
        }

        var links = await db.PageIssueLinks
            .Where(l => l.ContentId == contentId && l.StatusFlag == PageIssueLinkStatus.Active)
            .Take(100)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (links.Count == 0)
        {
            return;
        }

        if (outboxEvent.Type is "content.trashed" or "content.archived")
        {
            // Leave relationships; Remote Links may 404 later. Do not auto-delete Jira links.
            return;
        }

        var page = await pages.ReadAsync(contentId.Value, cancellationToken).ConfigureAwait(false);
        var baseUrl = await urls.GetCanonicalUrlAsync(tenant.Tenant.TenantId, cancellationToken).ConfigureAwait(false);
        if (page is null || baseUrl is null)
        {
            return;
        }

        var pageUrl = $"{baseUrl.ToString().TrimEnd('/')}/spaces/{page.SpaceKey}/pages/{contentId.Value:D}";
        foreach (var link in links)
        {
            var connector = await db.IssueConnectors.AsNoTracking().FirstOrDefaultAsync(c => c.Id == link.ConnectorId, cancellationToken).ConfigureAwait(false);
            if (connector is null)
            {
                continue;
            }

            var remote = await jira.UpsertRemoteLinkAsync(connector, link.IssueId, link.RemoteLinkGlobalId, page.Title, pageUrl, cancellationToken).ConfigureAwait(false);
            if (remote.IsSuccess)
            {
                link.MarkRemoteLink(remote.Value.Id, clock.UtcNow);
            }
            else
            {
                link.MarkSyncFailure(remote.Error!.Code, clock.UtcNow);
            }
        }

        db.Outbox.Add(OutboxMessage.Create(tenant.Tenant.TenantId, "jira.page_metadata_updated", new
        {
            schemaVersion = 1,
            contentId = contentId.Value.ToString("D"),
            pageTitle = page.Title,
            actorId = (string?)null,
        }, clock.UtcNow));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
