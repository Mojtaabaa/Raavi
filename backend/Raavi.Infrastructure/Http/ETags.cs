using System.Globalization;
using Microsoft.AspNetCore.Http;
using Raavi.Application.Common;

namespace Raavi.Infrastructure.Http;

/// <summary>
/// Optimistic concurrency over HTTP (API_CONVENTIONS.md §3): versioned aggregates expose a monotonically increasing
/// revision as a strong <c>ETag</c> (<c>"r12"</c>); updates must send it back in <c>If-Match</c>.
/// Missing header → 428, mismatch → 412 (checked by the application service against the stored revision).
/// </summary>
public static class ETags
{
    public static readonly Error PreconditionRequired = new("precondition.required", "This request requires an If-Match header.", ErrorKind.PreconditionRequired);

    public static readonly Error PreconditionFailed = new("precondition.failed", "The resource was changed by someone else. Reload and try again.", ErrorKind.PreconditionFailed);

    public static string Format(int revision) => "\"r" + revision.ToString(CultureInfo.InvariantCulture) + "\"";

    /// <summary>Parses <c>If-Match</c>. Only a single strong tag produced by <see cref="Format"/> is accepted.</summary>
    public static Result<int> RequireIfMatch(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var raw = request.Headers.IfMatch.ToString().Trim();
        if (raw.Length == 0)
        {
            return Result<int>.Failure(PreconditionRequired);
        }

        // "*" = any current representation; only operations that document it accept -1 (others treat it as stale).
        if (raw == "*")
        {
            return Result<int>.Success(-1);
        }

        if (raw.Length > 3 && raw[0] == '"' && raw[^1] == '"' && raw[1] == 'r'
            && int.TryParse(raw.AsSpan(2, raw.Length - 3), NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
        {
            return Result<int>.Success(revision);
        }

        return Result<int>.Failure(PreconditionFailed);
    }

    public static void Set(HttpResponse response, int revision)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Headers.ETag = Format(revision);
    }

    /// <summary>200 with the body and its ETag.</summary>
    public static IResult OkWithETag<T>(HttpContext http, T body, int revision)
    {
        ArgumentNullException.ThrowIfNull(http);
        Set(http.Response, revision);
        return Results.Ok(body);
    }
}
