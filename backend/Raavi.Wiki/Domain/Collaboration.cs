using Raavi.Domain.Common;

namespace Raavi.Wiki.Domain;

/// <summary>
/// Durable state of one real-time document (COLLABORATION_ARCHITECTURE.md §5): the Yjs state is the latest snapshot plus
/// the incremental updates after it. The collaboration server has no database access; it reads and writes these rows
/// through the API's internal endpoints. The page draft (Document JSON) is kept in step with every stored update.
/// </summary>
public sealed class CollaborationDocument : Entity, ITenantOwned
{
    private CollaborationDocument() { }

    public Guid TenantId { get; private set; }

    public Guid ContentId { get; private set; }

    /// <summary>Sequence number of the last stored update.</summary>
    public long LastSeq { get; private set; }

    /// <summary>Updates up to and including this sequence number are contained in the latest snapshot.</summary>
    public long SnapshotSeq { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static CollaborationDocument Create(Guid contentId, DateTimeOffset now) => new() { ContentId = contentId, UpdatedAt = now };

    public long NextSeq(DateTimeOffset now)
    {
        LastSeq++;
        UpdatedAt = now;
        return LastSeq;
    }

    public void Snapshotted(long seq, DateTimeOffset now)
    {
        SnapshotSeq = seq;
        UpdatedAt = now;
    }
}

/// <summary>One incremental Yjs update (binary, opaque to the API).</summary>
public sealed class CollaborationUpdate : Entity, ITenantOwned
{
    public const int MaxBytes = 4 * 1024 * 1024;

    private CollaborationUpdate() { }

    public Guid TenantId { get; private set; }

    public Guid DocumentId { get; private set; }

    public long Seq { get; private set; }

    public byte[] Bytes { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public static CollaborationUpdate Create(Guid documentId, long seq, byte[] bytes, DateTimeOffset now) =>
        new() { DocumentId = documentId, Seq = seq, Bytes = bytes, CreatedAt = now };
}

/// <summary>A full Yjs state covering all updates up to <see cref="Seq"/>; older updates are compacted away.</summary>
public sealed class CollaborationSnapshot : Entity, ITenantOwned
{
    public const int MaxBytes = 20 * 1024 * 1024;

    private CollaborationSnapshot() { }

    public Guid TenantId { get; private set; }

    public Guid DocumentId { get; private set; }

    public long Seq { get; private set; }

    public byte[] Bytes { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public static CollaborationSnapshot Create(Guid documentId, long seq, byte[] bytes, DateTimeOffset now) =>
        new() { DocumentId = documentId, Seq = seq, Bytes = bytes, CreatedAt = now };
}
