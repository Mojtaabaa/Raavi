using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Raavi.Identity.Security;

/// <summary>
/// CSRF protection for cookie-authenticated browser requests (AUTHENTICATION_ARCHITECTURE.md §8).
/// Every state-changing <c>/api</c> request must carry <c>X-XSRF-TOKEN</c> matching the antiforgery cookie — including
/// login and invitation acceptance (login CSRF). Requests authenticated with an <c>Authorization: Bearer</c> header
/// (operator API keys, later API tokens) are not ambient-credential requests and are exempt.
/// The SPA fetches a token from <c>GET /api/v1/auth/antiforgery</c> and again after every sign-in/out, because tokens
/// are bound to the authenticated identity.
/// </summary>
public static class AntiforgeryProtection
{
    public const string HeaderName = "X-XSRF-TOKEN";

    public static IApplicationBuilder UseRaaviAntiforgery(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (RequiresValidation(context.Request))
            {
                var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
                if (!await antiforgery.IsRequestValidAsync(context).ConfigureAwait(false))
                {
                    await Results.Problem(
                            statusCode: StatusCodes.Status400BadRequest,
                            title: "Invalid or missing anti-forgery token",
                            extensions: new Dictionary<string, object?> { ["code"] = "csrf.invalid", ["correlationId"] = context.TraceIdentifier })
                        .ExecuteAsync(context).ConfigureAwait(false);
                    return;
                }
            }

            await next(context).ConfigureAwait(false);
        });

    private static bool RequiresValidation(HttpRequest request) =>
        !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method))
        && request.Path.StartsWithSegments("/api", StringComparison.Ordinal)
        && !request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal);
}

