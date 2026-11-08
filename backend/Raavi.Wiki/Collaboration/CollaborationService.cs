using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Raavi.Application.Abstractions.Content;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Wiki.Application;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki.Collaboration;

public sealed record TicketDto(string Ticket, string DocumentName, string Url, string Permission, DateTimeOffset ExpiresAt);

public sealed record CollaborationStateDto(string? Snapshot, IReadOnlyList<string> Updates, long Seq, JsonElement? Seed);

public sealed record StoreUpdateRequest(string Update, JsonElement? Json, IReadOnlyList<Guid>? Contributors);

public sealed record StoreSnapshotRequest(string State, long Seq);

public sealed record CheckRequest(string DocumentName, Guid UserId, Guid SessionId);

public enum DocumentKind
{
    /// <summary>A page draft: <c>{tenantId}:page:{contentId}:draft</c>.</summary>
    Page = 0,

    /// <summary>A whiteboard: <c>{tenantId}:board:{boardId}:live</c> (Phase 11c).</summary>
    Board = 1,
}

/// <summary>A parsed collaboration document name (an address, never a credential). <see cref="ContentId"/> is the page or the board id, depending on <see cref="Kind"/>.</summary>
public sealed record DocumentAddress(Guid TenantId, Guid ContentId, DocumentKind Kind = DocumentKind.Page)
{
    public string Name => Kind == DocumentKind.Board ? $"{TenantId:D}:board:{ContentId:D}:live" : $"{TenantId:D}:page:{ContentId:D}:draft";

    public static DocumentAddress? Parse(string? name)
    {
        var parts = (name ?? string.Empty).Split(':');
        if (parts.Length != 4 || !Guid.TryParseExact(parts[0], "D", out var tid) || !Guid.TryParseExact(parts[2], "D", out var cid))
        {
            return null;
        }

        return (parts[1], parts[3]) switch
        {
            ("page", "draft") => new DocumentAddress(tid, cid),
            ("board", "live") => new DocumentAddress(tid, cid, DocumentKind.Board),
            _ => null,
        };
    }
}

