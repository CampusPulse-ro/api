using System.Text.Json;
using Serilog;
using Serilog.Formatting.Compact;

namespace CampusPulse.Api.Logging;

public static class LoggingSetup
{
    /// <summary>
    /// JSON lines to stdout (journald on the Pi), plus, when <c>Logging:File:Path</c> is set, the same
    /// events as JSON lines in a size-capped rolling file. The file matters on the Pi, whose journald
    /// keeps logs in RAM only, so they would not survive a reboot. Levels come from the usual
    /// <c>Logging:LogLevel</c> settings for both outputs.
    /// </summary>
    public static WebApplicationBuilder AddCampusPulseLogging(this WebApplicationBuilder builder)
    {
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
            var sizeLimit = builder.Configuration.GetValue<long?>("Logging:File:FileSizeLimitBytes") ?? 10 * 1024 * 1024;
            var keep = builder.Configuration.GetValue<int?>("Logging:File:RetainedFileCountLimit") ?? 10;
            // Serilog only as a file writer behind the standard ILogger: the app logs through
            // Microsoft.Extensions.Logging as usual, and the Logging:LogLevel filters apply first.
            var fileLogger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.File(new CompactJsonFormatter(), path,
                    fileSizeLimitBytes: sizeLimit,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: keep,
                    flushToDiskInterval: TimeSpan.FromSeconds(2))
                .CreateLogger();
            builder.Logging.AddSerilog(fileLogger, dispose: true);
        }

        return builder;
    }
}
