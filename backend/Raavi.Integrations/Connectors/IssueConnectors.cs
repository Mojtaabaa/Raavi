using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Integrations.Persistence;
using Raavi.Integrations.Webhooks;

namespace Raavi.Integrations.Connectors;

public static class ConnectorHttp
{
    public const string ClientName = "raavi-connectors";
}

/// <summary>Which hosts issue connectors may call. Same SSRF rules as webhooks (<see cref="WebhookHostPolicy"/>) with its own allow-list.</summary>
public sealed class ConnectorHostPolicy(IEnumerable<string> allowedHosts)
{
    private readonly WebhookHostPolicy _inner = new(allowedHosts);

    public bool IsConfigured => _inner.IsConfigured;

    public Uri? TryValidate(string? url, out string? code)
    {
        var uri = _inner.TryValidate(url, out var inner);
        code = inner?.Replace("webhook.", "connector.", StringComparison.Ordinal);
        return uri;
    }
}

public sealed record ConnectorDto(Guid Id, string Name, string Kind, string BaseUrl, bool HasToken, bool Enabled, DateTimeOffset CreatedAt, int Revision);

/// <param name="Token">null on update keeps the stored token, an empty string removes it.</param>
public sealed record ConnectorRequest(string? Name, string? Kind, string? BaseUrl, string? Token, bool? Enabled);

public sealed record ConnectorCatalogDto(string[] Kinds, bool HostPolicyConfigured);

public sealed record IssueDto(string Reference, string Title, string Status, string Url, string Type);

