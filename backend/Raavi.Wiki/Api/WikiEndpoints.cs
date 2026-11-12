using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Application.Common;
using Raavi.Infrastructure.Http;
using Raavi.Wiki.Application;

namespace Raavi.Wiki.Api;

/// <summary>Wiki core REST API (docs/api/WIKI_API.md). Tenant host only; every handler authorizes through the wiki authorizer.</summary>
public static class WikiEndpoints
{
    /// <summary>Server-rendered HTML names attachments by absolute path; a path-addressed workspace (ADR-045) needs its prefix in front.</summary>
    private static string WithPathBase(string html, HttpContext http) =>
        http.Request.PathBase.HasValue ? html.Replace("=\"/api/v1/attachments/", $"=\"{http.Request.PathBase}/api/v1/attachments/", StringComparison.Ordinal) : html;

    public static void MapWikiEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var g = api.MapGroup("").RequireHostKind(RaaviHostKind.Tenant).RequireAuthorization(RaaviPolicies.AuthenticatedUser);

        // ── Spaces ─────────────────────────────────────────────────────────────────────────────
        g.MapGet("/spaces", async (bool? includeArchived, string? scope, SpaceService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(includeArchived == true, scope == "administration", ct).ConfigureAwait(false)));

        g.MapPost("/spaces", async (CreateSpaceRequest body, SpaceService svc, HttpContext http, CancellationToken ct) =>
            (await svc.CreateAsync(body, ct).ConfigureAwait(false)).ToHttp(s =>
            {
                ETags.Set(http.Response, s.Revision);
                return Results.Created($"/api/v1/spaces/{s.Key}", s);
            }));

        g.MapGet("/spaces/{key}", async (string key, SpaceService svc, HttpContext http, CancellationToken ct) =>
            (await svc.GetAsync(key, ct).ConfigureAwait(false)).ToHttp(s => ETags.OkWithETag(http, s, s.Revision)));

        g.MapMethods("/spaces/{key}", ["PATCH", "PUT"], async (string key, UpdateSpaceRequest body, SpaceService svc, HttpContext http, CancellationToken ct) =>
            await Conditional(http, rev => svc.UpdateAsync(key, rev, body, ct), s => s.Revision).ConfigureAwait(false));

        g.MapPost("/spaces/{key}/archive", async (string key, SpaceService svc, HttpContext http, CancellationToken ct) =>
            await Conditional(http, rev => svc.SetArchivedAsync(key, rev, true, ct), s => s.Revision).ConfigureAwait(false));

        g.MapPost("/spaces/{key}/restore", async (string key, SpaceService svc, HttpContext http, CancellationToken ct) =>
            await Conditional(http, rev => svc.SetArchivedAsync(key, rev, false, ct), s => s.Revision).ConfigureAwait(false));

