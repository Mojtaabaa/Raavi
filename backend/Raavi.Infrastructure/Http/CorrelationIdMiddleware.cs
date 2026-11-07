using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Raavi.Infrastructure.Http;

/// <summary>
/// Accepts a caller-supplied X-Correlation-ID only when it is short and made of safe characters (prevents log injection);
/// otherwise generates one. The id becomes HttpContext.TraceIdentifier, a logging scope value and a response header.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var incoming = context.Request.Headers[HeaderName].ToString();
        var correlationId = IsAcceptable(incoming) ? incoming : Guid.NewGuid().ToString("N");

        context.TraceIdentifier = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context).ConfigureAwait(false);
        }
    }

    public static bool IsAcceptable(string? value) => !string.IsNullOrEmpty(value) && SafeId().IsMatch(value);

    [GeneratedRegex("^[A-Za-z0-9._-]{8,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeId();
}
