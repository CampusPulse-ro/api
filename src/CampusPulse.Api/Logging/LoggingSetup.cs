using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Serilog;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Compact;

namespace CampusPulse.Api.Logging;

public static class LoggingSetup
{
    /// <summary>
    /// JSON lines to stdout (journald on the Pi), plus, when <c>Logging:File:Path</c> is set, the same
    /// events as JSON lines in a size-capped rolling file. The file matters on the Pi, whose journald
    /// keeps logs in RAM only, so they would not survive a reboot. Levels come from the usual
    /// <c>Logging:LogLevel</c> settings for both outputs. Also makes the request id the request's
    /// <c>TraceIdentifier</c> (see <see cref="RequestIdHttpContextFactory"/>).
    /// </summary>
    public static WebApplicationBuilder AddCampusPulseLogging(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IHttpContextFactory>(services =>
            new RequestIdHttpContextFactory(new DefaultHttpContextFactory(services)));

        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            options.JsonWriterOptions = new JsonWriterOptions { Indented = false };
        });

        var path = builder.Configuration["Logging:File:Path"];
        if (!string.IsNullOrWhiteSpace(path))
        {
            EnsureWritable(path);
            var sizeLimit = builder.Configuration.GetValue<long?>("Logging:File:FileSizeLimitBytes") ?? 10 * 1024 * 1024;
            var keep = builder.Configuration.GetValue<int?>("Logging:File:RetainedFileCountLimit") ?? 10;
            // Serilog only as a file writer behind the standard ILogger: the app logs through
            // Microsoft.Extensions.Logging as usual. Registered as a plain provider, not with
            // AddSerilog(), which adds a Trace rule for its provider that beats every Logging:LogLevel
            // filter (the file then got every category at Trace, query strings included).
            var fileLogger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.File(new RenderedCompactJsonFormatter(), path,
                    fileSizeLimitBytes: sizeLimit,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: keep,
                    flushToDiskInterval: TimeSpan.FromSeconds(2))
                .CreateLogger();
            builder.Services.AddSingleton<ILoggerProvider>(_ => new SerilogLoggerProvider(fileLogger, dispose: true));
        }

        return builder;
    }

    /// <summary>
    /// Fails the start when the log file can't be written. The file sink opens lazily and drops events
    /// silently on error, so without this a wrong path or owner would mean no file and no sign of it;
    /// failing the start makes the deploy's readiness check roll back instead.
    /// </summary>
    private static void EnsureWritable(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);
            // The files the sink may append to: the path itself and its rolled siblings (api_001.log…).
            // Only when there are none is the path created, as the sink would do first.
            var name = Path.GetFileNameWithoutExtension(fullPath);
            var extension = Path.GetExtension(fullPath);
            var existing = Directory.GetFiles(directory, $"{name}_*{extension}").Append(fullPath).Where(File.Exists).ToList();
            foreach (var file in existing.Count > 0 ? existing : [fullPath])
            {
                using var _ = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"Logging:File:Path '{path}' can't be written: {e.Message}. Fix the path or the folder's owner, or unset it.", e);
        }
    }
}