        g.MapGet("/spaces/{key}/permissions", async (string key, SpaceService svc, CancellationToken ct) =>
            (await svc.GetPermissionsAsync(key, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPut("/spaces/{key}/permissions", async (string key, ReplaceGrantsRequest body, SpaceService svc, CancellationToken ct) =>
            (await svc.ReplacePermissionsAsync(key, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapGet("/spaces/{key}/tree", async (string key, Guid? parentId, PageService svc, CancellationToken ct) =>
            (await svc.TreeAsync(key, parentId, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapGet("/spaces/{key}/recent", async (string key, int? limit, DiscoveryService svc, CancellationToken ct) =>
            (await svc.RecentInSpaceAsync(key, limit ?? 10, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapGet("/spaces/{key}/activity", async (string key, int? limit, DiscoveryService svc, CancellationToken ct) =>
            (await svc.ActivityAsync(key, limit ?? 30, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/spaces/{key}/content", async (string key, CreatePageRequest body, PageService svc, HttpContext http, CancellationToken ct) =>
            (await svc.CreateAsync(key, body, ct).ConfigureAwait(false)).ToHttp(p =>
            {
                ETags.Set(http.Response, p.Revision);
                return Results.Created($"/api/v1/content/{p.Id}", p);
            }));

        g.MapGet("/spaces/{key}/templates", async (string key, TemplateService svc, CancellationToken ct) =>
            (await svc.ListForSpaceAsync(key, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/spaces/{key}/templates", async (string key, TemplateRequest body, TemplateService svc, CancellationToken ct) =>
            (await svc.CreateAsync(key, body, ct).ConfigureAwait(false)).ToHttp(t => Results.Created($"/api/v1/templates/{t.Id}", t)));

        // ── Content ────────────────────────────────────────────────────────────────────────────
        g.MapGet("/content/{id:guid}", async (Guid id, PageService svc, HttpContext http, CancellationToken ct) =>
            (await svc.GetAsync(id, ct).ConfigureAwait(false)).ToHttp(p => ETags.OkWithETag(http, p, p.Revision)));

        g.MapPut("/content/{id:guid}/governance", async (Guid id, UpdateGovernanceRequest body, PageService svc, HttpContext http, CancellationToken ct) =>
            (await svc.UpdateGovernanceAsync(id, body, ct).ConfigureAwait(false)).ToHttp(e => ETags.OkWithETag(http, e, e.Revision)));

        g.MapGet("/content/review-due", async (Guid? ownerUserId, int? take, PageService svc, CancellationToken ct) =>
            Results.Ok(await svc.ReviewDueAsync(ownerUserId, take ?? 50, ct).ConfigureAwait(false)));

        g.MapGet("/content/{id:guid}/relationships", async (Guid id, KnowledgeRelationshipService svc, CancellationToken ct) =>
            (await svc.ListAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/content/{id:guid}/relationships", async (Guid id, CreateKnowledgeRelationshipRequest body, KnowledgeRelationshipService svc, CancellationToken ct) =>
            (await svc.CreateAsync(id, body, ct).ConfigureAwait(false)).ToHttp(r => Results.Created($"/api/v1/content/{id}/relationships/{r.Id}", r)));

        g.MapDelete("/content/{id:guid}/relationships/{relationshipId:guid}", async (Guid id, Guid relationshipId, KnowledgeRelationshipService svc, CancellationToken ct) =>
            (await svc.RemoveAsync(id, relationshipId, ct).ConfigureAwait(false)).ToHttp());

        g.MapPut("/content/{id:guid}", async (Guid id, UpdatePageRequest body, PageService svc, HttpContext http, CancellationToken ct) =>
            await Conditional(http, rev => svc.UpdateAsync(id, rev, body, ct), p => p.Revision).ConfigureAwait(false));

        g.MapPost("/content/{id:guid}/move", async (Guid id, MovePageRequest body, PageService svc, HttpContext http, CancellationToken ct) =>
            await Conditional(http, rev => svc.MoveAsync(id, rev, body, ct), p => p.Revision).ConfigureAwait(false));

        g.MapPost("/content/{id:guid}/archive", async (Guid id, PageService svc, HttpContext http, CancellationToken ct) =>
            await Conditional(http, rev => svc.SetArchivedAsync(id, rev, true, ct), p => p.Revision).ConfigureAwait(false));

        g.MapPost("/content/{id:guid}/unarchive", async (Guid id, PageService svc, HttpContext http, CancellationToken ct) =>
            await Conditional(http, rev => svc.SetArchivedAsync(id, rev, false, ct), p => p.Revision).ConfigureAwait(false));

        g.MapDelete("/content/{id:guid}", async (Guid id, PageService svc, HttpContext http, CancellationToken ct) =>
        {
            var rev = ETags.RequireIfMatch(http.Request);
            return rev.IsSuccess ? (await svc.TrashAsync(id, rev.Value, ct).ConfigureAwait(false)).ToHttp() : rev.Error!.ToProblem();
        });

        g.MapGet("/content/{id:guid}/versions", async (Guid id, PageService svc, CancellationToken ct) =>
            (await svc.VersionsAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapGet("/content/{id:guid}/restrictions", async (Guid id, PageService svc, CancellationToken ct) =>
            (await svc.GetRestrictionsAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPut("/content/{id:guid}/restrictions", async (Guid id, ReplaceRestrictionsRequest body, PageService svc, CancellationToken ct) =>
            (await svc.ReplaceRestrictionsAsync(id, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/content/{id:guid}/labels", async (Guid id, LabelRequest body, PageService svc, CancellationToken ct) =>
            (await svc.AddLabelAsync(id, body.Name, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapDelete("/content/{id:guid}/labels/{name}", async (Guid id, string name, PageService svc, CancellationToken ct) =>
            (await svc.RemoveLabelAsync(id, name, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapGet("/quick-find", async (string? q, int? limit, DiscoveryService svc, CancellationToken ct) =>
            Results.Ok(await svc.QuickFindAsync(q ?? string.Empty, limit ?? 10, ct).ConfigureAwait(false)));

        g.MapGet("/labels", async (string? prefix, DiscoveryService svc, CancellationToken ct) =>
            Results.Ok(await svc.SuggestLabelsAsync(prefix ?? string.Empty, ct).ConfigureAwait(false)));

        g.MapGet("/labels/search", async (string? tags, string? tagMode, bool? includeDescendants, string? spaceKey, int? skip, int? take, LabelService svc, CancellationToken ct) =>
            (await svc.SearchPagesAsync(new LabelSearchRequest(tags, tagMode, includeDescendants ?? false, spaceKey, skip ?? 0, take ?? 50), ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapGet("/labels/{name}", async (string name, LabelService svc, CancellationToken ct) =>
            (await svc.GetBySlugAsync(name, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapGet("/labels/{name}/content", async (string name, string? tagMode, bool? includeDescendants, string? spaceKey, int? skip, int? take, LabelService labels, DiscoveryService discovery, CancellationToken ct) =>
        {
            if (tagMode is null && includeDescendants is not true && spaceKey is null && skip is null)
            {
                return Results.Ok(await discovery.ContentWithLabelAsync(name, ct).ConfigureAwait(false));
            }

            return (await labels.SearchPagesAsync(new LabelSearchRequest(name, tagMode, includeDescendants ?? false, spaceKey, skip ?? 0, take ?? 50), ct).ConfigureAwait(false)).ToHttp(Results.Ok);
        });

        g.MapGet("/content/{id:guid}/label-suggestions", async (Guid id, LabelService svc, CancellationToken ct) =>
            Results.Ok(await svc.SuggestForPageAsync(id, ct).ConfigureAwait(false)));

        g.MapGet("/admin/labels", async (string? q, bool? archived, Guid? parentId, string? sort, int? skip, int? take, LabelService svc, CancellationToken ct) =>
            (await svc.AdminListAsync(q, archived, parentId, sort, skip ?? 0, take ?? 50, ct).ConfigureAwait(false)).ToHttp(Results.Ok))
            .RequireAuthorization(RaaviPolicies.TenantAdministrator);

        g.MapPost("/admin/labels", async (LabelWriteRequest body, LabelService svc, CancellationToken ct) =>
            (await svc.CreateAsync(body, ct).ConfigureAwait(false)).ToHttp(Results.Ok))
            .RequireAuthorization(RaaviPolicies.TenantAdministrator);

        g.MapPut("/admin/labels/{id:guid}", async (Guid id, LabelWriteRequest body, LabelService svc, CancellationToken ct) =>
            (await svc.UpdateAsync(id, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok))
            .RequireAuthorization(RaaviPolicies.TenantAdministrator);

        g.MapPost("/admin/labels/{id:guid}/move", async (Guid id, LabelMoveRequest body, LabelService svc, CancellationToken ct) =>
            (await svc.MoveAsync(id, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok))
            .RequireAuthorization(RaaviPolicies.TenantAdministrator);

        g.MapPost("/admin/labels/{id:guid}/archive", async (Guid id, LabelService svc, CancellationToken ct) =>
            (await svc.ArchiveAsync(id, restore: false, ct).ConfigureAwait(false)).ToHttp(Results.Ok))
            .RequireAuthorization(RaaviPolicies.TenantAdministrator);

        g.MapPost("/admin/labels/{id:guid}/restore", async (Guid id, LabelService svc, CancellationToken ct) =>
            (await svc.ArchiveAsync(id, restore: true, ct).ConfigureAwait(false)).ToHttp(Results.Ok))
            .RequireAuthorization(RaaviPolicies.TenantAdministrator);

        g.MapDelete("/admin/labels/{id:guid}", async (Guid id, LabelService svc, CancellationToken ct) =>
            (await svc.DeleteAsync(id, ct).ConfigureAwait(false)).ToHttp())
            .RequireAuthorization(RaaviPolicies.TenantAdministrator);

        g.MapPost("/admin/labels/merge/preview", async (LabelMergeRequest body, LabelService svc, CancellationToken ct) =>
            (await svc.PreviewMergeAsync(body, ct).ConfigureAwait(false)).ToHttp(Results.Ok))
            .RequireAuthorization(RaaviPolicies.TenantAdministrator);

        g.MapPost("/admin/labels/merge", async (LabelMergeRequest body, LabelService svc, CancellationToken ct) =>
            (await svc.MergeAsync(body, ct).ConfigureAwait(false)).ToHttp(Results.Ok))
            .RequireAuthorization(RaaviPolicies.TenantAdministrator);

        g.MapGet("/admin/labels/analytics", async (LabelService svc, CancellationToken ct) =>
            (await svc.AnalyticsAsync(ct).ConfigureAwait(false)).ToHttp(Results.Ok))
            .RequireAuthorization(RaaviPolicies.TenantAdministrator);

        g.MapGet("/activity", async (int? limit, DiscoveryService svc, CancellationToken ct) =>
            (await svc.ActivityAsync(null, limit ?? 30, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapGet("/activity/stats", async (int? days, DiscoveryService svc, CancellationToken ct) =>
            Results.Ok(await svc.ActivityStatsAsync(days ?? 30, ct).ConfigureAwait(false)));

        // Workspace administrators: the complete, searchable activity log.
        g.MapGet("/tenant/activity-log", async (string? q, string? kind, string? space, DateTimeOffset? from, DateTimeOffset? to, Guid? actor, int? skip, int? take, DiscoveryService svc, CancellationToken ct) =>
            Results.Ok(await svc.ActivityLogAsync(q, kind, space, from, to, actor, skip ?? 0, take ?? 50, ct).ConfigureAwait(false)))
            .RequireAuthorization(RaaviPolicies.TenantAdministrator);

        // ── Editor & attachments (Phase 4) ─────────────────────────────────────────────────────
        g.MapGet("/editor/schema", () => Results.Ok(EditorService.Schema()));
        g.MapGet("/editor/link-rewrites", () => Results.Ok(Raavi.Wiki.Editor.LinkPathRewrites.All()
            .Select(r => new { prefix = r.Prefix, baseUrl = r.BaseUrl })));

        g.MapGet("/content/{id:guid}/html", async (Guid id, EditorService svc, HttpContext http, CancellationToken ct) =>
            (await svc.RenderAsync(id, ct).ConfigureAwait(false)).ToHttp(html => Results.Ok(new { html = WithPathBase(html, http) })));

        g.MapGet("/content/{id:guid}/attachments", async (Guid id, AttachmentService svc, CancellationToken ct) =>
            (await svc.ListAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        // Antiforgery is enforced by the Raavi middleware (X-XSRF-TOKEN header) for every unsafe request.
        g.MapPost("/content/{id:guid}/attachments", async (Guid id, IFormFile file, AttachmentService svc, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            return (await svc.UploadAsync(id, file.FileName, stream, file.Length, ct).ConfigureAwait(false)).ToHttp(a => Results.Created($"/api/v1/attachments/{a.Id}", a));
        }).DisableAntiforgery();

        g.MapGet("/attachments/{id:guid}", async (Guid id, AttachmentService svc, HttpContext http, CancellationToken ct) =>
        {
            var result = await svc.DownloadAsync(id, ct).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return result.Error!.ToProblem();
            }

            var file = result.Value;
            http.Response.Headers.CacheControl = "private, max-age=300";
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; sandbox";
            return file.Inline
                ? Results.File(file.Data, file.MediaType, entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue('"' + file.Sha256 + '"'))
                : Results.File(file.Data, file.MediaType == "application/pdf" ? file.MediaType : "application/octet-stream", fileDownloadName: file.FileName);
        });

        // ── Drafts, publishing, history, trash (Phase 5) ─────────────────────────────────────────
        g.MapGet("/content/{id:guid}/draft", async (Guid id, PublishingService svc, HttpContext http, CancellationToken ct) =>
            (await svc.GetDraftAsync(id, ct).ConfigureAwait(false)).ToHttp(d => ETags.OkWithETag(http, d, d.Revision)));

        g.MapPut("/content/{id:guid}/draft", async (Guid id, SaveDraftRequest body, PublishingService svc, HttpContext http, CancellationToken ct) =>
            await Conditional(http, rev => svc.SaveDraftAsync(id, rev, body, ct), d => d.Revision).ConfigureAwait(false));

        g.MapDelete("/content/{id:guid}/draft", async (Guid id, PublishingService svc, CancellationToken ct) =>
            (await svc.DiscardDraftAsync(id, ct).ConfigureAwait(false)).ToHttp());

        g.MapPost("/content/{id:guid}/publish", async (Guid id, PublishRequestBody body, PublishingService svc, HttpContext http, CancellationToken ct) =>
        {
            var rev = ETags.RequireIfMatch(http.Request);
            return rev.IsSuccess ? (await svc.PublishAsync(id, rev.Value, body, ct).ConfigureAwait(false)).ToHttp(r => ETags.OkWithETag(http, r, r.Page.Revision)) : rev.Error!.ToProblem();
        });

        g.MapDelete("/content/{id:guid}/schedule", async (Guid id, PublishingService svc, CancellationToken ct) =>
            (await svc.CancelScheduleAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapGet("/content/{id:guid}/versions/{number:int}", async (Guid id, int number, PublishingService svc, CancellationToken ct) =>
            (await svc.GetVersionAsync(id, number, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapGet("/content/{id:guid}/diff", async (Guid id, int from, string to, PublishingService svc, CancellationToken ct) =>
            (await svc.DiffAsync(id, from, to, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/content/{id:guid}/versions/{number:int}/restore", async (Guid id, int number, PublishingService svc, HttpContext http, CancellationToken ct) =>
        {
            var rev = ETags.RequireIfMatch(http.Request);
            return rev.IsSuccess ? (await svc.RestoreVersionAsync(id, number, rev.Value, ct).ConfigureAwait(false)).ToHttp(r => ETags.OkWithETag(http, r, r.Page.Revision)) : rev.Error!.ToProblem();
        });

        g.MapGet("/spaces/{key}/publish-requests", async (string key, bool? pending, PublishingService svc, CancellationToken ct) =>
            (await svc.ListRequestsAsync(key, pending ?? true, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/publish-requests/{id:guid}/approve", async (Guid id, DecisionRequest body, PublishingService svc, CancellationToken ct) =>
            (await svc.DecideAsync(id, true, body.Comment, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/publish-requests/{id:guid}/reject", async (Guid id, DecisionRequest body, PublishingService svc, CancellationToken ct) =>
            (await svc.DecideAsync(id, false, body.Comment, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPut("/spaces/{key}/publishing", async (string key, SpacePublishingRequest body, PublishingService svc, HttpContext http, CancellationToken ct) =>
            await Conditional(http, rev => svc.SetApprovalRequiredAsync(key, rev, body.RequireApproval, ct), s => s.Revision).ConfigureAwait(false));

        g.MapGet("/spaces/{key}/trash", async (string key, PublishingService svc, CancellationToken ct) =>
            (await svc.TrashAsync(key, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/content/{id:guid}/restore-from-trash", async (Guid id, PublishingService svc, CancellationToken ct) =>
            (await svc.RestoreFromTrashAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapDelete("/content/{id:guid}/purge", async (Guid id, PublishingService svc, CancellationToken ct) =>
            (await svc.PurgeAsync(id, ct).ConfigureAwait(false)).ToHttp());

        g.MapGet("/me/drafts", async (PublishingService svc, CancellationToken ct) => Results.Ok(await svc.MyDraftsAsync(ct).ConfigureAwait(false)));

        // ── Templates ──────────────────────────────────────────────────────────────────────────
        g.MapGet("/templates", async (TemplateService svc, CancellationToken ct) => Results.Ok(await svc.ListGlobalAsync(ct).ConfigureAwait(false)));

        g.MapPost("/templates", async (TemplateRequest body, TemplateService svc, CancellationToken ct) =>
            (await svc.CreateAsync(null, body, ct).ConfigureAwait(false)).ToHttp(t => Results.Created($"/api/v1/templates/{t.Id}", t)));

        g.MapGet("/templates/{id:guid}", async (Guid id, TemplateService svc, HttpContext http, CancellationToken ct) =>
            (await svc.GetAsync(id, ct).ConfigureAwait(false)).ToHttp(t => ETags.OkWithETag(http, t, t.Revision)));

        g.MapPut("/templates/{id:guid}", async (Guid id, TemplateRequest body, TemplateService svc, HttpContext http, CancellationToken ct) =>
            await Conditional(http, rev => svc.UpdateAsync(id, rev, body, ct), t => t.Revision).ConfigureAwait(false));

        g.MapDelete("/templates/{id:guid}", async (Guid id, TemplateService svc, CancellationToken ct) =>
            (await svc.DeleteAsync(id, ct).ConfigureAwait(false)).ToHttp());

        // ── Tenant-scope permissions (who may create spaces) ───────────────────────────────────
        var admin = api.MapGroup("/tenant").RequireHostKind(RaaviHostKind.Tenant).RequireAuthorization(RaaviPolicies.TenantAdministrator);
        admin.MapGet("/permissions", async (SpaceService svc, CancellationToken ct) => Results.Ok(await svc.GetTenantPermissionsAsync(ct).ConfigureAwait(false)));
        admin.MapPut("/permissions", async (ReplaceGrantsRequest body, SpaceService svc, CancellationToken ct) =>
            (await svc.ReplaceTenantPermissionsAsync(body, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        // Whether the caller may create spaces (drives the "Create space" button).
        g.MapGet("/spaces-capabilities", async (Raavi.Wiki.Security.WikiAuthorizer authz, CancellationToken ct) =>
            Results.Ok(new { canCreateSpaces = await authz.CanCreateSpacesAsync(ct).ConfigureAwait(false) }));
    }

    /// <summary>Runs an update that requires <c>If-Match</c> and returns the new representation with its ETag.</summary>
    private static async Task<IResult> Conditional<T>(HttpContext http, Func<int, Task<Result<T>>> operation, Func<T, int> revision)
    {
        var expected = ETags.RequireIfMatch(http.Request);
        if (!expected.IsSuccess)
        {
            return expected.Error!.ToProblem();
        }

        var result = await operation(expected.Value).ConfigureAwait(false);
        return result.ToHttp(v => ETags.OkWithETag(http, v, revision(v)));
    }
}
