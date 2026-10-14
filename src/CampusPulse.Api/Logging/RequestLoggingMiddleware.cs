using System.Diagnostics;

namespace CampusPulse.Api.Logging;

/// <summary>
/// Returns the request id in <c>X-Request-Id</c> and writes one line per request: method, path
/// (never the query string, which may carry tokens), status and duration. Successful <c>/health*</c>
/// checks go at Debug, since deploys and monitoring call them constantly; a failing one (a 503
/// readiness) stays at Information. The id itself is the TraceIdentifier set by
/// <see cref="RequestIdHttpContextFactory"/>, and hosting's log scope already carries it as
/// <c>RequestId</c> (that scope exists while <c>Microsoft.AspNetCore.Hosting</c> logs at Critical
/// or above, i.e. unless it is set to None).
/// </summary>
public sealed partial class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = context.TraceIdentifier;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[RequestIds.HeaderName] = requestId;
            return Task.CompletedTask;
        });

        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var status = context.Response.StatusCode;
            var level = status < 400 && context.Request.Path.StartsWithSegments("/health") ? LogLevel.Debug : LogLevel.Information;
            LogRequest(logger, level, context.Request.Method, context.Request.Path.Value ?? "/", status,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    [LoggerMessage(EventId = 1, Message = "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs} ms")]
    private static partial void LogRequest(ILogger logger, LogLevel level, string method, string path, int statusCode, double elapsedMs);
}
