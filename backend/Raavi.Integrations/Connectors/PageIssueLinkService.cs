using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Ai;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Application.Abstractions.Content;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Jobs;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Domain.Common;
using Raavi.Infrastructure.Messaging;
using Raavi.Integrations.Persistence;

namespace Raavi.Integrations.Connectors;

public sealed record TextQuoteDto(string? Exact, string? Prefix, string? Suffix, string? AnchorJson);

public sealed record PageIssueLinkContextDto(Guid Id, string Exact, string? Prefix, string? Suffix, DateTimeOffset CreatedAt);

public sealed record PageIssueLinkDto(
    Guid Id, Guid ContentId, Guid ConnectorId, string ConnectorName, string IssueId, string IssueKey, string? Summary, string? Status, string? IssueType,
    string? Priority, string? Assignee, string? ProjectKey, string? IssueUrl, string Source, string SyncState, DateTimeOffset CreatedAt, string? CreatedByName,
    DateTimeOffset? LastSyncedAt, string? LastSyncError, IReadOnlyList<PageIssueLinkContextDto> Contexts);

public sealed record CreateJiraIssueRequest(
    Guid ConnectorId, string ProjectKey, string IssueTypeId, string? Summary, string? Description, bool? AppendSourceTemplate,
    Dictionary<string, JsonElement>? ExtraFields, TextQuoteDto? Quote);

public sealed record LinkJiraIssueRequest(Guid ConnectorId, string IssueKeyOrId, TextQuoteDto? Quote);

public sealed record RelatedPageDto(Guid ContentId, Guid LinkId, string? Title, string? SpaceKey, string? PageUrl, string Source, DateTimeOffset LinkedAt);

