using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Wiki.Application;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki.Collaboration;

/// <summary>
/// Real-time collaboration on whiteboards (Phase 11c, ADR-035). The Yjs structure of a board is
/// <c>elements: Y.Map&lt;id, Y.Map&lt;field, value&gt;&gt;</c> plus <c>order: Y.Array&lt;id&gt;</c>. The collaboration server converts it to the
/// element list and sends the <b>full</b> Yjs state with it; the API validates the list exactly like a REST save, stores both on the board row
/// and tells the server to reset when it refuses. Tickets, checks and persistence run inside the same tenant scopes as for pages.
/// </summary>
public sealed class BoardCollaborationService(
    WikiDbContext db,
    WikiAuthorizer authz,
    WikiActor actor,
    ISessionValidator sessions,
    TicketSigner signer,
    IConfiguration configuration,
    IHttpContextAccessor http,
    IClock clock)
{
    public static readonly Error NotFound = new("board.not_found", "Whiteboard not found.", ErrorKind.NotFound);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public bool IsEnabled
    {
        get
        {
            var o = CollaborationOptions.From(configuration);
            return o.Enabled && signer.IsAvailable && !string.IsNullOrWhiteSpace(o.PublicUrl) && !string.IsNullOrEmpty(o.ServiceKey);
        }
    }

    public async Task<Result<TicketDto>> IssueTicketAsync(Guid boardId, CancellationToken ct)
    {
        if (!IsEnabled)
        {
            return Result<TicketDto>.Failure(CollaborationService.Unavailable);
        }

        var board = await db.Whiteboards.AsNoTracking().FirstOrDefaultAsync(b => b.Id == boardId, ct).ConfigureAwait(false);
        var rights = board is null ? null : await RightsAsync(board, ct).ConfigureAwait(false);
        if (board is null || rights is null || !rights.CanView)
        {
            return Result<TicketDto>.Failure(NotFound);
        }

        if (!rights.CanWrite(PermissionAction.Edit))
        {
            return Result<TicketDto>.Failure(WikiErrors.Forbidden);
        }

        var user = http.HttpContext?.User;
        if (!Guid.TryParse(user?.FindFirstValue("raavi:sid"), out var sessionId))
        {
            return Result<TicketDto>.Failure(WikiErrors.Forbidden); // tickets are bound to an interactive session
        }

        var options = CollaborationOptions.From(configuration);
        var now = clock.UtcNow;
        var lifetime = TimeSpan.FromSeconds(Math.Clamp(options.TicketLifetimeSeconds, 10, 60));
        var address = new DocumentAddress(db.CurrentTenantId, boardId, DocumentKind.Board);
        var ticket = signer.Sign(new TicketClaims(actor.UserId, address.TenantId, address.Name, "edit", sessionId, user!.FindFirstValue(ClaimTypes.Name) ?? string.Empty), lifetime, now);
        return Result<TicketDto>.Success(new TicketDto(ticket, address.Name, options.PublicUrl!, "edit", now + lifetime));
    }

    // ── Internal persistence (collaboration server, tenant scope opened by the endpoint) ─────────

    public async Task<CollaborationStateDto?> LoadStateAsync(Guid boardId, CancellationToken ct)
    {
        var board = await db.Whiteboards.AsNoTracking().FirstOrDefaultAsync(b => b.Id == boardId, ct).ConfigureAwait(false);
        if (board is null)
        {
            return null;
        }

        if (board.YjsState is { Length: > 0 } state)
        {
            return new CollaborationStateDto(Convert.ToBase64String(state), [], board.Revision, null);
        }

        // First live session since the last REST save: seed the Yjs document from the stored elements.
        using var seed = JsonDocument.Parse(JsonSerializer.Serialize(new { elements = board.Elements }, Web));
        return new CollaborationStateDto(null, [], board.Revision, seed.RootElement.Clone());
    }

    /// <summary>The collaboration server sends the full Yjs state (<c>Update</c>) and the converted elements (<c>Json</c>).</summary>
    public async Task<Result<long>> StoreStateAsync(Guid boardId, StoreUpdateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        byte[]? state;
        try
        {
            state = string.IsNullOrEmpty(request.Update) ? null : Convert.FromBase64String(request.Update);
        }
        catch (FormatException)
        {
            state = null;
        }

        if (state is null || state.Length > Whiteboard.MaxLiveStateBytes || request.Json is not { ValueKind: JsonValueKind.Object } json || !json.TryGetProperty("elements", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return Result<long>.Failure(new Error("collaboration.update_invalid", "Invalid board state."));
        }

        List<BoardElementInput>? elements;
        try
        {
            elements = list.Deserialize<List<BoardElementInput>>(Web);
        }
        catch (JsonException)
        {
            elements = null;
        }

        if (elements is null)
        {
            return Result<long>.Failure(new Error("collaboration.update_invalid", "Invalid board elements."));
        }

        var board = await db.Whiteboards.FirstOrDefaultAsync(b => b.Id == boardId, ct).ConfigureAwait(false);
        if (board is null)
        {
            return Result<long>.Failure(NotFound);
        }

        board.SetLiveState(elements, state, clock.UtcNow); // validates like a REST save; a DomainException becomes 400 and the server resets the document
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<long>.Success(board.Revision);
    }

    /// <summary>Re-evaluates a live connection: session still valid, board still there and still editable by the user.</summary>
    public async Task<string> CheckAsync(Guid boardId, Guid userId, Guid sessionId, CancellationToken ct)
    {
        if (!await sessions.IsActiveAsync(sessionId, userId, ct).ConfigureAwait(false))
        {
            return "none";
        }

        actor.ActAs(userId); // permissions read fresh (no membership cache)
        var board = await db.Whiteboards.AsNoTracking().FirstOrDefaultAsync(b => b.Id == boardId, ct).ConfigureAwait(false);
        var rights = board is null ? null : await RightsAsync(board, ct).ConfigureAwait(false);
        return rights is null || !rights.CanView ? "none" : rights.CanWrite(PermissionAction.Edit) ? "edit" : "view";
    }

    private async Task<SpaceRights?> RightsAsync(Whiteboard board, CancellationToken ct)
    {
        var space = await db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == board.SpaceId, ct).ConfigureAwait(false);
        return space is null ? null : await authz.GetSpaceRightsAsync(space, ct).ConfigureAwait(false);
    }
}
