using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CampusPulse.Api.RateLimiting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CampusPulse.Api.Tests;

/// <summary>
/// The per-client rate limits, with small buckets so a test can empty one. Requests go to <c>/</c>, which needs no
/// database: with a token it still counts as the student's (authentication runs for every request), and a POST to it is
/// a write (answered 405, until the write bucket is empty). The client's address is set on the connection, as
/// Kestrel would, with nginx's <c>X-Forwarded-For</c> where the request comes through the proxy.
/// </summary>
public sealed class RateLimitingTests
{
    private static readonly IPAddress Nginx = IPAddress.Loopback;

    private sealed class Factory(Dictionary<string, string> settings, ILoggerProvider? logs = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Postgres",
                "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=2");
            builder.UseSetting("Reminders:Enabled", "false");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            TestAuth.Configure(builder);
            if (logs is not null)
            {
                builder.ConfigureTestServices(services => services.AddSingleton<ILoggerProvider>(logs));
            }
        }
    }

    private static Factory With(params (string Key, string Value)[] settings) =>
        new(settings.ToDictionary(s => $"RateLimits:{s.Key}", s => s.Value));

    /// <summary>A request as Kestrel hands it over: from <paramref name="connection"/>, with nginx's
    /// <c>X-Forwarded-For</c> when <paramref name="forwardedFor"/> is given.</summary>
    private static Task<HttpContext> Send(WebApplicationFactory<Program> factory, IPAddress connection, string? forwardedFor = null,
        string? oid = null, string method = "GET", string path = "/", string? origin = null, string? token = null) =>
        factory.Server.SendAsync(context =>
        {
            context.Request.Method = method;
            context.Request.Path = path;
            context.Connection.RemoteIpAddress = connection;
            if (forwardedFor is not null)
            {
                context.Request.Headers["X-Forwarded-For"] = forwardedFor;
            }

            if ((token ?? (oid is null ? null : TestAuth.Token(oid: oid))) is { } bearer)
            {
                context.Request.Headers.Authorization = $"Bearer {bearer}";
            }

            if (origin is not null)
            {
                context.Request.Headers.Origin = origin;
            }
        });

    private static async Task<List<int>> Statuses(int count, Func<Task<HttpContext>> send)
    {
        var statuses = new List<int>();
        for (var i = 0; i < count; i++)
        {
            statuses.Add((await send()).Response.StatusCode);
        }

        return statuses;
    }

    [Fact]
    public async Task Anonymous_requests_are_counted_per_client_address_behind_nginx_and_refused_with_a_429_problem()
    {
        using var factory = With(("Anonymous:Burst", "3"), ("Anonymous:PerMinute", "1"));

        var first = await Statuses(3, () => Send(factory, Nginx, "203.0.113.7"));
        var refused = await Send(factory, Nginx, "203.0.113.7");
        var someoneElse = await Send(factory, Nginx, "203.0.113.8");

        Assert.Equal([200, 200, 200], first);
        Assert.Equal(StatusCodes.Status429TooManyRequests, refused.Response.StatusCode);
        // One token a minute: the next one is at most a minute away.
        var retryAfter = int.Parse(refused.Response.Headers.RetryAfter.ToString());
        Assert.InRange(retryAfter, 1, 60);
        Assert.StartsWith("application/problem+json", refused.Response.ContentType);
        var problem = await JsonDocument.ParseAsync(refused.Response.Body);
        Assert.Equal(429, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Too many requests", problem.RootElement.GetProperty("title").GetString());
        Assert.True(problem.RootElement.TryGetProperty("requestId", out _));
        Assert.Equal(StatusCodes.Status200OK, someoneElse.Response.StatusCode);
    }

    /// <summary>Only nginx (loopback) may say who the client is. A client that sends <c>X-Forwarded-For</c> itself, from
    /// anywhere else, is counted under its own address however it varies the header.</summary>
    [Fact]
    public async Task A_forwarded_header_from_anywhere_but_the_local_proxy_is_ignored()
    {
        using var factory = With(("Anonymous:Burst", "3"), ("Anonymous:PerMinute", "1"));
        var stranger = IPAddress.Parse("198.51.100.1");

        var statuses = await Statuses(5, () => Send(factory, stranger, $"192.0.2.{Random.Shared.Next(1, 250)}"));

        Assert.Equal([200, 200, 200, 429, 429], statuses);
    }

    /// <summary>nginx appends the address it saw to whatever the client sent; only that last one counts.</summary>
    [Fact]
    public async Task Addresses_a_client_puts_in_front_of_nginxs_are_ignored()
    {
        using var factory = With(("Anonymous:Burst", "3"), ("Anonymous:PerMinute", "1"));
        var round = 0;

        var statuses = await Statuses(5, () => Send(factory, Nginx, $"10.0.0.{++round}, 203.0.113.9"));

        Assert.Equal([200, 200, 200, 429, 429], statuses);
    }

    [Fact]
    public async Task A_signed_in_student_is_counted_as_themselves_not_by_address()
    {
        using var factory = With(("SignedIn:Burst", "3"), ("SignedIn:PerMinute", "1"));

        var ana = await Statuses(4, () => Send(factory, Nginx, "203.0.113.7", oid: "oid-ana"));
        var ion = await Send(factory, Nginx, "203.0.113.7", oid: "oid-ion");
        var anonymous = await Send(factory, Nginx, "203.0.113.7");

        Assert.Equal([200, 200, 200, 429], ana);
        Assert.Equal(StatusCodes.Status200OK, ion.Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, anonymous.Response.StatusCode);
    }

    /// <summary>A token that fails the check (here: signed by someone else) is anonymous, so made-up oids share their
    /// address's bucket instead of each getting a new one.</summary>
    [Fact]
    public async Task Refused_tokens_are_counted_by_address_whatever_oid_they_claim()
    {
        using var factory = With(("Anonymous:Burst", "2"), ("Anonymous:PerMinute", "1"));
        var foreignKey = new Microsoft.IdentityModel.Tokens.RsaSecurityKey(System.Security.Cryptography.RSA.Create(2048));

        var statuses = await Statuses(3, () => Send(factory, Nginx, "203.0.113.7",
            token: TestAuth.Token(oid: "oid-" + Guid.NewGuid().ToString("N"), signWith: foreignKey)));

        Assert.Equal([200, 200, 429], statuses);
    }

    [Fact]
    public async Task Writes_take_from_a_smaller_bucket_of_their_own_as_well()
    {
        using var factory = With(("Writes:Burst", "2"), ("Writes:PerMinute", "1"));

        var writes = await Statuses(3, () => Send(factory, Nginx, "203.0.113.7", oid: "oid-ana", method: "POST"));
        var read = await Send(factory, Nginx, "203.0.113.7", oid: "oid-ana");

        Assert.Equal([405, 405, 429], writes);
        Assert.Equal(StatusCodes.Status200OK, read.Response.StatusCode);
    }

    [Fact]
    public async Task A_browser_can_read_a_429_and_its_Retry_After()
    {
        using var factory = new Factory(new()
        {
            ["RateLimits:Anonymous:Burst"] = "1",
            ["RateLimits:Anonymous:PerMinute"] = "1",
            ["Cors:AllowedOrigins:0"] = "https://campuspulse.example",
        });

        await Send(factory, Nginx, "203.0.113.7", origin: "https://campuspulse.example");
        var refused = await Send(factory, Nginx, "203.0.113.7", origin: "https://campuspulse.example");

        Assert.Equal(StatusCodes.Status429TooManyRequests, refused.Response.StatusCode);
        Assert.Equal("https://campuspulse.example", refused.Response.Headers.AccessControlAllowOrigin.ToString());
        Assert.Contains("Retry-After", refused.Response.Headers.AccessControlExposeHeaders.ToString());
    }

    /// <summary>One Warning line a minute with the counts, never a student, an address or a path.</summary>
    [Fact]
    public async Task Refusals_are_logged_as_counts_only()
    {
        var logs = new CollectingLoggerProvider();
        using var factory = new Factory(new()
        {
            ["RateLimits:Anonymous:Burst"] = "1",
            ["RateLimits:Anonymous:PerMinute"] = "1",
            ["RateLimits:SignedIn:Burst"] = "1",
            ["RateLimits:SignedIn:PerMinute"] = "1",
        }, logs);

        await Statuses(3, () => Send(factory, Nginx, "203.0.113.7"));
        await Statuses(2, () => Send(factory, Nginx, "203.0.113.7", oid: "oid-ana"));
        var log = factory.Services.GetRequiredService<RateLimitLog>();
        log.Flush();
        log.Flush();

        var line = Assert.Single(logs.Entries, e => e.Category == typeof(RateLimitLog).FullName);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal("Rate limits refused 3 requests in the last minute (1 signed in, 2 anonymous)", line.Message);
    }

    /// <summary>Liveness is never refused: answering it costs less than refusing it, and a status check must not report
    /// an outage that isn't one.</summary>
    [Fact]
    public async Task The_liveness_check_is_never_refused()
    {
        using var factory = With(("Anonymous:Burst", "1"), ("Anonymous:PerMinute", "1"));

        var health = await Statuses(5, () => Send(factory, Nginx, "203.0.113.7", path: "/health"));
        var other = await Statuses(2, () => Send(factory, Nginx, "203.0.113.7"));

        Assert.All(health, status => Assert.Equal(200, status));
        Assert.Equal([200, 429], other);
    }

    /// <summary>The write bucket comes first, so a refused write takes nothing from the client's general bucket.</summary>
    [Fact]
    public async Task A_refused_write_costs_no_read()
    {
        using var factory = With(("Writes:Burst", "1"), ("Writes:PerMinute", "1"), ("SignedIn:Burst", "3"), ("SignedIn:PerMinute", "1"));

        var writes = await Statuses(5, () => Send(factory, Nginx, "203.0.113.7", oid: "oid-ana", method: "POST"));
        var reads = await Statuses(2, () => Send(factory, Nginx, "203.0.113.7", oid: "oid-ana"));

        Assert.Equal([405, 429, 429, 429, 429], writes);
        Assert.Equal([200, 200], reads);
    }

    [Fact]
    public async Task Turned_off_nothing_is_refused()
    {
        using var factory = With(("Enabled", "false"), ("Anonymous:Burst", "1"), ("Anonymous:PerMinute", "1"));

        Assert.All(await Statuses(5, () => Send(factory, Nginx, "203.0.113.7")), status => Assert.Equal(200, status));
    }

    [Fact]
    public void The_defaults_are_the_documented_ones()
    {
        using var factory = With();

        var limits = factory.Services.GetRequiredService<IOptions<RateLimitOptions>>().Value;

        Assert.True(limits.Enabled);
        Assert.Equal((100, 100), (limits.SignedIn.Burst, limits.SignedIn.PerMinute));
        Assert.Equal((300, 300), (limits.Anonymous.Burst, limits.Anonymous.PerMinute));
        Assert.Equal((30, 30), (limits.Writes.Burst, limits.Writes.PerMinute));
        Assert.Equal((10, 1), (limits.Account.Burst, limits.Account.PerMinute));
    }

    [Fact]
    public void A_bucket_without_tokens_fails_the_start()
    {
        using var factory = With(("Writes:PerMinute", "0"));

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Every RateLimits bucket needs Burst and PerMinute of at least 1.", error.ToString());
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:3::1", "2001:db8:1:3::/64")]
    public void An_IPv6_client_is_counted_by_its_64(string address, string key) =>
        Assert.Equal(key, RateLimitSetup.AddressKey(IPAddress.Parse(address)));
}

/// <summary><c>POST /me</c> and <c>DELETE /me</c> share the smallest bucket, per student.</summary>
public sealed class AccountRateLimitTests(PostgresAppFactory factory) : IClassFixture<PostgresAppFactory>
{
    private static HttpRequestMessage Request(HttpMethod method, string oid)
    {
        var request = new HttpRequestMessage(method, "/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token(oid: oid));
        return request;
    }

    [PostgresFact]
    public async Task Creating_and_deleting_the_account_have_the_strictest_limit()
    {
        using var app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimits:Account:Burst", "2");
            builder.UseSetting("RateLimits:Account:PerMinute", "1");
        });
        var client = app.CreateClient();
        var ana = "oid-" + Guid.NewGuid().ToString("N");
        var ion = "oid-" + Guid.NewGuid().ToString("N");

        var created = await client.SendAsync(Request(HttpMethod.Post, ana));
        var again = await client.SendAsync(Request(HttpMethod.Post, ana));
        var third = await client.SendAsync(Request(HttpMethod.Post, ana));
        var delete = await client.SendAsync(Request(HttpMethod.Delete, ana));
        var read = await client.SendAsync(Request(HttpMethod.Get, ana));
        var other = await client.SendAsync(Request(HttpMethod.Post, ion));

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.OK), (created.StatusCode, again.StatusCode));
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, delete.StatusCode);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }
}
