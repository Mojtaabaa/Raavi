using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Infrastructure.Http;

namespace Raavi.AI.PromptHub;

public sealed record VisibilityRequest(PromptVisibility Visibility);

public sealed record StatusRequest(PromptStatus Status);

public sealed record PublishRequest(string? ChangeNote);

/// <summary>Prompt Hub API (ADR-052). All routes need a signed-in member of the workspace; the service decides what each member may do.</summary>
public static class PromptHubEndpoints
{
    public static void MapPromptHubEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var g = api.MapGroup(string.Empty).RequireHostKind(RaaviHostKind.Tenant).RequireAuthorization(RaaviPolicies.AuthenticatedUser);

        g.MapGet("/prompts", async (string? scope, string? q, Guid? categoryId, bool? includeSub, string? context, PromptStatus? status, string? language, string? tag, string? sort, int? skip, int? take, PromptHubService svc, CancellationToken ct) =>
            (await svc.ListAsync(new PromptQuery(scope ?? "public", q, categoryId, includeSub ?? true, context, status, language, tag, sort, skip ?? 0, take ?? 25), ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapGet("/prompts/{id:guid}", async (Guid id, PromptHubService svc, CancellationToken ct) => (await svc.GetAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPost("/prompts", async (PromptInput body, PromptHubService svc, CancellationToken ct) =>
            (await svc.CreateAsync(body, ct).ConfigureAwait(false)).ToHttp(p => Results.Created($"/api/v1/prompts/{p.Prompt.Id}", p)));
        g.MapPut("/prompts/{id:guid}", async (Guid id, PromptInput body, PromptHubService svc, CancellationToken ct) => (await svc.UpdateAsync(id, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPost("/prompts/{id:guid}/publish", async (Guid id, PublishRequest body, PromptHubService svc, CancellationToken ct) => (await svc.PublishAsync(id, body.ChangeNote, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPost("/prompts/{id:guid}/visibility", async (Guid id, VisibilityRequest body, PromptHubService svc, CancellationToken ct) => (await svc.SetVisibilityAsync(id, body.Visibility, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPost("/prompts/{id:guid}/status", async (Guid id, StatusRequest body, PromptHubService svc, CancellationToken ct) => (await svc.SetStatusAsync(id, body.Status, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPost("/prompts/{id:guid}/duplicate", async (Guid id, PromptHubService svc, CancellationToken ct) =>
            (await svc.DuplicateAsync(id, ct).ConfigureAwait(false)).ToHttp(p => Results.Created($"/api/v1/prompts/{p.Prompt.Id}", p)));
        g.MapDelete("/prompts/{id:guid}", async (Guid id, PromptHubService svc, CancellationToken ct) => (await svc.DeleteAsync(id, ct).ConfigureAwait(false)).ToHttp());
        g.MapPost("/prompts/move", async (MovePromptsRequest body, PromptHubService svc, CancellationToken ct) =>
            (await svc.MoveManyAsync(body, ct).ConfigureAwait(false)).ToHttp(n => Results.Ok(new { moved = n })));

        // context, rendering and running
        g.MapGet("/prompt-variables", (PromptRunService svc) => Results.Ok(new { variables = svc.Catalog, kinds = Prompt.Contexts }));
        g.MapPost("/prompts/{id:guid}/render", async (Guid id, RenderRequest body, PromptRunService svc, CancellationToken ct) => (await svc.RenderAsync(id, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPost("/prompts/{id:guid}/copied", async (Guid id, UsageEventRequest body, PromptRunService svc, CancellationToken ct) => (await svc.RecordCopyAsync(id, body, ct).ConfigureAwait(false)).ToHttp());
        g.MapPost("/prompts/{id:guid}/manual-use", async (Guid id, UsageEventRequest body, PromptRunService svc, CancellationToken ct) => (await svc.ConfirmManualUseAsync(id, body, ct).ConfigureAwait(false)).ToHttp());
        g.MapPost("/prompts/{id:guid}/execute", async (Guid id, ExecuteRequest body, PromptRunService svc, CancellationToken ct) => (await svc.ExecuteAsync(id, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapGet("/prompt-executions", async (Guid? promptId, int? skip, int? take, PromptRunService svc, CancellationToken ct) => Results.Ok(await svc.HistoryAsync(promptId, skip ?? 0, take ?? 25, ct).ConfigureAwait(false)));
        g.MapGet("/prompt-executions/{id:guid}", async (Guid id, PromptRunService svc, CancellationToken ct) => (await svc.GetExecutionAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapGet("/prompts/{id:guid}/stats", async (Guid id, DateTimeOffset? from, DateTimeOffset? to, PromptRunService svc, CancellationToken ct) => (await svc.StatsAsync(id, from, to, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        // ratings, forks, versions, analytics
        g.MapGet("/prompts/{id:guid}/ratings", async (Guid id, PromptCommunityService svc, CancellationToken ct) => (await svc.RatingsAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPut("/prompts/{id:guid}/rating", async (Guid id, RateRequest body, PromptCommunityService svc, CancellationToken ct) => (await svc.RateAsync(id, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapDelete("/prompts/{id:guid}/rating", async (Guid id, PromptCommunityService svc, CancellationToken ct) => (await svc.RemoveRatingAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPost("/prompts/{id:guid}/fork", async (Guid id, PromptCommunityService svc, CancellationToken ct) =>
            (await svc.ForkAsync(id, ct).ConfigureAwait(false)).ToHttp(p => Results.Created($"/api/v1/prompts/{p.Prompt.Id}", p)));
        g.MapGet("/prompts/{id:guid}/versions", async (Guid id, PromptCommunityService svc, CancellationToken ct) => (await svc.VersionsAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapGet("/prompts/{id:guid}/versions/{versionId:guid}", async (Guid id, Guid versionId, PromptCommunityService svc, CancellationToken ct) => (await svc.GetVersionAsync(id, versionId, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapGet("/prompts/{id:guid}/diff", async (Guid id, Guid from, Guid to, PromptCommunityService svc, CancellationToken ct) => (await svc.DiffAsync(id, from, to, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPost("/prompts/{id:guid}/versions/{versionId:guid}/restore", async (Guid id, Guid versionId, RestoreRequest body, PromptCommunityService svc, CancellationToken ct) => (await svc.RestoreAsync(id, versionId, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapGet("/prompt-analytics", async (DateTimeOffset? from, DateTimeOffset? to, Guid? categoryId, Guid? userId, string? mode, PromptCommunityService svc, CancellationToken ct) =>
            (await svc.AnalyticsAsync(from, to, categoryId, userId, mode, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapGet("/prompt-categories", async (bool? includeArchived, PromptHubService svc, CancellationToken ct) => (await svc.CategoriesAsync(includeArchived ?? false, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPost("/prompt-categories", async (CategoryInput body, PromptHubService svc, CancellationToken ct) =>
            (await svc.CreateCategoryAsync(body, ct).ConfigureAwait(false)).ToHttp(c => Results.Created($"/api/v1/prompt-categories/{c.Id}", c)));
        g.MapPut("/prompt-categories/{id:guid}", async (Guid id, CategoryInput body, PromptHubService svc, CancellationToken ct) => (await svc.EditCategoryAsync(id, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPost("/prompt-categories/{id:guid}/move", async (Guid id, MoveCategoryRequest body, PromptHubService svc, CancellationToken ct) => (await svc.MoveCategoryAsync(id, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPost("/prompt-categories/{id:guid}/archive", async (Guid id, PromptHubService svc, CancellationToken ct) => (await svc.SetCategoryArchivedAsync(id, true, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPost("/prompt-categories/{id:guid}/restore", async (Guid id, PromptHubService svc, CancellationToken ct) => (await svc.SetCategoryArchivedAsync(id, false, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPost("/prompt-categories/{id:guid}/delete", async (Guid id, DeleteCategoryRequest body, PromptHubService svc, CancellationToken ct) => (await svc.DeleteCategoryAsync(id, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
    }
}
