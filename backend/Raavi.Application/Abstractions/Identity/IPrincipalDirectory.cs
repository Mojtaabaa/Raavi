namespace Raavi.Application.Abstractions.Identity;

/// <summary>
/// Who a user is inside the <b>current</b> tenant, as needed by the permission evaluator (ADR-012 step 4).
/// Group membership is flattened at write time (no nested groups yet).
/// </summary>
public sealed record PrincipalSnapshot(Guid UserId, bool IsActiveMember, bool IsTenantAdministrator, IReadOnlySet<Guid> GroupIds)
{
    public static PrincipalSnapshot None(Guid userId) => new(userId, false, false, new HashSet<Guid>());
}

public enum DirectoryEntryKind
{
    User = 1,
    Group = 2,
}

/// <summary>How to reach an active member: the e-mail address only when it is verified (never send to unverified addresses).</summary>
public sealed record PrincipalContact(Guid UserId, string DisplayName, string? VerifiedEmail, string? PreferredLocale);

/// <summary>A user or group of the current tenant, for permission pickers and author display.</summary>
public sealed record DirectoryEntry(DirectoryEntryKind Kind, Guid Id, string DisplayName, string? Detail);

/// <summary>
/// Read-only directory of the current tenant's principals, implemented by the Identity module and consumed by other
/// modules (MODULE_BOUNDARIES.md rule 4). Every method is scoped to the resolved tenant: ids of other tenants' users or
/// groups are simply absent from the results.
/// </summary>
public interface IPrincipalDirectory
{
    /// <summary>
    /// Membership, built-in role and group ids of <paramref name="userId"/> in the current tenant. <paramref name="fresh"/>
    /// bypasses the short membership cache (background work acting for a user must see revocations made in other processes).
    /// </summary>
    ValueTask<PrincipalSnapshot> GetPrincipalAsync(Guid userId, bool fresh = false, CancellationToken cancellationToken = default);

    /// <summary>Display names of users that are members of the current tenant (others are omitted).</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetUserNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);

    /// <summary>Contact details of <b>active</b> members of the current tenant (others are omitted), for notifications.</summary>
    Task<IReadOnlyDictionary<Guid, PrincipalContact>> GetContactsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Active members of the current tenant whose <b>verified</b> e-mail equals one of <paramref name="emails"/> (case-insensitive),
    /// keyed by the e-mail as given. Unverified addresses never match (migration mapping, ADR-025).
    /// </summary>
    Task<IReadOnlyDictionary<string, DirectoryEntry>> FindUsersByVerifiedEmailAsync(IReadOnlyCollection<string> emails, CancellationToken cancellationToken = default);

    /// <summary>Groups of the current tenant by exact name (case-insensitive), keyed by the name as given.</summary>
    Task<IReadOnlyDictionary<string, Guid>> FindGroupsByNameAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken = default);

    /// <summary>Names of groups of the current tenant (others are omitted).</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetGroupNamesAsync(IReadOnlyCollection<Guid> groupIds, CancellationToken cancellationToken = default);

    /// <summary>Active members and groups whose name (or member e-mail) contains <paramref name="query"/>.</summary>
    Task<IReadOnlyList<DirectoryEntry>> SearchAsync(string query, int take, CancellationToken cancellationToken = default);
}