/// <summary>Parses and validates issue references. Anything that does not match is refused before a URL is built (no path traversal, no query injection).</summary>
public static partial class IssueReference
{
    [GeneratedRegex(@"^(?<owner>[A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/(?<repo>[A-Za-z0-9._-]{1,100})#(?<n>[0-9]{1,9})$", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex GitHubPattern();

    [GeneratedRegex(@"^(?<path>(?:[A-Za-z0-9._-]{1,100}/){1,19}[A-Za-z0-9._-]{1,100})#(?<n>[0-9]{1,9})$", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex GitLabPattern();

    [GeneratedRegex(@"^(?<key>[A-Z][A-Z0-9_]{1,9}-[0-9]{1,9})$", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex JiraPattern();

    /// <summary>Returns the API path (relative to the connector base, no leading slash) for a valid reference, else null.</summary>
    public static string? ApiPath(IssueConnectorKind kind, string? reference)
    {
        var r = (reference ?? string.Empty).Trim();
        if (r.Length is 0 or > 300)
        {
            return null;
        }

        switch (kind)
        {
            case IssueConnectorKind.GitHub:
                var gh = GitHubPattern().Match(r);
                return gh.Success && !IsDots(gh.Groups["repo"].Value) ? $"repos/{gh.Groups["owner"].Value}/{gh.Groups["repo"].Value}/issues/{gh.Groups["n"].Value}" : null;
            case IssueConnectorKind.GitLab:
                var gl = GitLabPattern().Match(r);
                var path = gl.Groups["path"].Value;
                return gl.Success && !path.Split('/').Any(IsDots) ? $"api/v4/projects/{Uri.EscapeDataString(path)}/issues/{gl.Groups["n"].Value}" : null;
            case IssueConnectorKind.Jira:
                var jira = JiraPattern().Match(r);
                return jira.Success ? $"rest/api/2/issue/{jira.Groups["key"].Value}?fields=summary,status" : null;
            default:
                return null;
        }
    }

    private static bool IsDots(string segment) => segment is "." or "..";
}

/// <summary>Administration of the tenant's issue connectors (Phase 13a, ADR-043).</summary>
public sealed class IssueConnectorService(IntegrationsDbContext db, ConnectorHostPolicy policy, IOptions<IntegrationsOptions> options, IDataProtectionProvider protection, ITenantContext tenant, ICurrentUser current, IClock clock)
{
    public const int MaxConnectorsPerTenant = 10;

    public static readonly Error NotFound = new("connector.not_found", "Connector not found.", ErrorKind.NotFound);
    public static readonly Error TooMany = new("connector.too_many", $"A workspace can have at most {MaxConnectorsPerTenant} connectors.", ErrorKind.Conflict);
    public static readonly Error NameTaken = new("connector.name_taken", "A connector with this name already exists.", ErrorKind.Conflict);

    public static string ProtectorPurpose(Guid tenantId) => $"Raavi.Connectors.Token.v1.{tenantId:N}";

    private IDataProtector Protector => protection.CreateProtector(ProtectorPurpose(tenant.Tenant.TenantId));

    public ConnectorCatalogDto Catalog() => new([.. Enum.GetNames<IssueConnectorKind>()], policy.IsConfigured);

    public async Task<IReadOnlyList<ConnectorDto>> ListAsync(CancellationToken ct) =>
        (await db.IssueConnectors.AsNoTracking().OrderBy(c => c.Name).Take(MaxConnectorsPerTenant).ToListAsync(ct).ConfigureAwait(false)).Select(ToDto).ToList();

    public async Task<Result<ConnectorDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var c = await db.IssueConnectors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        return c is null ? Result<ConnectorDto>.Failure(NotFound) : Result<ConnectorDto>.Success(ToDto(c));
    }

    public async Task<Result<ConnectorDto>> CreateAsync(ConnectorRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var check = Validate(request, out var kind, out var baseUrl);
        if (check is not null)
        {
            return Result<ConnectorDto>.Failure(check);
        }

        if (await db.IssueConnectors.CountAsync(ct).ConfigureAwait(false) >= MaxConnectorsPerTenant)
        {
            return Result<ConnectorDto>.Failure(TooMany);
        }

        var name = (request.Name ?? string.Empty).Trim();
        if (await db.IssueConnectors.AnyAsync(c => c.Name == name, ct).ConfigureAwait(false))
        {
            return Result<ConnectorDto>.Failure(NameTaken);
        }

        var token = string.IsNullOrWhiteSpace(request.Token) ? null : Protector.Protect(request.Token.Trim());
        var c = IssueConnector.Create(request.Name, kind, baseUrl, token, current.UserId, clock.UtcNow);
        db.IssueConnectors.Add(c);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<ConnectorDto>.Success(ToDto(c));
    }

    public async Task<Result<ConnectorDto>> UpdateAsync(Guid id, int expectedRevision, ConnectorRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var c = await db.IssueConnectors.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        if (c is null)
        {
            return Result<ConnectorDto>.Failure(NotFound);
        }

        if (c.Revision != expectedRevision)
        {
            return Result<ConnectorDto>.Failure(new Error("precondition.failed", "The connector was changed by someone else. Reload and try again.", ErrorKind.PreconditionFailed));
        }

        var check = Validate(request, out var kind, out var baseUrl);
        if (check is not null)
        {
            return Result<ConnectorDto>.Failure(check);
        }

        var name = (request.Name ?? string.Empty).Trim();
        if (await db.IssueConnectors.AnyAsync(x => x.Name == name && x.Id != id, ct).ConfigureAwait(false))
        {
            return Result<ConnectorDto>.Failure(NameTaken);
        }

        string? token = request.Token is null ? null : request.Token.Trim().Length == 0 ? string.Empty : Protector.Protect(request.Token.Trim());
        c.Update(request.Name, kind, baseUrl, token, request.Enabled ?? c.Enabled, clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<ConnectorDto>.Success(ToDto(c));
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct)
    {
        var c = await db.IssueConnectors.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        if (c is null)
        {
            return Result.Failure(NotFound);
        }

        db.IssueConnectors.Remove(c);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    private Error? Validate(ConnectorRequest request, out IssueConnectorKind kind, out string baseUrl)
    {
        baseUrl = string.Empty;
        if (!Enum.TryParse(request.Kind, ignoreCase: true, out kind) || !Enum.IsDefined(kind) || int.TryParse(request.Kind, out _))
        {
            return new Error("connector.kind_invalid", "Unsupported connector type.", ErrorKind.Validation);
        }

        var uri = policy.TryValidate(request.BaseUrl, out var code);
        if (uri is null)
        {
            return new Error(code!, code switch
            {
                "connector.host_not_allowed" => "This host is not in the connector hosts allowed by the administrator of the installation.",
                "connector.host_forbidden" => "This address is not allowed for a connector.",
                _ => "The address must be an http(s) URL without credentials or a fragment.",
            }, ErrorKind.Validation);
        }

        if (!string.IsNullOrWhiteSpace(request.Token) && uri.Scheme != Uri.UriSchemeHttps && !options.Value.AllowHttpConnectorTokens)
        {
            return new Error("connector.https_required", "A connector with an access token must use https (or enable Raavi:Integrations:AllowHttpConnectorTokens for http trackers).", ErrorKind.Validation);
        }

        if (!string.IsNullOrEmpty(uri.Query))
        {
            return new Error("connector.url_invalid", "The address must not contain a query string.", ErrorKind.Validation);
        }

        baseUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return null;
    }

    private static ConnectorDto ToDto(IssueConnector c) => new(c.Id, c.Name, c.Kind.ToString(), c.BaseUrl, c.ProtectedToken is not null, c.Enabled, c.CreatedAt, c.Revision);
}

/// <summary>
/// Looks up one issue or pull request through a connector (Phase 13a). Any signed-in member of the workspace may ask — the token is the workspace's
/// read access to the tracker, chosen by its administrators. References are validated before a URL is built, upstream bodies are size-capped and never
/// returned, redirects are not followed, results are cached for a minute and every user is limited per hour.
/// </summary>
public sealed class IssueLookupService(
    IntegrationsDbContext db, IHttpClientFactory clients, ConnectorHostPolicy policy, IDataProtectionProvider protection, IMemoryCache cache,
    IOptions<IntegrationsOptions> options, ITenantContext tenant, ICurrentUser current, IClock clock)
{
    public const int MaxResponseBytes = 256 * 1024;
    public const int LookupsPerUserPerHour = 300;
    public static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(60);

    public static readonly Error ReferenceInvalid = new("issue.reference_invalid", "The reference does not have the format of this connector (GitHub owner/repo#1, GitLab group/project#1, Jira KEY-1).", ErrorKind.Validation);
    public static readonly Error IssueNotFound = new("issue.not_found", "The issue was not found or the connector cannot see it.", ErrorKind.NotFound);
    public static readonly Error Disabled = new("connector.disabled", "This connector is switched off.", ErrorKind.Conflict);
    public static readonly Error Denied = new("issue.upstream_denied", "The tracker refused the connector's access token.", ErrorKind.Unavailable);
    public static readonly Error Unreachable = new("issue.upstream_unreachable", "The tracker could not be reached.", ErrorKind.Unavailable);
    public static readonly Error BadResponse = new("issue.upstream_invalid", "The tracker answered in a way Raavi does not understand.", ErrorKind.Unavailable);
    public static readonly Error Limited = new("issue.rate_limited", "Too many lookups; try again later.", ErrorKind.TooManyRequests);

    public async Task<Result<IssueDto>> LookupAsync(Guid connectorId, string? reference, CancellationToken ct)
    {
        var c = await db.IssueConnectors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == connectorId, ct).ConfigureAwait(false);
        if (c is null)
        {
            return Result<IssueDto>.Failure(IssueConnectorService.NotFound);
        }

        if (!c.Enabled)
        {
            return Result<IssueDto>.Failure(Disabled);
        }

        var path = IssueReference.ApiPath(c.Kind, reference);
        if (path is null)
        {
            return Result<IssueDto>.Failure(ReferenceInvalid);
        }

        var tenantId = tenant.Tenant.TenantId;
        var cacheKey = $"issue:{tenantId:N}:{c.Id:N}:{c.Revision}:{path}";
        if (cache.TryGetValue(cacheKey, out IssueDto? hit) && hit is not null)
        {
            return Result<IssueDto>.Success(hit);
        }

        if (!TakeQuota(tenantId, current.UserId))
        {
            return Result<IssueDto>.Failure(Limited);
        }

        var result = await FetchAsync(c, path, reference!.Trim(), ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            cache.Set(cacheKey, result.Value, CacheTime);
        }

        return result;
    }

    /// <summary>Checks the connector end to end with a harmless request (used by the administrator's "test" button).</summary>
    public async Task<Result<IssueDto>> FetchAsync(IssueConnector c, string path, string reference, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (policy.TryValidate(c.BaseUrl, out _) is null)
        {
            return Result<IssueDto>.Failure(Unreachable); // the allow-list may have been tightened since the connector was saved
        }

        string? token = null;
        if (c.ProtectedToken is not null)
        {
            try
            {
                token = protection.CreateProtector(IssueConnectorService.ProtectorPurpose(tenant.Tenant.TenantId)).Unprotect(c.ProtectedToken);
            }
            catch (CryptographicException)
            {
                return Result<IssueDto>.Failure(Denied);
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(c.BaseUrl + "/" + path));
        request.Headers.TryAddWithoutValidation("User-Agent", "Raavi-Connectors/1");
        request.Headers.TryAddWithoutValidation("Accept", c.Kind == IssueConnectorKind.GitHub ? "application/vnd.github+json" : "application/json");
        if (c.Kind == IssueConnectorKind.GitHub)
        {
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        }

        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation(c.Kind == IssueConnectorKind.GitLab ? "PRIVATE-TOKEN" : "Authorization", c.Kind == IssueConnectorKind.GitLab ? token : "Bearer " + token);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.Value.TimeoutSeconds, 1, 60)));
        try
        {
            using var response = await clients.CreateClient(ConnectorHttp.ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status == 404)
            {
                return Result<IssueDto>.Failure(IssueNotFound);
            }

            if (status is 401 or 403)
            {
                return Result<IssueDto>.Failure(Denied);
            }

            if (status is < 200 or >= 300)
            {
                return Result<IssueDto>.Failure(Unreachable);
            }

            var body = await ReadLimitedAsync(response, cts.Token).ConfigureAwait(false);
            return body is null ? Result<IssueDto>.Failure(BadResponse) : Parse(c, reference, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result<IssueDto>.Failure(Unreachable);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || ex.InnerException is WebhookException)
        {
            return Result<IssueDto>.Failure(Unreachable);
        }
    }

    private static async Task<byte[]?> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            return null;
        }

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var ms = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + read > MaxResponseBytes)
                {
                    return null;
                }

                ms.Write(buffer, 0, read);
            }

            return ms.ToArray();
        }
    }

