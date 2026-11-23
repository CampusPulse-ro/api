using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using CampusPulse.Api.Auth;
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

/// <summary>At most <see cref="Limit"/> requests per <see cref="WindowMinutes"/> (a fixed window).</summary>
public sealed class WindowLimit
{
    public int Limit { get; set; }

    public int WindowMinutes { get; set; }

    internal FixedWindowRateLimiterOptions Options() => new()
    {
        PermitLimit = Limit,
        Window = TimeSpan.FromMinutes(WindowMinutes),
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

    /// <summary>Every request without a valid token (readiness checks, the contract, refused tokens), counted per client
    /// IP address (an IPv6 address by its /64, which one host usually has to itself). Larger than a student's, because a
    /// campus or dorm network puts many students behind one address, and an expired token's 401 must reach the app (it
    /// signs the student out) rather than a 429.</summary>
    public TokenBucket Anonymous { get; set; } = new() { Burst = 300, PerMinute = 300 };

    /// <summary>Every write (POST, PUT, PATCH, DELETE) outside <c>/auth/</c>, per student or IP, on top of the above.</summary>
    public TokenBucket Writes { get; set; } = new() { Burst = 30, PerMinute = 30 };

    /// <summary>Every <c>/auth/</c> call (signing in, refreshing, signing out), per client IP, instead of
    /// <see cref="Writes"/>.</summary>
    public TokenBucket Auth { get; set; } = new() { Burst = 60, PerMinute = 60 };

    /// <summary><c>POST /auth/email/start</c> per client IP, on top of the above: each call sends an email. The
    /// per-address limits (3 per 15 minutes, 10 a day) are kept in the database. A campus network puts many students
    /// behind one address, so raise this for a class that signs up together.</summary>
    public WindowLimit EmailStart { get; set; } = new() { Limit = 20, WindowMinutes = 60 };

    /// <summary><c>POST /me</c> and <c>DELETE /me</c>, per student, on top of the above: a client calls them once per
    /// interactive sign-in and once per account.</summary>
    public TokenBucket Account { get; set; } = new() { Burst = 10, PerMinute = 1 };

    internal IEnumerable<(string Name, TokenBucket Bucket)> Buckets() =>
        [(nameof(SignedIn), SignedIn), (nameof(Anonymous), Anonymous), (nameof(Writes), Writes), (nameof(Auth), Auth), (nameof(Account), Account)];
}

/// <summary>
/// Per-client rate limits (ASP.NET Core's RateLimiter), from <see cref="RateLimitOptions"/>. Every request takes a
/// token from its client's bucket (<see cref="RateLimitOptions.SignedIn"/> or <see cref="RateLimitOptions.Anonymous"/>)
/// and every write one from its write bucket first, so a new endpoint is limited without anyone remembering to and a
/// refused write costs no read; account creation and deletion also take one from <see cref="RateLimitOptions.Account"/>
/// (<see cref="AccountPolicy"/>). <c>GET /health</c> is exempt: answering it costs less than refusing it, and a status
/// check must never report an outage that isn't one.
/// <para>A refused request is a 429 ProblemDetails with <c>Retry-After</c> (seconds), and is counted for
/// <see cref="RateLimitLog"/>: counts only, never who. Buckets live in this process's memory, as the test
/// notification's limit does: a restart empties them, and each API instance counts on its own.</para>
/// </summary>
public static class RateLimitSetup
{
    public const string AccountPolicy = "account";

    public const string EmailStartPolicy = "email-start";

    private const string UserPrefix = "user:";

    public static IServiceCollection AddCampusPulseRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RateLimitOptions>()
            .Bind(configuration.GetSection(RateLimitOptions.Section))
            .Validate(o => o.Buckets().All(b => b.Bucket.Burst >= 1 && b.Bucket.PerMinute >= 1),
                "Every RateLimits bucket needs Burst and PerMinute of at least 1.")
            .Validate(o => o.EmailStart.Limit >= 1 && o.EmailStart.WindowMinutes >= 1,
                "RateLimits:EmailStart needs Limit and WindowMinutes of at least 1.")
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
        // Writes first: a token bucket doesn't get a token back when a later limiter in the chain refuses, so this way a
        // refused write costs nothing from the client's general bucket.
        limiter.GlobalLimiter = PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(context => context.Request.Path.StartsWithSegments("/auth")
                ? RateLimitPartition.GetTokenBucketLimiter("auth:" + AddressKey(context.Connection.RemoteIpAddress), _ => settings.Auth.Options())
                : IsWrite(context.Request.Method)
                    ? RateLimitPartition.GetTokenBucketLimiter(ClientKey(context), _ => settings.Writes.Options())
                    : RateLimitPartition.GetNoLimiter("read")),
            PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var key = ClientKey(context);
                var bucket = key.StartsWith(UserPrefix, StringComparison.Ordinal) ? settings.SignedIn : settings.Anonymous;
                return RateLimitPartition.GetTokenBucketLimiter(key, _ => bucket.Options());
            }));
        limiter.AddPolicy(AccountPolicy, context => RateLimitPartition.GetTokenBucketLimiter(ClientKey(context), _ => settings.Account.Options()));
        limiter.AddPolicy(EmailStartPolicy, context =>
            RateLimitPartition.GetFixedWindowLimiter(AddressKey(context.Connection.RemoteIpAddress), _ => settings.EmailStart.Options()));
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

    /// <summary>Who a request is counted as: <c>user:&lt;account id&gt;</c> for one of our tokens,
    /// <c>user:upt:&lt;oid&gt;</c> for a UPT Entra token (installed app versions; resolving its account would cost a
    /// query per request), else <c>ip:&lt;address&gt;</c>, the client's address as <c>ForwardedHeaders</c> found it behind
    /// nginx. An invalid token never gets this far signed in, so made-up ids can't open new buckets.</summary>
    public static string ClientKey(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            if (CampusPulseTokens.UserIdOf(context.User) is { } id)
            {
                return UserPrefix + id;
            }

            if (context.User.FindFirst("oid")?.Value is { Length: > 0 } oid)
            {
                return UserPrefix + "upt:" + oid;
            }
        }

        return "ip:" + AddressKey(context.Connection.RemoteIpAddress);
    }

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
