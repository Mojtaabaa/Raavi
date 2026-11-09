using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Raavi.Infrastructure.Http;

/// <summary>One error as the error log keeps it. Everything is already shortened and redacted by <see cref="ErrorText"/>.</summary>
public sealed record ErrorReport(
    DateTimeOffset At,
    string Source,
    string? TenantCode,
    Guid? UserId,
    string? Method,
    string? Path,
    int? StatusCode,
    string? ExceptionType,
    string Message,
    string? StackTrace,
    string? CorrelationId,
    string? UserAgent);

/// <summary>Where reported errors go (the Tenancy module stores them; hosts without it simply drop them).</summary>
public interface IErrorSink
{
    void Report(ErrorReport report);
}

/// <summary>Cuts and cleans error text before it is stored: secrets in connection strings and tokens never reach the log.</summary>
public static partial class ErrorText
{
    public const int MaxMessage = 1000;
    public const int MaxStack = 6000;

    public static string Clean(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = Secret().Replace(value, "$1=***");
        text = Bearer().Replace(text, "Bearer ***");
        return text.Length <= max ? text : text[..max] + "…";
    }

    [GeneratedRegex(@"(?i)\b(password|pwd|secret|token|apikey|api_key|authorization|user id|uid)\s*=\s*[^;,\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex Secret();

    [GeneratedRegex(@"(?i)bearer\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex Bearer();
}

/// <summary>Records every unhandled exception of a request and then lets the normal problem-details handler answer (it never handles the exception).</summary>
internal sealed class UnhandledExceptionLogger : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        if (exception is OperationCanceledException || exception is Raavi.Domain.Common.DomainException || exception is Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            return ValueTask.FromResult(false); // expected outcomes, not errors
        }

        try
        {
            var sink = httpContext.RequestServices.GetService<IErrorSink>();
            if (sink is not null)
            {
                string? tenant = null;
                try
                {
                    var context = httpContext.RequestServices.GetService<Raavi.Application.Abstractions.Tenancy.ITenantContext>();
                    tenant = context is { IsResolved: true } ? context.Tenant.Code : null;
                }
                catch (Exception)
                {
                    // no tenant for this request
                }

                Guid? user = null;
                var sub = httpContext.User.FindFirst("sub")?.Value ?? httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (Guid.TryParse(sub, out var parsed))
                {
                    user = parsed;
                }

                sink.Report(new ErrorReport(
                    DateTimeOffset.UtcNow,
                    "server",
                    tenant,
                    user,
                    httpContext.Request.Method,
                    httpContext.Request.Path.Value,
                    StatusCodes.Status500InternalServerError,
                    exception.GetType().FullName,
                    ErrorText.Clean(exception.Message, ErrorText.MaxMessage),
                    ErrorText.Clean(exception.ToString(), ErrorText.MaxStack),
                    httpContext.TraceIdentifier,
                    ErrorText.Clean(httpContext.Request.Headers.UserAgent.ToString(), 200)));
            }
        }
        catch (Exception)
        {
            // logging an error must never make the answer worse
        }

        return ValueTask.FromResult(false);
    }
}

public static class ErrorReportingExtensions
{
    public static IServiceCollection AddRaaviErrorReporting(this IServiceCollection services)
    {
        services.AddExceptionHandler<UnhandledExceptionLogger>();
        return services;
    }
}
