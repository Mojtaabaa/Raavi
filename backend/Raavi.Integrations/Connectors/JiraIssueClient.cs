using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Application.Common;
using Raavi.Integrations.Persistence;
using Raavi.Integrations.Webhooks;

namespace Raavi.Integrations.Connectors;

public sealed record JiraProjectDto(string Id, string Key, string Name);

public sealed record JiraIssueTypeDto(string Id, string Name, bool Subtask);

public sealed record JiraFieldMetaDto(string FieldId, string Name, bool Required, string SchemaType, IReadOnlyList<JiraFieldOptionDto>? AllowedValues);

public sealed record JiraFieldOptionDto(string Id, string Name);

public sealed record JiraCreateMetaDto(string ProjectKey, string IssueTypeId, IReadOnlyList<JiraFieldMetaDto> Fields);

public sealed record JiraCreatedIssueDto(string Id, string Key, string Url);

public sealed record JiraSearchHitDto(string Id, string Key, string Summary, string Status, string IssueType, string? Priority, string? Assignee, string ProjectKey, string Url);

public sealed record JiraIssueDetailDto(string Id, string Key, string Summary, string Status, string IssueType, string? Priority, string? Assignee, string? Reporter, string ProjectKey, string Url, DateTimeOffset? Created, DateTimeOffset? Updated, DateTimeOffset? DueDate);

public sealed record JiraRemoteLinkDto(string? Id, string GlobalId);

