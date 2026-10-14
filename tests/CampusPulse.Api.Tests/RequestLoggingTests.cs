using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CampusPulse.Api.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;

namespace CampusPulse.Api.Tests;

public sealed class RequestIdsTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("0f8fad5b-d9cb-469f-a165-70867728950e")]
    [InlineData("client-1.req_42:7")]
    public void A_well_formed_caller_id_is_kept(string id) => Assert.Equal(id, RequestIds.FromHeader(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("new\nline")]
    [InlineData("<script>")]
    [InlineData("ăîș")]
    public void Anything_else_is_refused(string? id) => Assert.Null(RequestIds.FromHeader(id));

    [Fact]
    public void Longer_than_64_is_refused()
    {
        Assert.NotNull(RequestIds.FromHeader(new string('a', 64)));
        Assert.Null(RequestIds.FromHeader(new string('a', 65)));
    }

    [Fact]
    public void New_ids_are_32_hex() => Assert.Matches("^[0-9a-f]{32}$", RequestIds.New());
}

public sealed class RequestLoggingTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "campuspulse-log-test-" + Guid.NewGuid().ToString("N"));

    /// <summary>The app with a log collector; <paramref name="debug"/> lowers the default level to Debug,
    /// otherwise appsettings.json's levels apply, as deployed.</summary>
    private sealed class Factory(CollectingLoggerProvider logs, string? logFile, bool debug = true) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Postgres",
                "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=2");
            if (debug)
            {
                builder.UseSetting("Logging:LogLevel:Default", "Debug");
            }

            if (logFile is not null)
            {
                builder.UseSetting("Logging:File:Path", logFile);
            }

            // Sign-in on, so the throwing route (which the fallback policy protects) can be reached.
            TestAuth.Configure(builder);

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<ILoggerProvider>(logs);
                // A route that throws, appended after the app's own pipeline (so after the
                // request logging and the exception handler).
                services.AddSingleton<IStartupFilter>(new ThrowingRouteFilter());
            });
        }
    }

    private sealed class ThrowingRouteFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Map("/__throw", branch => branch.Run(_ => throw new InvalidOperationException("boom")));
        };
    }

    [Fact]
    public async Task A_request_without_an_id_gets_a_new_one()
    {
        using var factory = new Factory(new CollectingLoggerProvider(), null);

        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Matches("^[0-9a-f]{32}$", response.Headers.GetValues(RequestIds.HeaderName).Single());
    }

    [Fact]
    public async Task A_caller_id_is_honoured_and_a_bad_one_replaced()
    {
        using var factory = new Factory(new CollectingLoggerProvider(), null);
        var client = factory.CreateClient();

        var good = new HttpRequestMessage(HttpMethod.Get, "/health");
        good.Headers.Add(RequestIds.HeaderName, "client-req-1");
        Assert.Equal("client-req-1", (await client.SendAsync(good)).Headers.GetValues(RequestIds.HeaderName).Single());

        var bad = new HttpRequestMessage(HttpMethod.Get, "/health");
        bad.Headers.TryAddWithoutValidation(RequestIds.HeaderName, new string('x', 65));
        Assert.Matches("^[0-9a-f]{32}$", (await client.SendAsync(bad)).Headers.GetValues(RequestIds.HeaderName).Single());
    }

    [Fact]
    public async Task Each_request_is_logged_once_with_its_one_id_and_passing_health_checks_only_at_debug()
    {
        var logs = new CollectingLoggerProvider();
        using var factory = new Factory(logs, null);
        var client = factory.CreateClient();

        var root = await client.GetAsync("/?token=secret");
        var id = root.Headers.GetValues(RequestIds.HeaderName).Single();
        await client.GetAsync("/health");
        // No database behind this factory, so readiness fails: that one must stay visible.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);

        var rootLine = Assert.Single(logs.Entries, e => e.Category.EndsWith(nameof(RequestLoggingMiddleware)) && e.Message.Contains("GET / "));
        Assert.Equal(LogLevel.Information, rootLine.Level);
        Assert.Contains("responded 200", rootLine.Message);
        Assert.DoesNotContain("secret", rootLine.Message);   // no query string in logs
        // One RequestId in scope, and it is the returned id (hosting's scope, not a second one).
        Assert.Equal([id], rootLine.ScopeValues.Where(kv => kv.Key == "RequestId").Select(kv => kv.Value));

        var healthLine = Assert.Single(logs.Entries, e => e.Category.EndsWith(nameof(RequestLoggingMiddleware)) && e.Message.Contains("/health responded"));
        Assert.Equal(LogLevel.Debug, healthLine.Level);
        var readyLine = Assert.Single(logs.Entries, e => e.Category.EndsWith(nameof(RequestLoggingMiddleware)) && e.Message.Contains("/health/ready responded 503"));
        Assert.Equal(LogLevel.Information, readyLine.Level);
    }

    [Fact]
    public async Task An_unhandled_exception_is_a_500_problem_carrying_the_request_id_and_is_logged_with_its_stack()
    {
        var logs = new CollectingLoggerProvider();
        using var factory = new Factory(logs, null);

        var request = new HttpRequestMessage(HttpMethod.Get, "/__throw");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token());
        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var id = response.Headers.GetValues(RequestIds.HeaderName).Single();
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, problem.GetProperty("requestId").GetString());
        Assert.NotEqual(id, problem.GetProperty("traceId").GetString());   // the default W3C trace id
        Assert.DoesNotContain("boom", problem.ToString());   // no exception details to the client

        var error = Assert.Single(logs.Entries, e => e.Exception is InvalidOperationException);
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Equal(id, error.Scope["RequestId"]);
        Assert.Contains(logs.Entries, e => e.Message.Contains("GET /__throw responded 500"));
    }

    [Fact]
    public async Task With_a_file_path_the_events_land_in_the_file_as_json_lines_at_the_configured_levels()
    {
        var file = Path.Combine(_logDir, "api.log");   // the folder is created at startup
        string id;
        SerilogLoggerProvider fileProvider;
        using (var factory = new Factory(new CollectingLoggerProvider(), file, debug: false))
        {
            var client = factory.CreateClient();
            var response = await client.GetAsync("/?token=secret");
            id = response.Headers.GetValues(RequestIds.HeaderName).Single();
            await client.GetAsync("/health");
            fileProvider = factory.Services.GetServices<ILoggerProvider>().OfType<SerilogLoggerProvider>().Single();
        }

        // The factory's Dispose only tells the app to stop; the app disposes its services (and so
        // closes the file) on its own thread, possibly after Dispose returned. Disposing the provider
        // here waits until the file is flushed and closed.
        fileProvider.Dispose();

        var text = File.ReadAllText(Directory.GetFiles(_logDir, "api*.log").Single());
        // The Logging:LogLevel defaults apply to the file too: no hosting request lines (which carry
        // the query string), no Debug health line.
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("/health", text);
        Assert.DoesNotContain("Microsoft.AspNetCore.Hosting", text);
        var events = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement).ToList();
        var request = Assert.Single(events, e =>
            e.TryGetProperty("SourceContext", out var c) && c.GetString()!.EndsWith(nameof(RequestLoggingMiddleware)));
        Assert.Equal(id, request.GetProperty("RequestId").GetString());
        Assert.Equal("/", request.GetProperty("Path").GetString());
        Assert.Equal(200, request.GetProperty("StatusCode").GetInt32());
        Assert.StartsWith("HTTP \"GET\" \"/\" responded 200", request.GetProperty("@m").GetString());
        Assert.True(request.TryGetProperty("@t", out _));
    }

    [Fact]
    public void An_unwritable_log_path_fails_the_start()
    {
        Directory.CreateDirectory(_logDir);
        var notAFolder = Path.Combine(_logDir, "file");
        File.WriteAllText(notAFolder, "");
        using var factory = new Factory(new CollectingLoggerProvider(), Path.Combine(notAFolder, "api.log"));

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(Chain(error), e => e is InvalidOperationException && e.Message.Contains("Logging:File:Path"));
    }

    private static IEnumerable<Exception> Chain(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
        {
            yield return e;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_logDir))
        {
            Directory.Delete(_logDir, recursive: true);
        }
    }
}

