using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace CampusPulse.Api.RateLimiting;

/// <summary>A token bucket: up to <see cref="Burst"/> requests at once, refilled by <see cref="PerMinute"/> a minute
/// (one at a time, evenly), so a client that keeps going gets <see cref="PerMinute"/> a minute.</summary>
public sealed class TokenBucket
{
    public int Burst { get; set; }

    public int PerMinute { get; set; }

    internal TokenBucketRateLimiterOptions Options() => new()
    {
        TokenLimit = Burst,
        TokensPerPeriod = 1,
        ReplenishmentPeriod = TimeSpan.FromMinutes(1) / PerMinute,
        // Refuse at once rather than hold the request (and its connection) in a queue.
        QueueLimit = 0,
        AutoReplenishment = true,
    };
}

/// <summary>The <c>RateLimits</c> configuration section (<c>RateLimits__SignedIn__Burst=100</c> and so on).</summary>
public sealed class RateLimitOptions
{
    public const string Section = "RateLimits";

    /// <summary>False turns every limit off except the test notification's own (load tests; an emergency).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Every request of a signed-in student, counted per student (the token's <c>oid</c>).</summary>
    public TokenBucket SignedIn { get; set; } = new() { Burst = 100, PerMinute = 100 };

    /// <summary>Every request without a valid token (health checks, the contract, refused tokens), counted per client
    /// IP address (an IPv6 address by its /64, which one host usually has to itself).</summary>
    public TokenBucket Anonymous { get; set; } = new() { Burst = 100, PerMinute = 100 };

    /// <summary>Every write (POST, PUT, PATCH, DELETE), per student or IP, on top of the above.</summary>
    public TokenBucket Writes { get; set; } = new() { Burst = 30, PerMinute = 30 };

    /// <summary><c>POST /me</c> and <c>DELETE /me</c>, per student, on top of the above: a client calls them once per
    /// interactive sign-in and once per account.</summary>
    public TokenBucket Account { get; set; } = new() { Burst = 10, PerMinute = 1 };

    internal IEnumerable<(string Name, TokenBucket Bucket)> Buckets() =>
        [(nameof(SignedIn), SignedIn), (nameof(Anonymous), Anonymous), (nameof(Writes), Writes), (nameof(Account), Account)];
}

/// <summary>
/// Per-client rate limits (ASP.NET Core's RateLimiter), from <see cref="RateLimitOptions"/>. Every request takes a
/// token from its client's bucket (<see cref="RateLimitOptions.SignedIn"/> or <see cref="RateLimitOptions.Anonymous"/>)
/// and every write one from its write bucket too, so a new endpoint is limited without anyone remembering to; account
/// creation and deletion also take one from <see cref="RateLimitOptions.Account"/> (<see cref="AccountPolicy"/>).
/// <para>A refused request is a 429 ProblemDetails with <c>Retry-After</c> (seconds), and is counted for
/// <see cref="RateLimitLog"/>: counts only, never who. Buckets live in this process's memory, as the test
/// notification's limit does: a restart empties them, and each API instance counts on its own.</para>
/// </summary>
public static class RateLimitSetup
{
    public const string AccountPolicy = "account";

    private const string UserPrefix = "user:";

    public static IServiceCollection AddCampusPulseRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RateLimitOptions>()
            .Bind(configuration.GetSection(RateLimitOptions.Section))
            .Validate(o => o.Buckets().All(b => b.Bucket.Burst >= 1 && b.Bucket.PerMinute >= 1),
                "Every RateLimits bucket needs Burst and PerMinute of at least 1.")
            .ValidateOnStart();
        services.AddSingleton<RateLimitLog>();
        services.AddHostedService(provider => provider.GetRequiredService<RateLimitLog>());
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<RateLimitOptions>>((limiter, settings) =>
            Configure(limiter, settings.Value));
        services.AddRateLimiter(_ => { });
        return services;
    }

    /// <summary>Limits requests from here on, when <see cref="RateLimitOptions.Enabled"/>. After authentication, so a
    /// signed-in student is counted by who they are; after CORS, so preflights aren't counted and a 429 still
    /// carries the CORS headers a browser needs to read it.</summary>
    public static WebApplication UseCampusPulseRateLimiting(this WebApplication app)
    {
        if (app.Services.GetRequiredService<IOptions<RateLimitOptions>>().Value.Enabled)
        {
            app.UseRateLimiter();
        }

        return app;
    }

    private static void Configure(RateLimiterOptions limiter, RateLimitOptions settings)
    {
        limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        limiter.GlobalLimiter = PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var key = ClientKey(context);
                var bucket = key.StartsWith(UserPrefix, StringComparison.Ordinal) ? settings.SignedIn : settings.Anonymous;
                return RateLimitPartition.GetTokenBucketLimiter(key, _ => bucket.Options());
            }),
            PartitionedRateLimiter.Create<HttpContext, string>(context => IsWrite(context.Request.Method)
                ? RateLimitPartition.GetTokenBucketLimiter(ClientKey(context), _ => settings.Writes.Options())
                : RateLimitPartition.GetNoLimiter("read")));
        limiter.AddPolicy(AccountPolicy, context => RateLimitPartition.GetTokenBucketLimiter(ClientKey(context), _ => settings.Account.Options()));
        limiter.OnRejected = async (rejected, cancellationToken) =>
        {
            var http = rejected.HttpContext;
            http.RequestServices.GetRequiredService<RateLimitLog>().Refused(signedIn: http.User.Identity?.IsAuthenticated == true);
            var seconds = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                : 60;
            http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
            {
                HttpContext = http,
                ProblemDetails =
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Too many requests",
                    Detail = $"This client sent too many requests. Try again in {seconds} s (Retry-After).",
                },
            });
        };
    }

    /// <summary>Who a request is counted as: <c>user:&lt;oid&gt;</c> for a valid token (an invalid one never gets this
    /// far signed in, so made-up oids can't open new buckets), else <c>ip:&lt;address&gt;</c>, the client's address as
    /// <c>ForwardedHeaders</c> found it behind nginx.</summary>
    public static string ClientKey(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true && context.User.FindFirst("oid")?.Value is { Length: > 0 } oid
            ? UserPrefix + oid
            : "ip:" + AddressKey(context.Connection.RemoteIpAddress);

    /// <summary>An IPv4 address as it is; an IPv6 one by its /64, since one host usually has a whole /64 and could
    /// otherwise use a new address per request.</summary>
    public static string AddressKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return new IPAddress(bytes) + "/64";
    }

    private static bool IsWrite(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
}
