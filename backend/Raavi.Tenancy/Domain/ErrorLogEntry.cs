using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Raavi.Application.Common;
using Raavi.Domain.Common;
using Raavi.Infrastructure.Http;
using Raavi.Tenancy.Persistence;

namespace Raavi.Tenancy.Domain;

/// <summary>An error the application recorded (unhandled server exception, or an error reported by the browser). Global (system area), no personal content.</summary>
public sealed class ErrorLogEntry : Entity
{
    private ErrorLogEntry() { }

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary><c>server</c> or <c>client</c>.</summary>
    public string Source { get; private set; } = "server";

    public string? TenantCode { get; private set; }

    public Guid? UserId { get; private set; }

    public string? Method { get; private set; }

    public string? Path { get; private set; }

    public int? StatusCode { get; private set; }

    public string? ExceptionType { get; private set; }

    public string Message { get; private set; } = string.Empty;

    public string? StackTrace { get; private set; }

    public string? CorrelationId { get; private set; }

    public string? UserAgent { get; private set; }

    /// <summary>Hash of type + path + the first line of the message with numbers removed: identical errors share it, so they can be counted together.</summary>
    public string Fingerprint { get; private set; } = string.Empty;

    public static ErrorLogEntry From(ErrorReport r) => new()
    {
        OccurredAt = r.At,
        Source = r.Source,
        TenantCode = r.TenantCode,
        UserId = r.UserId,
        Method = r.Method,
        Path = r.Path is { Length: > 300 } p ? p[..300] : r.Path,
        StatusCode = r.StatusCode,
        ExceptionType = r.ExceptionType is { Length: > 200 } t ? t[..200] : r.ExceptionType,
        Message = r.Message,
        StackTrace = r.StackTrace,
        CorrelationId = r.CorrelationId is { Length: > 64 } c ? c[..64] : r.CorrelationId,
        UserAgent = r.UserAgent,
        Fingerprint = FingerprintOf(r),
    };

    public static string FingerprintOf(ErrorReport r)
    {
        var firstLine = (r.Message.Split('\n', 2)[0]);
        var shape = new string(firstLine.Where(ch => !char.IsDigit(ch)).ToArray());
        var route = string.Join('/', (r.Path ?? string.Empty).Split('/').Select(s => Guid.TryParse(s, out _) ? "{id}" : s));
        var raw = $"{r.Source}|{r.ExceptionType}|{route}|{shape}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
    }
}

