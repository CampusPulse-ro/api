using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CampusPulse.Api.Tests;

/// <summary>Security headers on every answer, HSTS behind nginx, and compression; none of it needs a database.</summary>
public sealed class SecurityHeadersTests(CampusPulseAppFactory factory) : IClassFixture<CampusPulseAppFactory>
{
    public static TheoryData<string, HttpStatusCode> Answers => new()
    {
        { "/health", HttpStatusCode.OK },
        { "/openapi/v1.json", HttpStatusCode.OK },
        { "/me", HttpStatusCode.Unauthorized },
        { "/no/such/path", HttpStatusCode.Unauthorized },
    };

    [Theory]
    [MemberData(nameof(Answers))]
    public async Task Every_answer_tells_browsers_not_to_sniff_frame_refer_or_store_it(string path, HttpStatusCode status)
    {
        var response = await factory.CreateClient().GetAsync(path);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("default-src 'none'; frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.True(response.Headers.CacheControl?.NoStore, $"{path}: Cache-Control {response.Headers.CacheControl}");
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));   // plain HTTP: no HSTS
    }

    /// <summary>Behind nginx every request is HTTPS (X-Forwarded-Proto): those answers carry HSTS for a year.</summary>
    [Fact]
    public async Task Https_through_the_local_proxy_gets_HSTS()
    {
        var context = await factory.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/health";
            c.Request.Host = new HostString("campuspulse-api.example");
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            c.Request.Headers["X-Forwarded-Proto"] = "https";
            c.Request.Headers["X-Forwarded-For"] = "203.0.113.7";
        });

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("max-age=31536000", context.Response.Headers.StrictTransportSecurity.ToString());
    }

    [Theory]
    [InlineData("br")]
    [InlineData("gzip")]
    public async Task Answers_are_compressed_when_the_client_accepts_it_HTTPS_included(string encoding)
    {
        // As nginx passes it on: HTTPS, with or without the client's Accept-Encoding. (The document names the server
        // with the request's scheme, so both requests are HTTPS.)
        Task<HttpContext> Fetch(string? acceptEncoding) => factory.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/openapi/v1.json";
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            c.Request.Headers["X-Forwarded-Proto"] = "https";
            if (acceptEncoding is not null)
            {
                c.Request.Headers.AcceptEncoding = acceptEncoding;
            }
        });
        var identity = await Fetch(null);
        using var plainBody = new MemoryStream();
        await identity.Response.Body.CopyToAsync(plainBody);
        var plain = plainBody.ToArray();

        var context = await Fetch(encoding);

        Assert.Empty(identity.Response.Headers.ContentEncoding.ToString());
        Assert.Equal(encoding, context.Response.Headers.ContentEncoding.ToString());
        Assert.Contains("Accept-Encoding", context.Response.Headers.Vary.ToString());
        using var compressed = new MemoryStream();
        await context.Response.Body.CopyToAsync(compressed);
        Assert.True(compressed.Length < plain.Length / 3, $"{encoding}: {compressed.Length} of {plain.Length} bytes");
        compressed.Position = 0;
        await using Stream decoder = encoding == "br"
            ? new BrotliStream(compressed, CompressionMode.Decompress)
            : new GZipStream(compressed, CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        await decoder.CopyToAsync(decoded);
        Assert.Equal(plain, decoded.ToArray());
    }

    [Fact]
    public async Task Without_Accept_Encoding_the_answer_is_not_compressed()
    {
        var response = await factory.CreateClient().GetAsync("/openapi/v1.json");

        Assert.Empty(response.Content.Headers.ContentEncoding);
    }
}

/// <summary>The request limits: Kestrel's body size limit (on a real Kestrel server, which is what enforces it) and
/// the per-request timeout.</summary>
public sealed class RequestLimitTests
{
    private sealed class KestrelFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Postgres",
                "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=2");
            builder.UseSetting("Reminders:Enabled", "false");
            builder.UseSetting("Limits:MaxRequestBodyBytes", "1024");
            TestAuth.Configure(builder);
        }
    }

    private static HttpRequestMessage PutSettings(string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/me/settings")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token(oid: "oid-ana"));
        return request;
    }

    /// <summary>A body over the limit is refused with a 413 ProblemDetails before anything reads the database; the
    /// server doesn't name itself.</summary>
    [Fact]
    public async Task A_body_over_the_limit_is_a_413_problem_and_Kestrel_sends_no_Server_header()
    {
        using var factory = new KestrelFactory();
        factory.UseKestrel(0);
        factory.StartServer();
        var client = factory.CreateClient();

        var tooLarge = await client.SendAsync(PutSettings($$"""{ "studyGroupId": null, "padding": "{{new string('x', 2048)}}" }"""));
        var health = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal("application/problem+json", tooLarge.Content.Headers.ContentType?.MediaType);
        var problem = await tooLarge.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(413, problem.GetProperty("status").GetInt32());
        Assert.False(health.Headers.Contains("Server"));
    }

    private sealed class SlowFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Postgres",
                "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=2");
            builder.UseSetting("Reminders:Enabled", "false");
            builder.UseSetting("Limits:RequestTimeoutSeconds", "1");
            TestAuth.Configure(builder);
            // A route that never finishes on its own, after the app's pipeline (so inside its timeout).
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new SlowRouteFilter()));
        }
    }

    private sealed class SlowRouteFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Map("/__slow", branch => branch.Run(context => Task.Delay(Timeout.Infinite, context.RequestAborted)));
        };
    }

    [Fact]
    public async Task A_request_that_runs_too_long_is_cancelled_and_answered_504()
    {
        using var factory = new SlowFactory();
        var request = new HttpRequestMessage(HttpMethod.Get, "/__slow");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token(oid: "oid-ana"));
        var started = DateTime.UtcNow;

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(10));
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Request timed out", problem.GetProperty("title").GetString());
    }
}
