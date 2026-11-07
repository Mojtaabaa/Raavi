using System.Net;
using Raavi.Tests.Infrastructure;
using Raavi.Infrastructure.Http;

namespace Raavi.Tests.Api;

[Collection(SqlTestGroup.Name)]
public sealed class HttpFoundationTests(SqlServerFixture fx)
{
    [Fact]
    public async Task Liveness_and_readiness_report_healthy()
    {
        using var client = fx.Api.ForHost("localhost");

        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task Safe_correlation_id_is_echoed()
    {
        using var client = fx.Api.ForHost("localhost");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "test-corr-0001");

        using var response = await client.SendAsync(request);

        Assert.Equal("test-corr-0001", response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single());
    }

    [Fact]
    public async Task Unsafe_correlation_id_is_replaced()
    {
        using var client = fx.Api.ForHost("localhost");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, "<script>alert(1)</script>");

        using var response = await client.SendAsync(request);

        var id = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        Assert.NotEqual("<script>alert(1)</script>", id);
        Assert.Matches("^[0-9a-f]{32}$", id);
    }

    [Fact]
    public async Task Security_headers_are_present()
    {
        using var client = fx.Api.ForHost("localhost");

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_api_route_returns_problem_details_with_correlation_id()
    {
        using var client = fx.Api.ForHost("localhost");

        using var response = await client.GetAsync(new Uri("/api/v1/does-not-exist", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("correlationId", body, StringComparison.Ordinal);
    }
}
