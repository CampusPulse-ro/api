using System.Net;
using CampusPulse.Api.Push;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CampusPulse.Api.Tests;

/// <summary>The Expo client on a stub handler: batching, the ticket and receipt shapes, failures.</summary>
public sealed class ExpoPushSenderTests
{
    private static (ExpoPushSender Sender, StubPushHandler Expo) Create(ILogger<ExpoPushSender>? logger = null,
        TimeSpan? timeout = null)
    {
        var expo = new StubPushHandler();
        var client = new HttpClient(expo) { BaseAddress = ExpoPushSender.BaseAddress, Timeout = timeout ?? ExpoPushSender.Timeout };
        return (new ExpoPushSender(client, logger ?? NullLogger<ExpoPushSender>.Instance), expo);
    }

    private static (ILogger<ExpoPushSender> Logger, CollectingLoggerProvider Logs) Collecting()
    {
        var logs = new CollectingLoggerProvider();
        return (LoggerFactory.Create(builder => builder.AddProvider(logs)).CreateLogger<ExpoPushSender>(), logs);
    }

    private static List<PushMessage> Messages(int count) =>
        Enumerable.Range(0, count).Select(_ => new PushMessage(PushTesting.NewToken(), "T", "B")).ToList();

    [Fact]
    public async Task Messages_go_in_batches_of_100_and_tickets_come_back_in_order()
    {
        var (sender, expo) = Create();
        var messages = Messages(250);

        var tickets = await sender.SendAsync(messages, default);

        Assert.Equal([100, 100, 50], expo.Sends.Select(s => s.Body.GetArrayLength()));
        Assert.Equal(messages.Select(m => expo.Tickets[m.To]), tickets.Select(t => t.Id!));
        var first = expo.Sends[0].Body[0];
        Assert.Equal(messages[0].To, first.GetProperty("to").GetString());
        Assert.Equal("T", first.GetProperty("title").GetString());
        Assert.Equal("B", first.GetProperty("body").GetString());
        Assert.Equal("default", first.GetProperty("sound").GetString());
    }

    /// <summary>Data, priority and time to live go out only when set; otherwise Expo applies each platform's default.</summary>
    [Fact]
    public async Task Data_priority_and_time_to_live_are_sent_only_when_set()
    {
        var (sender, expo) = Create();
        var plain = new PushMessage(PushTesting.NewToken(), "T", "B");
        var reminder = new PushMessage(PushTesting.NewToken(), "T", "B")
        {
            Data = new Dictionary<string, string> { ["type"] = "classReminder", ["date"] = "2030-11-25" },
            HighPriority = true,
            TimeToLive = TimeSpan.FromSeconds(869.2),
        };

        await sender.SendAsync([plain, reminder], default);

        var (first, second) = (expo.Sends[0].Body[0], expo.Sends[0].Body[1]);
        Assert.Equal(["to", "title", "body", "sound"], first.EnumerateObject().Select(p => p.Name));
        Assert.Equal("classReminder", second.GetProperty("data").GetProperty("type").GetString());
        Assert.Equal("2030-11-25", second.GetProperty("data").GetProperty("date").GetString());
        Assert.Equal("high", second.GetProperty("priority").GetString());
        Assert.Equal(870, second.GetProperty("ttl").GetInt32());
    }

    [Fact]
    public async Task An_error_ticket_keeps_only_expos_code()
    {
        var (sender, expo) = Create();
        var messages = Messages(3);
        expo.Respond = _ => StubPushHandler.Json(new
        {
            data = new object[]
            {
                new { status = "error", message = $"\"{messages[0].To}\" is not a registered push notification recipient", details = new { error = "DeviceNotRegistered" } },
                new { status = "error", message = "too big", details = new { error = "Message Too Big; drop table" } },
                new { status = "ok", id = "1f3ad4e0-0000-0000-0000-000000000000" },
            },
        });

        var tickets = await sender.SendAsync(messages, default);

        Assert.Equal(new PushTicket(null, PushErrors.DeviceNotRegistered), tickets[0]);
        Assert.Equal(new PushTicket(null, PushErrors.Unknown), tickets[1]);
        Assert.Equal(new PushTicket("1f3ad4e0-0000-0000-0000-000000000000", null), tickets[2]);
    }

    [Fact]
    public async Task A_failed_batch_refuses_only_its_messages()
    {
        var (sender, expo) = Create();
        var batches = 0;
        expo.Respond = call => Interlocked.Increment(ref batches) == 2
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : expo.AcceptAll(call);

        var tickets = await sender.SendAsync(Messages(150), default);

        Assert.All(tickets.Take(100), t => Assert.True(t.Ok));
        Assert.All(tickets.Skip(100), t => Assert.Equal(PushErrors.RequestFailed, t.Error));
    }

