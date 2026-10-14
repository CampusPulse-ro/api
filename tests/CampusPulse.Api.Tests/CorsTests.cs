using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CampusPulse.Api.Tests;

public sealed class CorsOriginsTests
{
    private static readonly string[] Allowed =
    [
        "https://campuspulse.thorsp.net",
        "https://*.campuspulse-web.pages.dev",
        "http://localhost:5173",
    ];

    [Theory]
    [InlineData("https://campuspulse.thorsp.net")]
    [InlineData("https://CAMPUSPULSE.thorsp.net")]
    [InlineData("https://staging.campuspulse-web.pages.dev")]
    [InlineData("https://feat-12-login.campuspulse-web.pages.dev")]
    [InlineData("https://abc123.feat-12.campuspulse-web.pages.dev")]
    [InlineData("http://localhost:5173")]
    public void Allows_listed_origins_and_preview_subdomains(string origin) =>
        Assert.True(CorsOrigins.IsAllowed(origin, Allowed));

    [Theory]
    [InlineData("http://campuspulse.thorsp.net")]            // wrong scheme
    [InlineData("https://campuspulse.thorsp.net.evil.com")]  // suffix trick
    [InlineData("https://evil-campuspulse-web.pages.dev")]   // look-alike, not a subdomain
    [InlineData("https://campuspulse-web.pages.dev")]        // the wildcard's own apex
    [InlineData("https://staging.campuspulse-web.pages.dev:8443")] // a port on a wildcard
    [InlineData("http://localhost:3000")]                    // other port
    [InlineData("null")]
    [InlineData("")]
    public void Refuses_everything_else(string origin) =>
        Assert.False(CorsOrigins.IsAllowed(origin, Allowed));

    [Fact]
    public void No_configuration_allows_nothing() =>
        Assert.False(CorsOrigins.IsAllowed("https://campuspulse.thorsp.net", []));
}

public sealed class CorsHttpTests
{
    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Postgres",
                "Host=localhost;Database=unused;Username=unused;Password=unused");
            builder.UseSetting("Cors:AllowedOrigins:0", "https://*.campuspulse-web.pages.dev");
        }
    }

    [Fact]
    public async Task Preflight_from_a_preview_is_allowed_and_a_stranger_is_not()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();

        var ok = await client.SendAsync(Preflight("https://feat-x.campuspulse-web.pages.dev"));
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.Equal("https://feat-x.campuspulse-web.pages.dev",
            ok.Headers.GetValues("Access-Control-Allow-Origin").Single());

        var refused = await client.SendAsync(Preflight("https://example.com"));
        Assert.False(refused.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task An_allowed_origin_can_read_the_request_id()
    {
        using var factory = new Factory();
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("Origin", "https://feat-x.campuspulse-web.pages.dev");

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Contains("X-Request-Id", response.Headers.GetValues("Access-Control-Expose-Headers"));
    }

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/health");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        return request;
    }
}
