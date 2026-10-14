using System.Net;
using CampusPulse.Api.Push;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CampusPulse.Api.Tests;

/// <summary>The Expo client on a stub handler: batching, the ticket and receipt shapes, failures.</summary>
public sealed class ExpoPushSenderTests
{
    private static (ExpoPushSender Sender, StubPushHandler Expo) Create()
    {
        var expo = new StubPushHandler();
        var client = new HttpClient(expo) { BaseAddress = ExpoPushSender.BaseAddress };
        return (new ExpoPushSender(client, NullLogger<ExpoPushSender>.Instance), expo);
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

    [Fact]
    public async Task Receipts_that_cannot_be_read_throw()
    {
        var (sender, expo) = Create();
        expo.Respond = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests);

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