    [Fact]
    public async Task A_timeout_refuses_the_batch_as_a_failed_request()
    {
        var (sender, expo) = Create(timeout: TimeSpan.FromMilliseconds(100));
        expo.Delay = TimeSpan.FromSeconds(30);

        var tickets = await sender.SendAsync(Messages(2), default);

        Assert.All(tickets, t => Assert.Equal(PushErrors.RequestFailed, t.Error));
    }

    [Fact]
    public async Task An_unreachable_service_refuses_the_batch_as_a_failed_request()
    {
        var (sender, expo) = Create();
        expo.Respond = _ => throw new HttpRequestException("Connection refused");

        var tickets = await sender.SendAsync(Messages(2), default);

        Assert.All(tickets, t => Assert.Equal(PushErrors.RequestFailed, t.Error));
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_malformed()
    {
        var (sender, expo) = Create();
        expo.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>busy</html>") };

        var tickets = await sender.SendAsync(Messages(2), default);

        Assert.All(tickets, t => Assert.Equal(PushErrors.MalformedResponse, t.Error));
    }

    /// <summary>Expo refuses a whole request with error codes, and its details list the tokens: the
    /// codes are logged, the tokens never.</summary>
    [Theory]
    [InlineData(400)]
    [InlineData(200)]
    public async Task A_refused_request_logs_expos_error_codes_and_never_its_details(int status)
    {
        var (logger, logs) = Collecting();
        var (sender, expo) = Create(logger);
        var messages = Messages(2);
        expo.Respond = _ => StubPushHandler.Json(new
        {
            errors = new object[]
            {
                new { code = "PUSH_TOO_MANY_EXPERIENCE_IDS", message = $"Tokens: {messages[0].To}", details = new { a = new[] { messages[0].To }, b = new[] { messages[1].To } } },
                new { code = $"weird {messages[1].To}" },
            },
        }, (HttpStatusCode)status);

        var tickets = await sender.SendAsync(messages, default);

        Assert.All(tickets, t => Assert.Equal(PushErrors.RequestFailed, t.Error));
        var line = Assert.Single(logs.Entries);
        Assert.Contains("PUSH_TOO_MANY_EXPERIENCE_IDS", line.Message);
        Assert.Contains(PushErrors.Unknown, line.Message);
        Assert.DoesNotContain(logs.Entries, e => messages.Any(m => e.Message.Contains(m.To, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_refused_receipt_read_names_the_codes_and_never_the_details()
    {
        var (sender, expo) = Create();
        var token = PushTesting.NewToken();
        expo.Respond = _ => StubPushHandler.Json(new
        {
            errors = new[] { new { code = "PUSH_TOO_MANY_RECEIPTS", message = token, details = new { token } } },
        }, HttpStatusCode.BadRequest);

        var e = await Assert.ThrowsAsync<PushServiceException>(() => sender.GetReceiptsAsync(["ticket-1"], default));

        Assert.Contains("PUSH_TOO_MANY_RECEIPTS", e.Message);
        Assert.DoesNotContain(token, e.Message);
    }

    [Fact]
    public async Task An_answer_that_does_not_match_the_batch_is_malformed()
    {
        var (sender, expo) = Create();
        expo.Respond = _ => StubPushHandler.Json(new { data = new[] { new { status = "ok", id = "only-one" } } });

        var tickets = await sender.SendAsync(Messages(2), default);

        Assert.All(tickets, t => Assert.Equal(PushErrors.MalformedResponse, t.Error));
    }

    [Fact]
    public async Task Receipts_are_read_in_batches_of_300_and_missing_ones_are_not_ready()
    {
        var (sender, expo) = Create();
        var ids = Enumerable.Range(0, 650).Select(i => $"ticket-{i}").ToList();
        expo.Respond = call => StubPushHandler.Json(new
        {
            data = call.Body.GetProperty("ids").EnumerateArray()
                .Select(id => id.GetString()!)
                .Where(id => id != "ticket-7")
                .ToDictionary(id => id, id => id == "ticket-8"
                    ? (object)new { status = "error", details = new { error = "DeviceNotRegistered" } }
                    : new { status = "ok" }),
        });

        var receipts = await sender.GetReceiptsAsync(ids, default);

        Assert.Equal([300, 300, 50], expo.ReceiptReads.Select(r => r.Body.GetProperty("ids").GetArrayLength()));
        Assert.Equal(649, receipts.Count);
        Assert.False(receipts.ContainsKey("ticket-7"));
        Assert.Equal(PushErrors.DeviceNotRegistered, receipts["ticket-8"].Error);
        Assert.True(receipts["ticket-9"].Ok);
    }

    public static TheoryData<string> UnreadableReceipts => new() { "429", "data is an array", "not JSON", "timeout", "unreachable" };

    [Theory]
    [MemberData(nameof(UnreadableReceipts))]
    public async Task Receipts_that_cannot_be_read_throw(string answer)
    {
        var (sender, expo) = Create(timeout: TimeSpan.FromMilliseconds(answer == "timeout" ? 100 : 15_000));
        expo.Respond = answer switch
        {
            "429" => _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            "data is an array" => _ => StubPushHandler.Json(new { data = new[] { 1, 2 } }),
            "not JSON" => _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("busy") },
            "unreachable" => _ => throw new HttpRequestException("Connection refused"),
            _ => expo.Default,
        };
        expo.Delay = answer == "timeout" ? TimeSpan.FromSeconds(30) : null;

        await Assert.ThrowsAsync<PushServiceException>(() => sender.GetReceiptsAsync(["ticket-1"], default));
    }
}

/// <summary>How the app sets the Expo client up, without a database.</summary>
public sealed class PushSetupTests
{
    private static IServiceProvider Services(CampusPulseAppFactory app, string? accessToken) =>
        accessToken is null
            ? app.Services
            : app.WithWebHostBuilder(builder => builder.UseSetting("Push:Expo:AccessToken", accessToken)).Services;

    /// <summary>
    /// The handler keeps the default proxy (<c>UseProxy</c> on, no <c>Proxy</c> of its own), so it
    /// uses <see cref="HttpClient.DefaultProxy"/>: on Linux, <c>HTTPS_PROXY</c> / <c>NO_PROXY</c>.
    /// That's how the API reaches Expo through the Pi's egress proxy.
    /// </summary>
    [Fact]
    public void The_expo_client_uses_the_default_proxy_and_has_a_timeout()
    {
        using var app = new CampusPulseAppFactory();
        var services = Services(app, null);

        var handler = services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(nameof(IPushSender));
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        var (useProxy, proxy) = handler switch
        {
            HttpClientHandler h => (h.UseProxy, h.Proxy),
            SocketsHttpHandler s => (s.UseProxy, s.Proxy),
            _ => throw new InvalidOperationException($"Unexpected primary handler {handler.GetType()}"),
        };
        Assert.True(useProxy);
        Assert.Null(proxy);

        var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IPushSender));
        Assert.Equal(ExpoPushSender.Timeout, client.Timeout);
        Assert.Equal(ExpoPushSender.BaseAddress, client.BaseAddress);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public void An_access_token_is_sent_as_bearer()
    {
        using var app = new CampusPulseAppFactory();

        var client = Services(app, "expo-access-token-for-tests").GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IPushSender));

        Assert.Equal("Bearer", client.DefaultRequestHeaders.Authorization?.Scheme);
        Assert.Equal("expo-access-token-for-tests", client.DefaultRequestHeaders.Authorization?.Parameter);
    }
}

public sealed class TestNotificationLimitTests
{
    private static readonly DateTimeOffset Start = new(2030, 1, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_slot_is_held_for_a_minute_then_free_again()
    {
        var clock = new TestClock(Start);
        var limit = new TestNotificationLimit(clock);
        var student = Guid.NewGuid();

        Assert.True(limit.TryStart(student, out _));
        clock.Advance(TimeSpan.FromSeconds(45));
        Assert.False(limit.TryStart(student, out var retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(15), retryAfter);
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.True(limit.TryStart(student, out _));
    }

    [Fact]
    public void A_released_slot_can_be_taken_again_at_once()
    {
        var limit = new TestNotificationLimit(new TestClock(Start));
        var student = Guid.NewGuid();
        Assert.True(limit.TryStart(student, out _));

        limit.Release(student);

        Assert.True(limit.TryStart(student, out _));
    }

    /// <summary>Nothing about a student outlives the minute: expired entries go on the next call,
    /// however few students there are (a deleted account's id included).</summary>
    [Fact]
    public void Expired_entries_are_dropped_on_every_call()
    {
        var clock = new TestClock(Start);
        var limit = new TestNotificationLimit(clock);
        Assert.True(limit.TryStart(Guid.NewGuid(), out _));
        Assert.True(limit.TryStart(Guid.NewGuid(), out _));

        clock.Advance(TestNotificationLimit.Window);
        Assert.True(limit.TryStart(Guid.NewGuid(), out _));

        Assert.Equal(1, limit.Count);
    }
}
