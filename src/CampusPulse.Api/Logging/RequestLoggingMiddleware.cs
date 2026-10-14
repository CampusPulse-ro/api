using System.Diagnostics;

namespace CampusPulse.Api.Logging;

/// <summary>
/// The outermost middleware: gives every request an id (returned in <c>X-Request-Id</c> and used as
/// <c>HttpContext.TraceIdentifier</c>, so error responses carry it as <c>traceId</c>), puts it in the
/// logging scope of everything logged during the request, and logs one line per request with its
/// method, path (no query string: it can carry tokens), status and duration. Health checks are logged
/// at Debug, because the deploy and monitoring call them constantly.
/// </summary>
public sealed partial class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = RequestIds.FromHeader(context.Request.Headers[RequestIds.HeaderName]) ?? RequestIds.New();
        context.TraceIdentifier = requestId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[RequestIds.HeaderName] = requestId;
            return Task.CompletedTask;
        });

        using var scope = logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = requestId });
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var level = context.Request.Path.StartsWithSegments("/health") ? LogLevel.Debug : LogLevel.Information;
            LogRequest(logger, level, context.Request.Method, context.Request.Path.Value ?? "/",
                context.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    [LoggerMessage(EventId = 1, Message = "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs} ms")]
    private static partial void LogRequest(ILogger logger, LogLevel level, string method, string path,
        int statusCode, double elapsedMs);
}
