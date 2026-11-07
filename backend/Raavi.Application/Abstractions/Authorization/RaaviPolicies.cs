namespace Raavi.Application.Abstractions.Authorization;

/// <summary>Authorization policy names shared across modules.</summary>
public static class RaaviPolicies
{
    /// <summary>System administration on the system host. Operator API keys or an MFA session of a user holding the SystemAdministrator role.</summary>
    public const string SystemAdministrator = "raavi.system-admin";

    public const string SystemAdministratorRole = "raavi:system-admin";

    /// <summary>Tenant administration on a tenant host (members, invitations, groups, provider policies).</summary>
    public const string TenantAdministrator = "raavi.tenant-admin";

    public const string TenantAdministratorRole = "raavi:tenant-admin";

    /// <summary>Any signed-in member of the resolved tenant, or any signed-in user on the system host.</summary>
    public const string AuthenticatedUser = "raavi.user";

    public const string ActorTypeClaim = "raavi:actor_type";
}