    private static Result<IssueDto> Parse(IssueConnector c, string reference, byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string? title;
            string? status;
            string? url;
            var type = "issue";
            switch (c.Kind)
            {
                case IssueConnectorKind.GitHub:
                    title = Str(root, "title");
                    status = Str(root, "state");
                    url = Str(root, "html_url");
                    if (root.TryGetProperty("pull_request", out var pr) && pr.ValueKind == JsonValueKind.Object)
                    {
                        type = "pull_request";
                        if (pr.TryGetProperty("merged_at", out var merged) && merged.ValueKind == JsonValueKind.String)
                        {
                            status = "merged";
                        }
                    }

                    break;
                case IssueConnectorKind.GitLab:
                    title = Str(root, "title");
                    status = Str(root, "state");
                    url = Str(root, "web_url");
                    break;
                default:
                    var fields = root.TryGetProperty("fields", out var f) ? f : default;
                    title = fields.ValueKind == JsonValueKind.Object ? Str(fields, "summary") : null;
                    status = fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object ? Str(st, "name") : null;
                    url = Str(root, "key") is { } key ? c.BaseUrl + "/browse/" + key : null;
                    break;
            }

            if (title is null || status is null || url is null || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            {
                return Result<IssueDto>.Failure(BadResponse);
            }

            return Result<IssueDto>.Success(new IssueDto(reference, Clean(title, 300), Clean(status, 60), u.ToString(), type));
        }
        catch (JsonException)
        {
            return Result<IssueDto>.Failure(BadResponse);
        }
    }

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Clean(string s, int max)
    {
        var sb = new StringBuilder(Math.Min(s.Length, max));
        foreach (var ch in s)
        {
            if (sb.Length >= max)
            {
                break;
            }

            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0 && sb[^1] != ' ')
                {
                    sb.Append(' ');
                }
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString().Trim();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid Tenant, Guid User), (DateTimeOffset Start, int Count)> Windows = new();

    private bool TakeQuota(Guid tenantId, Guid userId)
    {
        var now = clock.UtcNow;
        var updated = Windows.AddOrUpdate(
            (tenantId, userId),
            _ => (now, 1),
            (_, w) => now - w.Start >= TimeSpan.FromHours(1) ? (now, 1) : (w.Start, w.Count + 1));
        return updated.Count <= LookupsPerUserPerHour;
    }
}
