namespace Raavi.Integrations;

/// <summary>
/// Integrations module (Phase 11): Jira, GitHub, GitLab, Teams, Outlook, Google Workspace, SMTP, webhooks.
/// Outbound calls go through the SSRF-guarded HTTP client defined in SECURITY_ARCHITECTURE.md §5.
/// </summary>
public static class IntegrationsModule
{
    public const string Name = "integrations";
}
