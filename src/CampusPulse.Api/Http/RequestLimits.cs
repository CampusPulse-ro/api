using System.IO.Compression;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.ResponseCompression;

namespace CampusPulse.Api.Http;

/// <summary>The <c>Limits</c> configuration section.</summary>
public sealed class RequestLimitOptions
{
    public const string Section = "Limits";

    /// <summary>The largest request body Kestrel accepts. The largest body this API takes is a push token (about
    /// 100 bytes); anything near the default 16 KiB is not a client of ours.</summary>
    public long MaxRequestBodyBytes { get; set; } = 16 * 1024;

    /// <summary>How long a request may run before it is cancelled and answered 504, so a request stuck on the
    /// database or a remote service frees its connection instead of holding it until nginx gives up (60 s). Normal
    /// requests take milliseconds; 30 s, like Npgsql's command timeout, leaves room for the first request after a
    /// start (EF Core builds its model and compiles queries then: seconds on the Pi) and for the test notification,
    /// which waits up to 15 s for the push service.</summary>
    public int RequestTimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Limits on a single request, and compression of the answers:
/// <list type="bullet">
/// <item>request bodies up to <see cref="RequestLimitOptions.MaxRequestBodyBytes"/> (Kestrel; 413 ProblemDetails);</item>
/// <item>a timeout per request (<see cref="RequestLimitOptions.RequestTimeoutSeconds"/>; 504 ProblemDetails);</item>
/// <item>no <c>Server: Kestrel</c> header;</item>
/// <item>Brotli or gzip compression, at the fastest level (dynamic JSON on a Raspberry Pi). On for HTTPS too: BREACH
/// needs an attacker's page to make the victim's browser send credentialed requests that reflect attacker input next to
/// a secret, and this API takes bearer tokens a page can't attach, never cookies.</item>
/// </list>
/// </summary>
public static class RequestLimits
{
    public static WebApplicationBuilder AddCampusPulseRequestLimits(this WebApplicationBuilder builder)
    {
        var limits = builder.Configuration.GetSection(RequestLimitOptions.Section).Get<RequestLimitOptions>() ?? new RequestLimitOptions();
        if (limits.MaxRequestBodyBytes < 1 || limits.RequestTimeoutSeconds < 1)
        {
            throw new InvalidOperationException("Limits__MaxRequestBodyBytes and Limits__RequestTimeoutSeconds must be at least 1.");
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = limits.MaxRequestBodyBytes;
        });

        builder.Services.AddRequestTimeouts(options => options.DefaultPolicy = TimeoutPolicy(TimeSpan.FromSeconds(limits.RequestTimeoutSeconds)));

        builder.Services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
        });
        builder.Services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
        builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
        return builder;
    }

    private static RequestTimeoutPolicy TimeoutPolicy(TimeSpan timeout) => new()
    {
        Timeout = timeout,
        TimeoutStatusCode = StatusCodes.Status504GatewayTimeout,
        WriteTimeoutResponse = context => context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails =
            {
                Status = StatusCodes.Status504GatewayTimeout,
                Title = "Request timed out",
                Detail = $"The request took longer than {timeout.TotalSeconds:0} s and was cancelled. Try again.",
            },
        }).AsTask(),
    };
}