/// <summary>Page↔Jira relationships: Raavi owns the link; Jira owns issue fields. Authz via <see cref="IPermissionEvaluator"/>.</summary>
public sealed class PageIssueLinkService(
    IntegrationsDbContext db,
    JiraIssueClient jira,
    IPermissionEvaluator authz,
    IPageTextReader pages,
    IKnowledgeRelationshipProjection relationships,
    ITenantUrlResolver urls,
    IPrincipalDirectory directory,
    ITenantContext tenant,
    ICurrentUser current,
    IClock clock,
    ITenantJobScheduler jobs)
{
    public const string RelationshipSourceSystem = "jira";

    public static string ExternalKey(Guid connectorId, string issueId) => $"jira:{connectorId:N}/{issueId}";
    public static readonly Error NotFound = new("jira_link.not_found", "Link not found.", ErrorKind.NotFound);
    public static readonly Error PageNotFound = new("jira_link.page_not_found", "Page not found.", ErrorKind.NotFound);
    public static readonly Error Forbidden = new("jira_link.forbidden", "You are not allowed to change links on this page.", ErrorKind.Forbidden);
    public static readonly Error Duplicate = new("jira_link.duplicate", "This page is already linked to that Jira issue.", ErrorKind.Conflict);
    public static readonly Error NotJira = new("jira_link.not_jira", "Choose an enabled Jira connector.", ErrorKind.Validation);

    public async Task<Result<IReadOnlyList<PageIssueLinkDto>>> ListForPageAsync(Guid contentId, CancellationToken ct)
    {
        if (!await CanAsync(contentId, PermissionAction.View, ct).ConfigureAwait(false))
        {
            return Result<IReadOnlyList<PageIssueLinkDto>>.Failure(PageNotFound);
        }

        var rows = await db.PageIssueLinks.AsNoTracking()
            .Where(l => l.ContentId == contentId && l.StatusFlag == PageIssueLinkStatus.Active)
            .OrderByDescending(l => l.CreatedAt)
            .Take(200)
            .ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<PageIssueLinkDto>>.Success(await ToDtosAsync(rows, ct).ConfigureAwait(false));
    }

    public async Task<Result<PageIssueLinkDto>> CreateIssueAsync(Guid contentId, CreateJiraIssueRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var page = await RequirePageAsync(contentId, PermissionAction.Edit, ct).ConfigureAwait(false);
        if (!page.IsSuccess)
        {
            return Result<PageIssueLinkDto>.Failure(page.Error!);
        }

        var connector = await LoadJiraAsync(request.ConnectorId, ct).ConfigureAwait(false);
        if (!connector.IsSuccess)
        {
            return Result<PageIssueLinkDto>.Failure(connector.Error!);
        }

        var summary = string.IsNullOrWhiteSpace(request.Summary) ? request.Quote?.Exact : request.Summary;
        if (string.IsNullOrWhiteSpace(summary))
        {
            return Result<PageIssueLinkDto>.Failure(new Error("jira.summary_required", "A summary is required.", ErrorKind.Validation));
        }

        var description = BuildDescription(request.Description, request.AppendSourceTemplate != false, page.Value, request.Quote);
        using var fieldsDoc = BuildCreateFields(request.ProjectKey, request.IssueTypeId, summary.Trim(), description, request.ExtraFields);
        var fields = fieldsDoc.RootElement.GetProperty("fields").Clone();
        var created = await jira.CreateIssueAsync(connector.Value, fields, ct).ConfigureAwait(false);
        if (!created.IsSuccess)
        {
            return Result<PageIssueLinkDto>.Failure(created.Error!);
        }

        return await PersistLinkAsync(
            page.Value, connector.Value, created.Value.Id, created.Value.Key, created.Value.Url,
            PageIssueLinkSource.CreatedFromRaavi, summary.Trim(), status: "Open", issueType: null, request.Quote, ct).ConfigureAwait(false);
    }

    public async Task<Result<PageIssueLinkDto>> LinkExistingAsync(Guid contentId, LinkJiraIssueRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var page = await RequirePageAsync(contentId, PermissionAction.Edit, ct).ConfigureAwait(false);
        if (!page.IsSuccess)
        {
            return Result<PageIssueLinkDto>.Failure(page.Error!);
        }

        var connector = await LoadJiraAsync(request.ConnectorId, ct).ConfigureAwait(false);
        if (!connector.IsSuccess)
        {
            return Result<PageIssueLinkDto>.Failure(connector.Error!);
        }

        var detail = await jira.GetIssueAsync(connector.Value, request.IssueKeyOrId, ct).ConfigureAwait(false);
        if (!detail.IsSuccess)
        {
            return Result<PageIssueLinkDto>.Failure(detail.Error!);
        }

        var existing = await db.PageIssueLinks.FirstOrDefaultAsync(
            l => l.ContentId == contentId && l.ConnectorId == connector.Value.Id && l.IssueId == detail.Value.Id && l.StatusFlag == PageIssueLinkStatus.Active, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (request.Quote?.Exact is { Length: > 0 })
            {
                try
                {
                    db.PageIssueLinkContexts.Add(PageIssueLinkContext.Create(existing.Id, request.Quote.Exact, request.Quote.Prefix, request.Quote.Suffix, request.Quote.AnchorJson, current.UserId, clock.UtcNow));
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }
                catch (DomainException ex)
                {
                    return Result<PageIssueLinkDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Validation));
                }
            }

            return Result<PageIssueLinkDto>.Success((await ToDtosAsync([existing], ct).ConfigureAwait(false))[0]);
        }

        return await PersistLinkAsync(
            page.Value, connector.Value, detail.Value.Id, detail.Value.Key, detail.Value.Url,
            PageIssueLinkSource.LinkedExistingIssue, detail.Value.Summary, detail.Value.Status, detail.Value.IssueType, request.Quote, ct,
            detail.Value.Priority, detail.Value.Assignee, detail.Value.ProjectKey).ConfigureAwait(false);
    }

    public async Task<Result> UnlinkAsync(Guid contentId, Guid linkId, CancellationToken ct)
    {
        if (!await CanAsync(contentId, PermissionAction.Edit, ct).ConfigureAwait(false))
        {
            return Result.Failure(PageNotFound);
        }

        var link = await db.PageIssueLinks.FirstOrDefaultAsync(l => l.Id == linkId && l.ContentId == contentId && l.StatusFlag == PageIssueLinkStatus.Active, ct).ConfigureAwait(false);
        if (link is null)
        {
            return Result.Failure(NotFound);
        }

        link.Unlink(current.UserId, clock.UtcNow);
        var pageText = await pages.ReadAsync(contentId, ct).ConfigureAwait(false);
        PublishEvent("jira.page_issue_unlinked", link, pageText);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await relationships.RemoveExternalAsync(contentId, ExternalKey(link.ConnectorId, link.IssueId), RelationshipSourceSystem, ct).ConfigureAwait(false);

        if (link.RemoteLinkId is not null)
        {
            var connector = await db.IssueConnectors.AsNoTracking().FirstOrDefaultAsync(c => c.Id == link.ConnectorId, ct).ConfigureAwait(false);
            if (connector is not null)
            {
                await jira.DeleteRemoteLinkAsync(connector, link.IssueId, link.RemoteLinkId, ct).ConfigureAwait(false);
            }
        }

        return Result.Success();
    }

    public async Task<Result<PageIssueLinkDto>> RefreshAsync(Guid contentId, Guid linkId, CancellationToken ct)
    {
        if (!await CanAsync(contentId, PermissionAction.View, ct).ConfigureAwait(false))
        {
            return Result<PageIssueLinkDto>.Failure(PageNotFound);
        }

        var link = await db.PageIssueLinks.FirstOrDefaultAsync(l => l.Id == linkId && l.ContentId == contentId && l.StatusFlag == PageIssueLinkStatus.Active, ct).ConfigureAwait(false);
        if (link is null)
        {
            return Result<PageIssueLinkDto>.Failure(NotFound);
        }

        var connector = await db.IssueConnectors.FirstOrDefaultAsync(c => c.Id == link.ConnectorId, ct).ConfigureAwait(false);
        if (connector is null)
        {
            return Result<PageIssueLinkDto>.Failure(IssueConnectorService.NotFound);
        }

        var detail = await jira.GetIssueAsync(connector, link.IssueId, ct).ConfigureAwait(false);
        if (!detail.IsSuccess)
        {
            if (detail.Error!.Code == "issue.not_found")
            {
                link.MarkUnavailable(detail.Error.Message, clock.UtcNow);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            return Result<PageIssueLinkDto>.Failure(detail.Error);
        }

        link.ApplyIssue(detail.Value.Id, detail.Value.Key, detail.Value.Summary, detail.Value.Status, detail.Value.IssueType, detail.Value.Priority, detail.Value.Assignee, detail.Value.ProjectKey, detail.Value.Url, clock.UtcNow);
        PublishEvent("jira.page_issue_metadata_updated", link, null);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<PageIssueLinkDto>.Success((await ToDtosAsync([link], ct).ConfigureAwait(false))[0]);
    }

    public async Task<Result<IReadOnlyList<RelatedPageDto>>> PagesForIssueAsync(Guid connectorId, string issueId, CancellationToken ct)
    {
        var links = await db.PageIssueLinks.AsNoTracking()
            .Where(l => l.ConnectorId == connectorId && l.IssueId == issueId && l.StatusFlag == PageIssueLinkStatus.Active)
            .OrderByDescending(l => l.CreatedAt)
            .Take(200)
            .ToListAsync(ct).ConfigureAwait(false);
        var result = new List<RelatedPageDto>();
        var baseUrl = await urls.GetCanonicalUrlAsync(tenant.Tenant.TenantId, ct).ConfigureAwait(false);
        foreach (var link in links)
        {
            if (!await CanAsync(link.ContentId, PermissionAction.View, ct).ConfigureAwait(false))
            {
                continue;
            }

            var page = await pages.ReadAsync(link.ContentId, ct).ConfigureAwait(false);
            var pageUrl = page is null || baseUrl is null ? null : $"{baseUrl.ToString().TrimEnd('/')}/spaces/{page.SpaceKey}/pages/{link.ContentId:D}";
            result.Add(new RelatedPageDto(link.ContentId, link.Id, page?.Title, page?.SpaceKey, pageUrl, link.Source.ToString(), link.CreatedAt));
        }

        return Result<IReadOnlyList<RelatedPageDto>>.Success(result);
    }

    /// <summary>Hangfire / manual: ensure Remote Link exists for a pending link.</summary>
    public async Task SyncRemoteLinkAsync(Guid linkId, CancellationToken ct)
    {
        var link = await db.PageIssueLinks.FirstOrDefaultAsync(l => l.Id == linkId && l.StatusFlag == PageIssueLinkStatus.Active, ct).ConfigureAwait(false);
        if (link is null || link.SyncState is PageIssueSyncState.Synced)
        {
            return;
        }

        var connector = await db.IssueConnectors.FirstOrDefaultAsync(c => c.Id == link.ConnectorId, ct).ConfigureAwait(false);
        var page = await pages.ReadAsync(link.ContentId, ct).ConfigureAwait(false);
        if (connector is null || page is null)
        {
            link.MarkSyncFailure("page_or_connector_missing", clock.UtcNow);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return;
        }

        var baseUrl = await urls.GetCanonicalUrlAsync(tenant.Tenant.TenantId, ct).ConfigureAwait(false);
        if (baseUrl is null)
        {
            link.MarkSyncFailure("canonical_url_missing", clock.UtcNow);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return;
        }

        var pageUrl = $"{baseUrl.ToString().TrimEnd('/')}/spaces/{page.SpaceKey}/pages/{link.ContentId:D}";
        var remote = await jira.UpsertRemoteLinkAsync(connector, link.IssueId, link.RemoteLinkGlobalId, page.Title, pageUrl, ct).ConfigureAwait(false);
        if (remote.IsSuccess)
        {
            link.MarkRemoteLink(remote.Value.Id, clock.UtcNow);
        }
        else
        {
            link.MarkSyncFailure(remote.Error!.Code, clock.UtcNow);
            PublishEvent("jira.relationship_sync_failed", link, page);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task<Result<PageIssueLinkDto>> PersistLinkAsync(
        PageText page,
        IssueConnector connector,
        string issueId,
        string issueKey,
        string issueUrl,
        PageIssueLinkSource source,
        string? summary,
        string? status,
        string? issueType,
        TextQuoteDto? quote,
        CancellationToken ct,
        string? priority = null,
        string? assignee = null,
        string? projectKey = null)
    {
        var globalId = $"raavi:page:{tenant.Tenant.TenantId:N}:{page.ContentId:N}";
        try
        {
            var link = PageIssueLink.Create(
                page.ContentId, page.SpaceId ?? Guid.Empty, connector.Id, issueId, issueKey, source, globalId, current.UserId, clock.UtcNow,
                summary, status, issueType, priority, assignee, projectKey, issueUrl);
            db.PageIssueLinks.Add(link);
            if (quote?.Exact is { Length: > 0 })
            {
                db.PageIssueLinkContexts.Add(PageIssueLinkContext.Create(link.Id, quote.Exact, quote.Prefix, quote.Suffix, quote.AnchorJson, current.UserId, clock.UtcNow));
            }

            // Fill SpaceId from a lightweight authorize path: re-read is expensive; store empty then update from page if we add SpaceId to PageText
            PublishEvent("jira.page_issue_linked", link, page);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            var title = string.IsNullOrWhiteSpace(summary) ? issueKey : $"{issueKey}: {summary}";
            await relationships.UpsertExternalAsync(
                page.ContentId, "RelatedTo", ExternalKey(connector.Id, issueId), title, RelationshipSourceSystem, current.UserId, ct).ConfigureAwait(false);
            jobs.Enqueue<PageIssueRemoteLinkJob, PageIssueRemoteLinkArgs>(new PageIssueRemoteLinkArgs(link.Id));
            await SyncRemoteLinkAsync(link.Id, ct).ConfigureAwait(false);
            var fresh = await db.PageIssueLinks.AsNoTracking().FirstAsync(l => l.Id == link.Id, ct).ConfigureAwait(false);
            return Result<PageIssueLinkDto>.Success((await ToDtosAsync([fresh], ct).ConfigureAwait(false))[0]);
        }
        catch (DomainException ex)
        {
            return Result<PageIssueLinkDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Validation));
        }
        catch (DbUpdateException)
        {
            return Result<PageIssueLinkDto>.Failure(Duplicate);
        }
    }

    private void PublishEvent(string type, PageIssueLink link, PageText? page)
    {
        var payload = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["tenantId"] = tenant.Tenant.TenantId.ToString("D"),
            ["connectorId"] = link.ConnectorId.ToString("D"),
            ["issueId"] = link.IssueId,
            ["issueKey"] = link.IssueKey,
            ["contentId"] = link.ContentId.ToString("D"),
            ["spaceId"] = link.SpaceId == Guid.Empty ? null : link.SpaceId.ToString("D"),
            ["pageTitle"] = page?.Title,
            ["source"] = link.Source.ToString(),
            ["actorId"] = current.IsAuthenticated ? current.UserId.ToString("D") : null,
            ["syncState"] = link.SyncState.ToString(),
        };
        db.Outbox.Add(OutboxMessage.Create(tenant.Tenant.TenantId, type, payload, clock.UtcNow));
    }

    private async Task<Result<IssueConnector>> LoadJiraAsync(Guid connectorId, CancellationToken ct)
    {
        var c = await db.IssueConnectors.FirstOrDefaultAsync(x => x.Id == connectorId, ct).ConfigureAwait(false);
        if (c is null)
        {
            return Result<IssueConnector>.Failure(IssueConnectorService.NotFound);
        }

        if (c.Kind != IssueConnectorKind.Jira || !c.Enabled)
        {
            return Result<IssueConnector>.Failure(NotJira);
        }

        return Result<IssueConnector>.Success(c);
    }

    private async Task<bool> CanAsync(Guid contentId, PermissionAction action, CancellationToken ct)
    {
        var decision = await authz.AuthorizeAsync(new ResourceRef(PermissionScope.Content, ContentId: contentId, ContentType: "page"), action, ct).ConfigureAwait(false);
        return decision.IsAllowed;
    }

    private async Task<Result<PageText>> RequirePageAsync(Guid contentId, PermissionAction action, CancellationToken ct)
    {
        if (!await CanAsync(contentId, action, ct).ConfigureAwait(false))
        {
            return Result<PageText>.Failure(action == PermissionAction.Edit ? Forbidden : PageNotFound);
        }

        var page = await pages.ReadAsync(contentId, ct).ConfigureAwait(false);
        return page is null ? Result<PageText>.Failure(PageNotFound) : Result<PageText>.Success(page);
    }

    private async Task<IReadOnlyList<PageIssueLinkDto>> ToDtosAsync(List<PageIssueLink> links, CancellationToken ct)
    {
        if (links.Count == 0)
        {
            return [];
        }

        var ids = links.Select(l => l.Id).ToList();
        var contexts = await db.PageIssueLinkContexts.AsNoTracking().Where(c => ids.Contains(c.LinkId)).OrderBy(c => c.CreatedAt).ToListAsync(ct).ConfigureAwait(false);
        var connectorIds = links.Select(l => l.ConnectorId).Distinct().ToList();
        var connectorNames = await db.IssueConnectors.AsNoTracking().Where(c => connectorIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct).ConfigureAwait(false);
        var userIds = links.Select(l => l.CreatedBy).Distinct().ToList();
        var names = await directory.GetUserNamesAsync(userIds, ct).ConfigureAwait(false);
        return links.Select(l => new PageIssueLinkDto(
            l.Id, l.ContentId, l.ConnectorId, connectorNames.GetValueOrDefault(l.ConnectorId, string.Empty), l.IssueId, l.IssueKey, l.Summary, l.Status, l.IssueType,
            l.Priority, l.Assignee, l.ProjectKey, l.IssueUrl, l.Source.ToString(), l.SyncState.ToString(), l.CreatedAt, names.GetValueOrDefault(l.CreatedBy),
            l.LastSyncedAt, l.LastSyncError,
            contexts.Where(c => c.LinkId == l.Id).Select(c => new PageIssueLinkContextDto(c.Id, c.Exact, c.Prefix, c.Suffix, c.CreatedAt)).ToList())).ToList();
    }

    private static string BuildDescription(string? userDescription, bool appendTemplate, PageText page, TextQuoteDto? quote)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(userDescription))
        {
            sb.Append(userDescription.Trim());
        }

        if (!appendTemplate)
        {
            return sb.ToString();
        }

        const string marker = "----\nCreated from Raavi";
        if (sb.ToString().Contains(marker, StringComparison.Ordinal))
        {
            return sb.ToString();
        }

        if (sb.Length > 0)
        {
            sb.Append("\n\n");
        }

        sb.Append(marker).Append('\n');
        sb.Append("Page: ").Append(page.Title).Append('\n');
        sb.Append("Page ID: ").Append(page.ContentId.ToString("D")).Append('\n');
        sb.Append("Space: ").Append(page.SpaceKey).Append('\n');
        if (quote?.Exact is { Length: > 0 })
        {
            sb.Append("Selected text:\n").Append(quote.Exact.Trim()).Append('\n');
        }

        return sb.ToString();
    }

    private static JsonDocument BuildCreateFields(string projectKey, string issueTypeId, string summary, string description, Dictionary<string, JsonElement>? extra)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("fields");
            writer.WriteStartObject();
            writer.WritePropertyName("project");
            writer.WriteStartObject();
            writer.WriteString("key", projectKey);
            writer.WriteEndObject();
            writer.WritePropertyName("issuetype");
            writer.WriteStartObject();
            writer.WriteString("id", issueTypeId);
            writer.WriteEndObject();
            writer.WriteString("summary", summary.Length > 255 ? summary[..255] : summary);
            if (!string.IsNullOrWhiteSpace(description))
            {
                writer.WriteString("description", description);
            }

            if (extra is not null)
            {
                foreach (var (key, value) in extra)
                {
                    if (key is "project" or "issuetype" or "summary" or "description")
                    {
                        continue;
                    }

                    var allowed = key.StartsWith("customfield_", StringComparison.Ordinal)
                        || key is "priority" or "labels" or "components" or "assignee" or "duedate" or "fixVersions" or "versions" or "environment";
                    if (!allowed)
                    {
                        continue;
                    }

                    writer.WritePropertyName(key);
                    value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray());
    }
}

public sealed record PageIssueRemoteLinkArgs(Guid LinkId);

public sealed class PageIssueRemoteLinkJob(PageIssueLinkService links) : ITenantJob<PageIssueRemoteLinkArgs>
{
    public Task ExecuteAsync(PageIssueRemoteLinkArgs args, CancellationToken cancellationToken) =>
        links.SyncRemoteLinkAsync(args.LinkId, cancellationToken);
}