/// <summary>
/// Real-time collaboration on page drafts (ADR-011); whiteboards are handled by <see cref="BoardCollaborationService"/> (ADR-035). Issues tickets after the same checks as REST editing; serves the
/// collaboration server's persistence calls inside the document's tenant scope; keeps the shared draft in step.
/// </summary>
public sealed class CollaborationService(
    WikiDbContext db,
    PageService pages,
    PublishingService publishing,
    WikiActor actor,
    ISessionValidator sessions,
    TicketSigner signer,
    IConfiguration configuration,
    IHttpContextAccessor http,
    IClock clock)
{
    public static readonly Error Unavailable = new("collaboration.unavailable", "Real-time editing is not available.", ErrorKind.Unavailable);

    public bool IsEnabled
    {
        get
        {
            var o = CollaborationOptions.From(configuration);
            return o.Enabled && signer.IsAvailable && !string.IsNullOrWhiteSpace(o.PublicUrl) && !string.IsNullOrEmpty(o.ServiceKey);
        }
    }

    // ── Tickets (browser, tenant host) ───────────────────────────────────────────────────────────

    public async Task<Result<TicketDto>> IssueTicketAsync(Guid contentId, CancellationToken ct)
    {
        if (!IsEnabled)
        {
            return Result<TicketDto>.Failure(Unavailable);
        }

        var loaded = await pages.LoadAsync(contentId, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<TicketDto>.Failure(loaded.Error!);
        }

        if (!loaded.Value.Provider.Capabilities.HasFlag(ContentTypeCapabilities.Versioned) || !loaded.Value.Rights.CanEdit)
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
        var address = new DocumentAddress(db.CurrentTenantId, contentId);
        var ticket = signer.Sign(new TicketClaims(actor.UserId, address.TenantId, address.Name, "edit", sessionId, user!.FindFirstValue(ClaimTypes.Name) ?? string.Empty), lifetime, now);
        return Result<TicketDto>.Success(new TicketDto(ticket, address.Name, options.PublicUrl!, "edit", now + lifetime));
    }

    // ── Internal persistence (collaboration server, tenant scope opened by the endpoint) ─────────

    public async Task<CollaborationStateDto?> LoadStateAsync(Guid contentId, CancellationToken ct)
    {
        var node = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == contentId && n.Status != ContentStatus.Trashed, ct).ConfigureAwait(false);
        if (node is null)
        {
            return null;
        }

        var doc = await db.CollaborationDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.ContentId == contentId, ct).ConfigureAwait(false);
        if (doc is not null)
        {
            var snapshot = await db.CollaborationSnapshots.AsNoTracking().Where(s => s.DocumentId == doc.Id).OrderByDescending(s => s.Seq).Select(s => s.Bytes).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            var updates = await db.CollaborationUpdates.AsNoTracking().Where(u => u.DocumentId == doc.Id && u.Seq > doc.SnapshotSeq).OrderBy(u => u.Seq).Select(u => u.Bytes).ToListAsync(ct).ConfigureAwait(false);
            if (snapshot is not null || updates.Count > 0)
            {
                return new CollaborationStateDto(snapshot is null ? null : Convert.ToBase64String(snapshot), updates.Select(Convert.ToBase64String).ToList(), doc.LastSeq, null);
            }
        }

        // First load: seed the Yjs document from the shared draft, or from the published version.
        var json = await db.Drafts.AsNoTracking().Where(d => d.ContentId == contentId).Select(d => d.ContentJson).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? (node.CurrentVersionId is { } vid
                ? await db.PageVersions.AsNoTracking().Where(v => v.Id == vid).Select(v => v.ContentJson).FirstAsync(ct).ConfigureAwait(false)
                : RaaviDocument.Empty.Json);
        using var seed = JsonDocument.Parse(RaaviDocument.Parse(json).Json);
        return new CollaborationStateDto(null, [], 0, seed.RootElement.Clone());
    }

    /// <summary>Appends an incremental update and writes the converted Document JSON into the shared draft.</summary>
    public async Task<Result<long>> StoreUpdateAsync(Guid contentId, StoreUpdateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bytes = Decode(request.Update, CollaborationUpdate.MaxBytes);
        if (bytes is null)
        {
            return Result<long>.Failure(new Error("collaboration.update_invalid", "Invalid update."));
        }

        if (!await db.Nodes.AnyAsync(n => n.Id == contentId && n.Status != ContentStatus.Trashed, ct).ConfigureAwait(false))
        {
            return Result<long>.Failure(WikiErrors.ContentNotFound);
        }

        // Validate the converted body before anything is persisted: content outside the registry is refused.
        var body = request.Json is { } j ? RaaviDocument.Parse(new JsonObjectEnvelope(j).ToJson()) : null;
        var now = clock.UtcNow;
        var doc = await db.CollaborationDocuments.FirstOrDefaultAsync(d => d.ContentId == contentId, ct).ConfigureAwait(false);
        if (doc is null)
        {
            doc = CollaborationDocument.Create(contentId, now);
            db.CollaborationDocuments.Add(doc);
        }

        var seq = doc.NextSeq(now);
        db.CollaborationUpdates.Add(CollaborationUpdate.Create(doc.Id, seq, bytes, now));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (body is not null)
        {
            await publishing.ApplyCollaborationBodyAsync(contentId, body, request.Contributors ?? [], ct).ConfigureAwait(false);
        }

        return Result<long>.Success(seq);
    }

    /// <summary>Stores a full state and compacts the updates it covers (after the snapshot is committed).</summary>
    public async Task<Result> StoreSnapshotAsync(Guid contentId, StoreSnapshotRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bytes = Decode(request.State, CollaborationSnapshot.MaxBytes);
        var doc = await db.CollaborationDocuments.FirstOrDefaultAsync(d => d.ContentId == contentId, ct).ConfigureAwait(false);
        if (bytes is null || doc is null || request.Seq > doc.LastSeq || request.Seq < doc.SnapshotSeq)
        {
            return Result.Failure(new Error("collaboration.snapshot_invalid", "Invalid snapshot.", ErrorKind.Conflict));
        }

        var now = clock.UtcNow;
        db.CollaborationSnapshots.Add(CollaborationSnapshot.Create(doc.Id, request.Seq, bytes, now));
        doc.Snapshotted(request.Seq, now);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await db.CollaborationUpdates.Where(u => u.DocumentId == doc.Id && u.Seq <= request.Seq).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.CollaborationSnapshots.Where(s => s.DocumentId == doc.Id && s.Seq < request.Seq).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>Re-evaluates a live connection: session still valid, still a member, still allowed to edit the page.</summary>
    public async Task<string> CheckAsync(Guid contentId, Guid userId, Guid sessionId, CancellationToken ct)
    {
        if (!await sessions.IsActiveAsync(sessionId, userId, ct).ConfigureAwait(false))
        {
            return "none";
        }

        actor.ActAs(userId); // permissions read fresh (no membership cache) — WikiActor.IsBackground
        var loaded = await pages.LoadAsync(contentId, track: false, ct).ConfigureAwait(false);
        return !loaded.IsSuccess ? "none" : loaded.Value.Rights.CanEdit ? "edit" : "view";
    }

    private static byte[]? Decode(string? base64, int max)
    {
        if (string.IsNullOrEmpty(base64) || base64.Length > max / 3 * 4 + 4)
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(base64);
            return bytes.Length is > 0 && bytes.Length <= max ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The collaboration server sends the bare ProseMirror <c>doc</c>; wrap it in the canonical envelope.</summary>
    private readonly struct JsonObjectEnvelope(JsonElement doc)
    {
        public string ToJson() => string.Create(CultureInfo.InvariantCulture,
            $$"""{"schema":"raavi.doc","schemaVersion":{{RaaviDocument.CurrentSchemaVersion}},"doc":{{doc.GetRawText()}}}""");
    }
}
