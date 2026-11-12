using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Calendar;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Integrations.Persistence;

namespace Raavi.Integrations.Connectors;

/// <summary>Read-only Jira due-date overlay for the calendar feed. Never persists issues as Raavi events.</summary>
public sealed class JiraCalendarOverlay(
    IntegrationsDbContext db,
    IHttpClientFactory clients,
    ConnectorHostPolicy policy,
    IDataProtectionProvider protection,
    ITenantContext tenant) : ICalendarOverlaySource
{
    public string Kind => "Jira";

    public async Task<IReadOnlyList<CalendarOverlayOccurrence>> GetOccurrencesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        if (!policy.IsConfigured)
        {
            return [];
        }

        var connectors = await db.IssueConnectors.AsNoTracking()
            .Where(c => c.Enabled && c.Kind == IssueConnectorKind.Jira && c.ProtectedToken != null)
            .Take(5).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (connectors.Count == 0)
        {
            return [];
        }

        var list = new List<CalendarOverlayOccurrence>();
        var client = clients.CreateClient(ConnectorHttp.ClientName);
        var fromStr = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var toStr = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var connector in connectors)
        {
            if (policy.TryValidate(connector.BaseUrl, out _) is null)
            {
                continue;
            }

            string token;
            try
            {
                token = protection.CreateProtector(IssueConnectorService.ProtectorPurpose(tenant.Tenant.TenantId)).Unprotect(connector.ProtectedToken!);
            }
            catch
            {
                continue;
            }

            var jql = $"duedate >= \"{fromStr}\" AND duedate <= \"{toStr}\" ORDER BY duedate ASC";
            var path = $"rest/api/2/search?jql={Uri.EscapeDataString(jql)}&maxResults=50&fields=summary,duedate,status";
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(connector.BaseUrl.TrimEnd('/') + "/" + path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            try
            {
                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!doc.RootElement.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var issue in issues.EnumerateArray())
                {
                    var key = issue.TryGetProperty("key", out var k) ? k.GetString() : null;
                    if (string.IsNullOrWhiteSpace(key) || !issue.TryGetProperty("fields", out var fields))
                    {
                        continue;
                    }

                    var summary = fields.TryGetProperty("summary", out var s) ? s.GetString() : key;
                    if (!fields.TryGetProperty("duedate", out var dueEl) || dueEl.ValueKind != JsonValueKind.String
                        || !DateOnly.TryParse(dueEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var due))
                    {
                        continue;
                    }

                    var url = $"{connector.BaseUrl.TrimEnd('/')}/browse/{key}";
                    list.Add(new CalendarOverlayOccurrence(
                        "Jira", key!, summary ?? key!, null, true,
                        due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null,
                        due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null,
                        "UTC", url, FreeBusyOnly: false));
                    if (list.Count >= 200)
                    {
                        return list;
                    }
                }
            }
            catch (HttpRequestException)
            {
                // Overlay is best-effort; never fail the Raavi calendar feed.
            }
            catch (JsonException)
            {
            }
        }

        return list;
    }
}