/// <summary>Captures log entries with their scope values, for assertions.</summary>
public sealed class CollectingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    private readonly List<Entry> _entries = [];

    /// <summary>A copy of what was logged so far. The app keeps logging on its own threads (background
    /// services such as the sign-in metadata check) while a test reads, so never the live list.</summary>
    public IReadOnlyList<Entry> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>A log entry; <see cref="ScopeValues"/> lists every scope value (outermost first),
    /// <see cref="Scope"/> the same as a dictionary where an inner scope wins.</summary>
    public sealed record Entry(string Category, LogLevel Level, string Message, Exception? Exception,
        IReadOnlyList<KeyValuePair<string, object?>> ScopeValues)
    {
        public IReadOnlyDictionary<string, object?> Scope =>
            ScopeValues.GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Last().Value);
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class Logger(CollectingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var scope = new List<KeyValuePair<string, object?>>();
            owner._scopes.ForEachScope((value, acc) =>
            {
                if (value is IEnumerable<KeyValuePair<string, object>> pairs)
                {
                    foreach (var (k, v) in pairs)
                    {
                        acc.Add(new(k, v));
                    }
                }
            }, scope);
            lock (owner._entries)
            {
                owner._entries.Add(new Entry(category, logLevel, formatter(state, exception), exception, scope));
            }
        }
    }
}