/// <summary>Jira Data Center / Server REST API v2 write/read helpers used by Page–Issue linking. Reuses the connector HTTP client and SSRF policy.</summary>
public sealed class JiraIssueClient(
    IHttpClientFactory clients,
    ConnectorHostPolicy policy,
    IDataProtectionProvider protection,
    IOptions<IntegrationsOptions> options,
    ITenantContext tenant)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task<Result<IReadOnlyList<JiraProjectDto>>> ProjectsAsync(IssueConnector connector, CancellationToken ct)
    {
        var raw = await SendAsync(connector, HttpMethod.Get, "rest/api/2/project", null, ct).ConfigureAwait(false);
        if (!raw.IsSuccess)
        {
            return Result<IReadOnlyList<JiraProjectDto>>.Failure(raw.Error!);
        }

        try
        {
            using var doc = JsonDocument.Parse(raw.Value);
            var list = new List<JiraProjectDto>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var id = Str(el, "id");
                var key = Str(el, "key");
                var name = Str(el, "name") ?? key;
                if (id is not null && key is not null)
                {
                    list.Add(new JiraProjectDto(id, key, name ?? key));
                }
            }

            return Result<IReadOnlyList<JiraProjectDto>>.Success(list);
        }
        catch (JsonException)
        {
            return Result<IReadOnlyList<JiraProjectDto>>.Failure(IssueLookupService.BadResponse);
        }
    }

    public async Task<Result<JiraCreateMetaDto>> CreateMetaAsync(IssueConnector connector, string projectKey, string issueTypeId, CancellationToken ct)
    {
        var path = $"rest/api/2/issue/createmeta?projectKeys={Uri.EscapeDataString(projectKey)}&issuetypeIds={Uri.EscapeDataString(issueTypeId)}&expand=projects.issuetypes.fields";
        var raw = await SendAsync(connector, HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        if (!raw.IsSuccess)
        {
            return Result<JiraCreateMetaDto>.Failure(raw.Error!);
        }

        try
        {
            using var doc = JsonDocument.Parse(raw.Value);
            if (!doc.RootElement.TryGetProperty("projects", out var projects) || projects.GetArrayLength() == 0)
            {
                return Result<JiraCreateMetaDto>.Failure(new Error("jira.project_not_found", "The Jira project was not found or is not creatable with this token.", ErrorKind.NotFound));
            }

            var project = projects[0];
            if (!project.TryGetProperty("issuetypes", out var types) || types.GetArrayLength() == 0)
            {
                return Result<JiraCreateMetaDto>.Failure(new Error("jira.issuetype_not_found", "The issue type was not found for this project.", ErrorKind.NotFound));
            }

            var type = types[0];
            var fields = new List<JiraFieldMetaDto>();
            if (type.TryGetProperty("fields", out var fieldMap) && fieldMap.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in fieldMap.EnumerateObject())
                {
                    var f = prop.Value;
                    var required = f.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.True;
                    var name = Str(f, "name") ?? prop.Name;
                    var schemaType = f.TryGetProperty("schema", out var schema) ? Str(schema, "type") ?? "unknown" : "unknown";
                    List<JiraFieldOptionDto>? allowed = null;
                    if (f.TryGetProperty("allowedValues", out var av) && av.ValueKind == JsonValueKind.Array)
                    {
                        allowed = [];
                        foreach (var opt in av.EnumerateArray())
                        {
                            var oid = Str(opt, "id") ?? Str(opt, "value") ?? Str(opt, "name");
                            var oname = Str(opt, "name") ?? Str(opt, "value") ?? oid;
                            if (oid is not null)
                            {
                                allowed.Add(new JiraFieldOptionDto(oid, oname ?? oid));
                            }
                        }
                    }

                    fields.Add(new JiraFieldMetaDto(prop.Name, name, required, schemaType, allowed));
                }
            }

            return Result<JiraCreateMetaDto>.Success(new JiraCreateMetaDto(projectKey, issueTypeId, fields));
        }
        catch (JsonException)
        {
            return Result<JiraCreateMetaDto>.Failure(IssueLookupService.BadResponse);
        }
    }

    public async Task<Result<IReadOnlyList<JiraIssueTypeDto>>> IssueTypesAsync(IssueConnector connector, string projectKey, CancellationToken ct)
    {
        var path = $"rest/api/2/issue/createmeta?projectKeys={Uri.EscapeDataString(projectKey)}&expand=projects.issuetypes";
        var raw = await SendAsync(connector, HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        if (!raw.IsSuccess)
        {
            return Result<IReadOnlyList<JiraIssueTypeDto>>.Failure(raw.Error!);
        }

        try
        {
            using var doc = JsonDocument.Parse(raw.Value);
            var list = new List<JiraIssueTypeDto>();
            if (doc.RootElement.TryGetProperty("projects", out var projects) && projects.GetArrayLength() > 0
                && projects[0].TryGetProperty("issuetypes", out var types))
            {
                foreach (var t in types.EnumerateArray())
                {
                    var id = Str(t, "id");
                    var name = Str(t, "name");
                    if (id is not null && name is not null)
                    {
                        list.Add(new JiraIssueTypeDto(id, name, t.TryGetProperty("subtask", out var st) && st.ValueKind == JsonValueKind.True));
                    }
                }
            }

            return Result<IReadOnlyList<JiraIssueTypeDto>>.Success(list);
        }
        catch (JsonException)
        {
            return Result<IReadOnlyList<JiraIssueTypeDto>>.Failure(IssueLookupService.BadResponse);
        }
    }

    public async Task<Result<JiraCreatedIssueDto>> CreateIssueAsync(IssueConnector connector, JsonElement fields, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("fields");
            fields.WriteTo(writer);
            writer.WriteEndObject();
        }

        var raw = await SendAsync(connector, HttpMethod.Post, "rest/api/2/issue", Encoding.UTF8.GetString(stream.ToArray()), ct).ConfigureAwait(false);
        if (!raw.IsSuccess)
        {
            return Result<JiraCreatedIssueDto>.Failure(raw.Error!);
        }

        try
        {
            using var doc = JsonDocument.Parse(raw.Value);
            var id = Str(doc.RootElement, "id");
            var key = Str(doc.RootElement, "key");
            if (id is null || key is null)
            {
                return Result<JiraCreatedIssueDto>.Failure(IssueLookupService.BadResponse);
            }

            return Result<JiraCreatedIssueDto>.Success(new JiraCreatedIssueDto(id, key, $"{connector.BaseUrl.TrimEnd('/')}/browse/{key}"));
        }
        catch (JsonException)
        {
            return Result<JiraCreatedIssueDto>.Failure(IssueLookupService.BadResponse);
        }
    }

    public async Task<Result<IReadOnlyList<JiraSearchHitDto>>> SearchAsync(IssueConnector connector, string query, CancellationToken ct)
    {
        var q = (query ?? string.Empty).Trim();
        if (q.Length is 0 or > 500)
        {
            return Result<IReadOnlyList<JiraSearchHitDto>>.Failure(new Error("jira.search_invalid", "Enter a Jira key or search text.", ErrorKind.Validation));
        }

        string jql;
        if (IssueReference.ApiPath(IssueConnectorKind.Jira, q) is not null)
        {
            jql = $"key = \"{q.Replace("\"", string.Empty, StringComparison.Ordinal)}\"";
        }
        else if (q.StartsWith("jql:", StringComparison.OrdinalIgnoreCase))
        {
            jql = q[4..].Trim();
            if (jql.Length is 0 or > 1000)
            {
                return Result<IReadOnlyList<JiraSearchHitDto>>.Failure(new Error("jira.search_invalid", "The JQL query is not valid.", ErrorKind.Validation));
            }
        }
        else
        {
            var safe = q.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
            jql = $"summary ~ \"{safe}\" ORDER BY updated DESC";
        }

        var path = $"rest/api/2/search?jql={Uri.EscapeDataString(jql)}&maxResults=25&fields=summary,status,issuetype,priority,assignee,project";
        var raw = await SendAsync(connector, HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        if (!raw.IsSuccess)
        {
            return Result<IReadOnlyList<JiraSearchHitDto>>.Failure(raw.Error!);
        }

        try
        {
            using var doc = JsonDocument.Parse(raw.Value);
            var list = new List<JiraSearchHitDto>();
            if (doc.RootElement.TryGetProperty("issues", out var issues))
            {
                foreach (var issue in issues.EnumerateArray())
                {
                    if (ParseHit(connector, issue) is { } hit)
                    {
                        list.Add(hit);
                    }
                }
            }

            return Result<IReadOnlyList<JiraSearchHitDto>>.Success(list);
        }
        catch (JsonException)
        {
            return Result<IReadOnlyList<JiraSearchHitDto>>.Failure(IssueLookupService.BadResponse);
        }
    }

    public async Task<Result<JiraIssueDetailDto>> GetIssueAsync(IssueConnector connector, string issueIdOrKey, CancellationToken ct)
    {
        var path = $"rest/api/2/issue/{Uri.EscapeDataString(issueIdOrKey)}?fields=summary,status,issuetype,priority,assignee,reporter,project,created,updated,duedate";
        var raw = await SendAsync(connector, HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        if (!raw.IsSuccess)
        {
            return Result<JiraIssueDetailDto>.Failure(raw.Error!);
        }

        try
        {
            using var doc = JsonDocument.Parse(raw.Value);
            var hit = ParseHit(connector, doc.RootElement);
            if (hit is null)
            {
                return Result<JiraIssueDetailDto>.Failure(IssueLookupService.BadResponse);
            }

            var fields = doc.RootElement.GetProperty("fields");
            DateTimeOffset? created = ParseTime(fields, "created");
            DateTimeOffset? updated = ParseTime(fields, "updated");
            DateTimeOffset? due = null;
            if (fields.TryGetProperty("duedate", out var d) && d.ValueKind == JsonValueKind.String && DateOnly.TryParse(d.GetString(), out var day))
            {
                due = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            }

            var reporter = fields.TryGetProperty("reporter", out var rep) ? Str(rep, "displayName") : null;
            return Result<JiraIssueDetailDto>.Success(new JiraIssueDetailDto(
                hit.Id, hit.Key, hit.Summary, hit.Status, hit.IssueType, hit.Priority, hit.Assignee, reporter, hit.ProjectKey, hit.Url, created, updated, due));
        }
        catch (JsonException)
        {
            return Result<JiraIssueDetailDto>.Failure(IssueLookupService.BadResponse);
        }
    }

    public async Task<Result<JiraRemoteLinkDto>> UpsertRemoteLinkAsync(
        IssueConnector connector,
        string issueIdOrKey,
        string globalId,
        string title,
        string url,
        CancellationToken ct)
    {
        var existing = await FindRemoteLinkAsync(connector, issueIdOrKey, globalId, ct).ConfigureAwait(false);
        if (!existing.IsSuccess)
        {
            return Result<JiraRemoteLinkDto>.Failure(existing.Error!);
        }

        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["globalId"] = globalId,
            ["application"] = new Dictionary<string, string> { ["type"] = "com.raavi.wiki", ["name"] = "Raavi" },
            ["object"] = new Dictionary<string, object?>
            {
                ["url"] = url,
                ["title"] = title,
                ["icon"] = new Dictionary<string, string> { ["title"] = "Raavi" },
            },
        });

        if (existing.Value is { Id: { } rid })
        {
            var put = await SendAsync(connector, HttpMethod.Put, $"rest/api/2/issue/{Uri.EscapeDataString(issueIdOrKey)}/remotelink/{Uri.EscapeDataString(rid)}", payload, ct).ConfigureAwait(false);
            return put.IsSuccess
                ? Result<JiraRemoteLinkDto>.Success(new JiraRemoteLinkDto(rid, globalId))
                : Result<JiraRemoteLinkDto>.Failure(put.Error!);
        }

        var post = await SendAsync(connector, HttpMethod.Post, $"rest/api/2/issue/{Uri.EscapeDataString(issueIdOrKey)}/remotelink", payload, ct).ConfigureAwait(false);
        if (!post.IsSuccess)
        {
            return Result<JiraRemoteLinkDto>.Failure(post.Error!);
        }

        try
        {
            using var doc = JsonDocument.Parse(post.Value);
            return Result<JiraRemoteLinkDto>.Success(new JiraRemoteLinkDto(Str(doc.RootElement, "id"), globalId));
        }
        catch (JsonException)
        {
            return Result<JiraRemoteLinkDto>.Success(new JiraRemoteLinkDto(null, globalId));
        }
    }

    public async Task<Result> DeleteRemoteLinkAsync(IssueConnector connector, string issueIdOrKey, string remoteLinkId, CancellationToken ct)
    {
        var del = await SendAsync(connector, HttpMethod.Delete, $"rest/api/2/issue/{Uri.EscapeDataString(issueIdOrKey)}/remotelink/{Uri.EscapeDataString(remoteLinkId)}", null, ct).ConfigureAwait(false);
        return del.IsSuccess || del.Error!.Code == "issue.not_found" ? Result.Success() : Result.Failure(del.Error!);
    }

    private async Task<Result<JiraRemoteLinkDto?>> FindRemoteLinkAsync(IssueConnector connector, string issueIdOrKey, string globalId, CancellationToken ct)
    {
        var raw = await SendAsync(connector, HttpMethod.Get, $"rest/api/2/issue/{Uri.EscapeDataString(issueIdOrKey)}/remotelink", null, ct).ConfigureAwait(false);
        if (!raw.IsSuccess)
        {
            return Result<JiraRemoteLinkDto?>.Failure(raw.Error!);
        }

        try
        {
            using var doc = JsonDocument.Parse(raw.Value);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (string.Equals(Str(el, "globalId"), globalId, StringComparison.Ordinal))
                {
                    return Result<JiraRemoteLinkDto?>.Success(new JiraRemoteLinkDto(Str(el, "id"), globalId));
                }
            }

            return Result<JiraRemoteLinkDto?>.Success(null);
        }
        catch (JsonException)
        {
            return Result<JiraRemoteLinkDto?>.Failure(IssueLookupService.BadResponse);
        }
    }

    private async Task<Result<string>> SendAsync(IssueConnector connector, HttpMethod method, string path, string? jsonBody, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connector);
        if (connector.Kind != IssueConnectorKind.Jira)
        {
            return Result<string>.Failure(new Error("jira.connector_kind", "Only a Jira connector can perform this operation.", ErrorKind.Validation));
        }

        if (!connector.Enabled)
        {
            return Result<string>.Failure(IssueLookupService.Disabled);
        }

        if (policy.TryValidate(connector.BaseUrl, out _) is null)
        {
            return Result<string>.Failure(IssueLookupService.Unreachable);
        }

        string? token = null;
        if (connector.ProtectedToken is not null)
        {
            try
            {
                token = protection.CreateProtector(IssueConnectorService.ProtectorPurpose(tenant.Tenant.TenantId)).Unprotect(connector.ProtectedToken);
            }
            catch (CryptographicException)
            {
                return Result<string>.Failure(IssueLookupService.Denied);
            }
        }

        if (token is null)
        {
            return Result<string>.Failure(new Error("jira.token_required", "Creating or linking Jira issues needs a connector access token.", ErrorKind.Validation));
        }

        using var request = new HttpRequestMessage(method, new Uri(connector.BaseUrl.TrimEnd('/') + "/" + path.TrimStart('/')));
        request.Headers.TryAddWithoutValidation("User-Agent", "Raavi-Connectors/1");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.Value.TimeoutSeconds, 1, 60)));
        try
        {
            using var response = await clients.CreateClient(ConnectorHttp.ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status == 404)
            {
                return Result<string>.Failure(IssueLookupService.IssueNotFound);
            }

            if (status is 401 or 403)
            {
                return Result<string>.Failure(IssueLookupService.Denied);
            }

            if (status == 429)
            {
                return Result<string>.Failure(IssueLookupService.Limited);
            }

            if (status == 400)
            {
                var errBody = await ReadLimitedAsync(response, cts.Token).ConfigureAwait(false);
                var message = ExtractJiraError(errBody) ?? "Jira rejected the request.";
                return Result<string>.Failure(new Error("jira.validation", message, ErrorKind.Validation));
            }

            if (status is < 200 or >= 300)
            {
                return Result<string>.Failure(IssueLookupService.Unreachable);
            }

            if (method == HttpMethod.Delete || status == 204)
            {
                return Result<string>.Success("{}");
            }

            var body = await ReadLimitedAsync(response, cts.Token).ConfigureAwait(false);
            return body is null ? Result<string>.Failure(IssueLookupService.BadResponse) : Result<string>.Success(Encoding.UTF8.GetString(body));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result<string>.Failure(IssueLookupService.Unreachable);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || ex.InnerException is WebhookException)
        {
            return Result<string>.Failure(IssueLookupService.Unreachable);
        }
    }

    private static async Task<byte[]?> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > IssueLookupService.MaxResponseBytes)
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
                if (ms.Length + read > IssueLookupService.MaxResponseBytes)
                {
                    return null;
                }

                ms.Write(buffer, 0, read);
            }

            return ms.ToArray();
        }
    }

    private static string? ExtractJiraError(byte[]? body)
    {
        if (body is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in errors.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String)
                    {
                        return $"{p.Name}: {p.Value.GetString()}";
                    }
                }
            }

            if (doc.RootElement.TryGetProperty("errorMessages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in msgs.EnumerateArray())
                {
                    if (m.ValueKind == JsonValueKind.String)
                    {
                        return m.GetString();
                    }
                }
            }
        }
        catch (JsonException)
        {
            // ignore
        }

        return null;
    }

    private static JiraSearchHitDto? ParseHit(IssueConnector connector, JsonElement issue)
    {
        var id = Str(issue, "id");
        var key = Str(issue, "key");
        if (id is null || key is null || !issue.TryGetProperty("fields", out var fields))
        {
            return null;
        }

        var summary = Str(fields, "summary") ?? key;
        var status = fields.TryGetProperty("status", out var st) ? Str(st, "name") ?? "Unknown" : "Unknown";
        var type = fields.TryGetProperty("issuetype", out var it) ? Str(it, "name") ?? "Issue" : "Issue";
        var priority = fields.TryGetProperty("priority", out var pr) ? Str(pr, "name") : null;
        var assignee = fields.TryGetProperty("assignee", out var asg) && asg.ValueKind == JsonValueKind.Object ? Str(asg, "displayName") : null;
        var project = fields.TryGetProperty("project", out var pj) ? Str(pj, "key") ?? string.Empty : string.Empty;
        return new JiraSearchHitDto(id, key, summary, status, type, priority, assignee, project, $"{connector.BaseUrl.TrimEnd('/')}/browse/{key}");
    }

    private static DateTimeOffset? ParseTime(JsonElement fields, string name)
    {
        if (!fields.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(v.GetString(), out var dto) ? dto : null;
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
