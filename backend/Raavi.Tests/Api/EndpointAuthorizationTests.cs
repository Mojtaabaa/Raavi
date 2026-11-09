using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Raavi.Tests.Infrastructure;
using Xunit.Abstractions;

namespace Raavi.Tests.Api;

/// <summary>
/// ASVS V4 guard (Phase 12e): every HTTP endpoint the API exposes must require authorization, except an explicit, reviewed list of anonymous ones.
/// A new endpoint that forgets <c>RequireAuthorization</c> fails here instead of shipping open.
/// </summary>
[Collection(SqlTestGroup.Name)]
public sealed class EndpointAuthorizationTests(SqlServerFixture fx, ITestOutputHelper output)
{
    // Anonymous on purpose. Add to this list only after reviewing why the endpoint cannot require a session.
    private static readonly string[] AnonymousAllowList =
    [
        "* /health/live", "* /health/ready", "GET /health/status",
        // SPA bootstrap and login-page discovery (rate limited, expose only public tenant information)
        "GET /api/v1/host/context", "GET /api/v1/host/logo", "GET /api/v1/discovery/tenants/{code}",
        // JSON 404 for unknown API paths
        "* /api/{**path}",
        // OpenAPI document: mapped only when the environment is Development (Program.cs)
        "GET /openapi/{documentName}.json",
        // sign-in, sign-up, password reset and external login start/callback
        "GET /api/v1/auth/antiforgery",
        "GET /api/v1/auth/callback/{providerId}",
        "GET /api/v1/auth/providers",
        "POST /api/v1/errors/client",
        "GET /api/v1/auth/system/options",
        "GET /api/v1/auth/relay/{providerId}",
        "GET /api/v1/auth/relay/complete",
        "POST /api/v1/auth/invitations/accept",
        "POST /api/v1/auth/invitations/inspect",
        "POST /api/v1/auth/login/{providerId}/begin",
        "POST /api/v1/auth/login/credentials/{providerId}",
        "POST /api/v1/auth/login/local",
        "POST /api/v1/auth/logout",
        "POST /api/v1/auth/system/first-login",
        "POST /api/v1/auth/system/login",
        "POST /api/v1/auth/system/setup/begin",
        "POST /api/v1/auth/system/setup/complete",
        "POST /api/v1/auth/system/setup/inspect",
        // collaboration server -> API calls, authenticated by the HMAC signature middleware (it covers the whole /internal/collaboration prefix; jwks is the public key)
        "GET /internal/collaboration/documents/{name}/state",
        "GET /internal/collaboration/jwks",
        "POST /internal/collaboration/check",
        "POST /internal/collaboration/documents/{name}/snapshot",
        "POST /internal/collaboration/documents/{name}/updates",
    ];

    private static bool IsAllowed(string verb, string pattern) =>
        AnonymousAllowList.Any(a => a == verb + " " + pattern || a == "* " + pattern);

    [Fact]
    public void Every_endpoint_requires_authorization_unless_it_is_on_the_reviewed_anonymous_list()
    {
        using var factory = new ApiFactory(fx);
        var source = factory.Services.GetRequiredService<EndpointDataSource>();
        var open = new List<string>();
        var total = 0;
        foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
        {
            var pattern = "/" + endpoint.RoutePattern.RawText?.TrimStart('/');
            var verbs = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"];
            total++;
            var secured = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any();
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            foreach (var verb in verbs)
            {
                if ((!secured || anonymous) && !IsAllowed(verb, pattern))
                {
                    open.Add($"{verb} {pattern}{(anonymous ? " [AllowAnonymous]" : " [no authorization metadata]")}");
                }
            }
        }

        output.WriteLine($"{total} endpoints checked");
        Assert.True(total > 100, "the endpoint list looks empty");
        Assert.True(open.Count == 0, "Endpoints without required authorization:\n" + string.Join("\n", open.Order()));
    }
}
