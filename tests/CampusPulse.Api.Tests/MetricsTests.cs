using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace CampusPulse.Api.Tests;

/// <summary>
/// The metrics endpoint: only on the metrics port, only to loopback clients, never on the API's own port (the one
/// nginx proxies). Requests are handed over as Kestrel would, with the connection's local port and remote address set.
/// </summary>
public sealed class MetricsTests
{
    private const int MetricsPort = 9464;
    private const int ApiPort = 5093;

    private sealed class Factory(int? metricsPort, Dictionary<string, string>? settings = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Postgres",
                "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=2");
            builder.UseSetting("Reminders:Enabled", "false");
            if (metricsPort is not null)
            {
                builder.UseSetting("Metrics:Port", metricsPort.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            foreach (var (key, value) in settings ?? [])
            {
                builder.UseSetting(key, value);
            }
        }
    }

    private static async Task<(int Status, string Body)> Get(WebApplicationFactory<Program> factory, string path, int localPort, IPAddress remote)
    {
        var context = await factory.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = path;
            c.Connection.LocalPort = localPort;
            c.Connection.LocalIpAddress = IPAddress.Loopback;
            c.Connection.RemoteIpAddress = remote;
        });
        using var reader = new StreamReader(context.Response.Body);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task The_metrics_port_serves_request_and_rate_limit_metrics_to_a_local_scraper()
    {
        using var factory = new Factory(MetricsPort, new()
        {
            ["RateLimits:Anonymous:Burst"] = "1",
            ["RateLimits:Anonymous:PerMinute"] = "1",
        });
        Assert.Equal(StatusCodes.Status200OK, (await Get(factory, "/health", ApiPort, IPAddress.Loopback)).Status);
        Assert.Equal(StatusCodes.Status429TooManyRequests, (await Get(factory, "/health", ApiPort, IPAddress.Loopback)).Status);

        var (status, body) = await Get(factory, "/metrics", MetricsPort, IPAddress.Loopback);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Contains("http_server_request_duration_seconds_bucket{", body);
        Assert.Contains("http_route=\"/health\"", body);
        Assert.Contains("http_response_status_code=\"429\"", body);
        Assert.Contains("aspnetcore_rate_limiting_requests_total{", body);
        Assert.Contains("aspnetcore_rate_limiting_result=\"global_limiter\"", body);
        Assert.Contains("dotnet_gc_collections_total", body);
    }

    public static TheoryData<string, int, string> NotAScrape => new()
    {
        { "/metrics", ApiPort, "127.0.0.1" },        // the API's own port: what nginx reaches
        { "/metrics", MetricsPort, "192.168.0.10" },  // the metrics port, from another machine
        { "/health", MetricsPort, "127.0.0.1" },      // the metrics port serves nothing else
    };

    [Theory]
    [MemberData(nameof(NotAScrape))]
    public async Task Metrics_are_not_served_anywhere_else(string path, int localPort, string remote)
    {
        using var factory = new Factory(MetricsPort);

        var (status, body) = await Get(factory, path, localPort, IPAddress.Parse(remote));

        Assert.NotEqual(StatusCodes.Status200OK, status);
        Assert.DoesNotContain("http_server_request_duration", body);
    }

    [Fact]
    public async Task Without_a_metrics_port_there_is_no_metrics_endpoint()
    {
        using var factory = new Factory(metricsPort: null);

        var (status, body) = await Get(factory, "/metrics", MetricsPort, IPAddress.Loopback);

        Assert.NotEqual(StatusCodes.Status200OK, status);
        Assert.DoesNotContain("http_server_request_duration", body);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>On a real Kestrel server: with <c>Metrics:Port</c> set, the API adds a listener on 127.0.0.1 to the URLs
    /// it was given, and that listener answers <c>/metrics</c>; the API's own port doesn't.</summary>
    [Fact]
    public async Task The_app_listens_for_metrics_on_127_0_0_1_next_to_its_own_port()
    {
        var (apiPort, metricsPort) = (FreePort(), FreePort());
        using var factory = new Factory(metricsPort, new() { [WebHostDefaults.ServerUrlsKey] = $"http://127.0.0.1:{apiPort}" });
        factory.UseKestrel();
        factory.StartServer();
        using var http = new HttpClient();

        var scrape = await http.GetAsync($"http://127.0.0.1:{metricsPort}/metrics");
        var health = await http.GetAsync($"http://127.0.0.1:{apiPort}/health");
        var publicMetrics = await http.GetAsync($"http://127.0.0.1:{apiPort}/metrics");

        Assert.Equal(HttpStatusCode.OK, scrape.StatusCode);
        Assert.Equal(new MediaTypeHeaderValue("text/plain").MediaType, scrape.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, publicMetrics.StatusCode);
    }
}
