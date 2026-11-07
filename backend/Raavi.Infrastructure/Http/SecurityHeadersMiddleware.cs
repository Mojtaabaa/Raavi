using Microsoft.AspNetCore.Http;

namespace Raavi.Infrastructure.Http;

/// <summary>
/// Baseline security headers for API responses (docs/architecture/SECURITY_ARCHITECTURE.md §6).
/// The SPA is served by the reverse proxy with its own CSP; API responses never render HTML, so they get a deny-all CSP.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            // Endpoints may set a stricter policy (e.g. attachment downloads add 'sandbox'); never a weaker default.
            if (string.IsNullOrEmpty(headers.ContentSecurityPolicy))
            {
                headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            }
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            return Task.CompletedTask;
        });

        return next(context);
    }
}
