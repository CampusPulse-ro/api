using System.Globalization;
using System.Net;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace CampusPulse.Api.Metrics;

/// <summary>
/// Metrics for scraping (OpenTelemetry, Prometheus text format), served only on a separate loopback listener so that
/// nothing through nginx can read them. With <c>Metrics:Port</c> set, the API also listens on
/// <c>http://127.0.0.1:&lt;port&gt;</c> (added to the URLs it was given) and answers <see cref="Path"/> there, to loopback
/// clients only; every other request on that port is a 404, and the public port has no <see cref="Path"/> at all
/// (nginx proxies only that one). Unset (the default), no metrics are collected or served.
/// <para>What is exported: request rate, duration and status per route (<c>http.server.*</c>), rate-limit
/// decisions (<c>aspnetcore.rate_limiting.*</c>, 429s included), unhandled exceptions, Kestrel connections, outgoing
/// HTTP calls (Microsoft sign-in, Expo), the Npgsql connection pool (<c>db.client.*</c>), the timetable cache
/// (<see cref="CampusPulseMetrics"/>) and the .NET runtime (GC, memory, thread pool).</para>
/// </summary>
public static class MetricsSetup
{
    public const string Path = "/metrics";

    /// <summary>The meters exported.</summary>
    public static readonly string[] Meters =
    [
        CampusPulseMetrics.MeterName,
        "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Server.Kestrel",
        "Microsoft.AspNetCore.RateLimiting",
        "Microsoft.AspNetCore.Diagnostics",
        "System.Net.Http",
        "System.Runtime",
        "Npgsql",
    ];

    /// <summary><c>Metrics:Port</c>, or null when metrics are off. Anything but a port number fails the start.</summary>
    public static int? Port(IConfiguration configuration)
    {
        var text = configuration["Metrics:Port"];
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
            ? port
            : throw new InvalidOperationException("Metrics__Port must be a TCP port number (1-65535).");
    }

    public static WebApplicationBuilder AddCampusPulseMetrics(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<CampusPulseMetrics>();
        if (Port(builder.Configuration) is not { } port)
        {
            return builder;
        }

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("campuspulse-api"))
            .WithMetrics(metrics => metrics
                .AddMeter(Meters)
                // Scrapes are seconds apart; a cached answer would only hide the latest counts.
                .AddPrometheusExporter(options => options.ScrapeResponseCacheDurationMilliseconds = 0));

        // The listener is bound to 127.0.0.1 here, whatever the configured URLs say, so it can't end up on a public
        // interface. Without configured URLs (a container's ASPNETCORE_HTTP_PORTS) nothing is added: put it in the URLs.
        var urls = builder.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey);
        if (!string.IsNullOrWhiteSpace(urls)
            && !urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(url => BindingAddress.Parse(url).Port == port))
        {
            builder.WebHost.UseUrls($"{urls};http://127.0.0.1:{port}");
        }

        return builder;
    }

    /// <summary>First in the pipeline: a scrape is answered before anything else runs (no request log line, no
    /// rate limit), and judged on the connection itself, before <c>ForwardedHeaders</c> could change its address.</summary>
    public static WebApplication UseCampusPulseMetrics(this WebApplication app)
    {
        if (Port(app.Configuration) is not { } port)
        {
            return app;
        }

        app.UseOpenTelemetryPrometheusScrapingEndpoint(context => IsScrape(context, port));
        app.Use((context, next) =>
        {
            if (context.Connection.LocalPort != port)
            {
                return next(context);
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
        return app;
    }

    /// <summary>A GET of <see cref="Path"/> that arrived on the metrics port from a loopback address.</summary>
    public static bool IsScrape(HttpContext context, int port) =>
        context.Connection.LocalPort == port
        && context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote)
        && HttpMethods.IsGet(context.Request.Method)
        && context.Request.Path == Path;
}
