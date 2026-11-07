namespace Raavi.Application.Abstractions.Identity;

/// <summary>The authenticated principal of the current unit of work. Authentication only — membership and permissions are separate.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    /// <summary>Raavi user id. Throws when not authenticated.</summary>
    Guid UserId { get; }

    /// <summary>Authentication provider that established the session (e.g. "local", "jira-eng").</summary>
    string? AuthenticationProviderId { get; }
}
