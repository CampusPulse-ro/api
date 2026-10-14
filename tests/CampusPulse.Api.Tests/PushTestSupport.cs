using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace CampusPulse.Api.Tests;

/// <summary>
/// Stands in for Expo's push API: records every request and answers through <see cref="Respond"/>.
/// By default every message is accepted, with a new ticket id per token (<see cref="Tickets"/>),
/// and no receipt is ready yet.
/// </summary>
public sealed class StubPushHandler : HttpMessageHandler
{
    public sealed record Call(HttpMethod Method, string Path, string? Authorization, JsonElement Body);

    private readonly List<Call> _calls = [];

    public StubPushHandler() => Respond = Default;

    public Func<Call, HttpResponseMessage> Respond { get; set; }

    /// <summary>The last ticket id handed out for each token.</summary>
    public ConcurrentDictionary<string, string> Tickets { get; } = new();

    public IReadOnlyList<Call> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public IReadOnlyList<Call> Sends => Calls.Where(c => c.Path == "/--/api/v2/push/send").ToList();

    public IReadOnlyList<Call> ReceiptReads => Calls.Where(c => c.Path == "/--/api/v2/push/getReceipts").ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var text = request.Content is null ? "null" : await request.Content.ReadAsStringAsync(cancellationToken);
        var call = new Call(request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(),
            JsonDocument.Parse(text).RootElement.Clone());
        lock (_calls)
        {
            _calls.Add(call);
        }

        return Respond(call);
    }

    // The client factory disposes handlers it rotates out; this one outlives them.
    protected override void Dispose(bool disposing)
    {
    }

    public static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(value) };

    public HttpResponseMessage Default(Call call) => call.Path switch
    {
        "/--/api/v2/push/send" => AcceptAll(call),
        "/--/api/v2/push/getReceipts" => Json(new { data = new Dictionary<string, object>() }),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    };

    public HttpResponseMessage AcceptAll(Call call) => Json(new
    {
        data = call.Body.EnumerateArray().Select(message => (object)new { status = "ok", id = NewTicket(message) }).ToArray(),
    });

    /// <summary>Accepts every message except those to <paramref name="gone"/>, refused the way Expo
    /// does it: its message text quotes the token, its code is in details.error.</summary>
    public HttpResponseMessage AcceptAllBut(Call call, ISet<string> gone) => Json(new
    {
        data = call.Body.EnumerateArray().Select(message =>
        {
            var to = message.GetProperty("to").GetString()!;
            return gone.Contains(to)
                ? new { status = "error", message = $"\"{to}\" is not a registered push notification recipient", details = new { error = "DeviceNotRegistered" } }
                : (object)new { status = "ok", id = NewTicket(message) };
        }).ToArray(),
    });

    private string NewTicket(JsonElement message)
    {
        var id = Guid.NewGuid().ToString();
        Tickets[message.GetProperty("to").GetString()!] = id;
        return id;
    }
}

/// <summary>The default for every HttpClient in the Postgres tests: fails instead of reaching the
/// network. A test that sends gives the client a <see cref="StubPushHandler"/>.</summary>
public sealed class NoNetworkHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Tests never reach the network: give this client a stub handler.");
}

/// <summary>A clock the test moves by hand.</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

public static class PushTesting
{
    public static string NewToken() => $"ExponentPushToken[{Guid.NewGuid():N}]";

    public static string NewOid() => "oid-" + Guid.NewGuid().ToString("N");

    /// <summary>Every HttpClient made by the factory gets <paramref name="handler"/>: no test reaches
    /// the network.</summary>
    public static void UseHandler(IServiceCollection services, HttpMessageHandler handler) =>
        services.ConfigureAll<HttpClientFactoryOptions>(options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = handler));

    /// <summary>The app on <paramref name="factory"/>'s database with Expo stubbed, the app's clock
    /// set to <paramref name="clock"/> (tokens are still checked against the real one), and
    /// optionally a log collector.</summary>
    public static WebApplicationFactory<Program> With(this PostgresAppFactory factory, StubPushHandler expo, TestClock clock,
        ILoggerProvider? logs = null) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            UseHandler(services, expo);
            services.AddSingleton<TimeProvider>(clock);
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,
                options => options.TimeProvider = TimeProvider.System);
            if (logs is not null)
            {
                services.AddSingleton<ILoggerProvider>(logs);
            }
        }));

    public static HttpRequestMessage Request(HttpMethod method, string path, string oid, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token(oid: oid));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    /// <summary>What the app does after an interactive sign-in; returns the student's id.</summary>
    public static async Task<Guid> SignIn(HttpClient client, string oid)
    {
        var response = await client.SendAsync(Request(HttpMethod.Post, "/me", oid));
        Assert.True(response.IsSuccessStatusCode, $"POST /me: {(int)response.StatusCode}");
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    public static Task<HttpResponseMessage> Register(HttpClient client, string oid, string token, string platform = "android") =>
        client.SendAsync(Request(HttpMethod.Post, "/me/devices", oid, new { token, platform }));
}
