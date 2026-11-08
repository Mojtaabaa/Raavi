using Raavi.Application.Abstractions.Tenancy;
using Raavi.Domain.Common;

namespace Raavi.Integrations.Persistence;

public enum IssueConnectorKind
{
    GitHub = 1,
    GitLab = 2,
    Jira = 3,
}

/// <summary>
/// A tenant's read-only connection to an issue tracker (Phase 13a, ADR-043): GitHub (github.com or Enterprise Server), GitLab or Jira Data Center.
/// The access token is optional (public data) and stored only protected (Data Protection, tenant-bound purpose); it is never returned.
/// </summary>
public sealed class IssueConnector : Entity, ITenantOwned
{
    public const int MaxNameLength = 100;
    public const int MaxUrlLength = 300;

    private IssueConnector() { }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public IssueConnectorKind Kind { get; private set; }

    /// <summary>API base for GitHub (https://api.github.com or https://host/api/v3), web base for GitLab and Jira (https://host[/prefix]).</summary>
    public string BaseUrl { get; private set; } = string.Empty;

    public string? ProtectedToken { get; private set; }

    public bool Enabled { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public int Revision { get; private set; } = 1;

    public static IssueConnector Create(string? name, IssueConnectorKind kind, string baseUrl, string? protectedToken, Guid userId, DateTimeOffset now)
    {
        var c = new IssueConnector { CreatedBy = userId, CreatedAt = now, UpdatedAt = now, Enabled = true, ProtectedToken = protectedToken };
        c.Set(name, kind, baseUrl);
        return c;
    }

    /// <param name="newProtectedToken">null keeps the stored token; an empty string removes it.</param>
    public void Update(string? name, IssueConnectorKind kind, string baseUrl, string? newProtectedToken, bool enabled, DateTimeOffset now)
    {
        Set(name, kind, baseUrl);
        if (newProtectedToken is not null)
        {
            ProtectedToken = newProtectedToken.Length == 0 ? null : newProtectedToken;
        }

        Enabled = enabled;
        UpdatedAt = now;
        Revision++;
    }

    private void Set(string? name, IssueConnectorKind kind, string baseUrl)
    {
        var n = (name ?? string.Empty).Trim();
        if (n.Length is 0 or > MaxNameLength || n.Any(char.IsControl))
        {
            throw new DomainException("connector.name_invalid", $"A connector needs a name of 1–{MaxNameLength} characters.");
        }

        if (!Enum.IsDefined(kind))
        {
            throw new DomainException("connector.kind_invalid", "Unsupported connector type.");
        }

        if (baseUrl.Length is 0 or > MaxUrlLength)
        {
            throw new DomainException("connector.url_invalid", $"The address must be 1–{MaxUrlLength} characters.");
        }

        Name = n;
        Kind = kind;
        BaseUrl = baseUrl;
    }
}
