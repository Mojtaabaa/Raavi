using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Raavi.Infrastructure.Http;

/// <summary>What kind of host served the request, set by tenant resolution (API_CONVENTIONS.md §2).</summary>
public enum RaaviHostKind
{
    System = 1,
    Tenant = 2,
}

public sealed record RaaviHostFeature(RaaviHostKind Kind);

/// <summary>Endpoint metadata: the endpoint exists only on hosts of this kind.</summary>
public sealed record RequiredHostKindMetadata(RaaviHostKind Kind);

public static class HostKindExtensions
{
    public static RaaviHostKind? GetHostKind(this HttpContext context) =>
        context?.Features.Get<RaaviHostFeature>()?.Kind;

    /// <summary>
    /// Endpoints on the wrong host type answer 404, as if they did not exist. Enforced by
    /// <see cref="UseRaaviHostKindEnforcement"/> <b>before</b> authentication/authorization, so a wrong host never
    /// reveals the endpoint through a 401/403.
    /// </summary>
    public static TBuilder RequireHostKind<TBuilder>(this TBuilder builder, RaaviHostKind kind)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new RequiredHostKindMetadata(kind));
        return builder;
    }

    /// <summary>Must run after routing and tenant resolution, before rate limiting, authentication and authorization.</summary>
    public static IApplicationBuilder UseRaaviHostKindEnforcement(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var required = context.GetEndpoint()?.Metadata.GetMetadata<RequiredHostKindMetadata>();
            if (required is not null && context.GetHostKind() != required.Kind)
            {
                await Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not Found").ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        });
}
