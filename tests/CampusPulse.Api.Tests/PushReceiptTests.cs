using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CampusPulse.Api.Push;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CampusPulse.Api.Tests.PushTesting;

namespace CampusPulse.Api.Tests;

/// <summary>The receipt check, one pass at a time on a hand-moved clock. These tests share a
/// database, so each starts by clearing the pending receipts.</summary>
public sealed class PushReceiptTests(PostgresAppFactory factory) : IClassFixture<PostgresAppFactory>
{
    private static readonly DateTimeOffset Start = new(2030, 6, 1, 8, 0, 0, TimeSpan.Zero);

    private async Task ClearPending()
    {
        await using var db = factory.CreateDbContext();
        await db.PushReceiptsPending.ExecuteDeleteAsync();
    }

    /// <summary>A student with <paramref name="count"/> devices who has sent a test notification to
    /// all of them at <see cref="Start"/>.</summary>
    private static async Task<(string Oid, Guid UserId, List<string> Tokens)> StudentWithSentTest(HttpClient client, int count)
    {
        var oid = NewOid();
        var userId = await SignIn(client, oid);
        var tokens = Enumerable.Range(0, count).Select(_ => NewToken()).ToList();
        foreach (var token in tokens)
        {
            Assert.Equal(HttpStatusCode.Created, (await Register(client, oid, token)).StatusCode);
        }

        var sent = await client.SendAsync(Request(HttpMethod.Post, "/me/notifications/test", oid));
        Assert.Equal(count, (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sent").GetInt32());
        return (oid, userId, tokens);
    }

    [PostgresFact]
    public async Task Receipts_are_read_after_15_minutes_and_acted_on_and_unanswered_ones_expire_after_a_day()
    {
        await ClearPending();
        var expo = new StubPushHandler();
        var clock = new TestClock(Start);
        using var app = factory.With(expo, clock);
        var checker = app.Services.GetRequiredService<PushReceiptChecker>();
        var (_, _, tokens) = await StudentWithSentTest(app.CreateClient(), 3);
        var (delivered, gone, unanswered) = (tokens[0], tokens[1], tokens[2]);

        // Too young: nothing is asked.
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(PushReceiptCheck.Nothing, await checker.CheckOnceAsync(default));
        Assert.Empty(expo.ReceiptReads);

        clock.Advance(TimeSpan.FromMinutes(6));
        expo.Respond = call => StubPushHandler.Json(new
        {
            data = new Dictionary<string, object>
            {
                [expo.Tickets[delivered]] = new { status = "ok" },
                [expo.Tickets[gone]] = new { status = "error", message = $"\"{gone}\" is not a registered push notification recipient", details = new { error = "DeviceNotRegistered" } },
            },
        });
        var check = await checker.CheckOnceAsync(default);

        Assert.Equal(new PushReceiptCheck(Requested: 3, Answered: 2, DevicesRemoved: 1, Expired: 0), check);
        var read = Assert.Single(expo.ReceiptReads);
        Assert.Equal(3, read.Body.GetProperty("ids").GetArrayLength());
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal([expo.Tickets[unanswered]], await db.PushReceiptsPending.Select(r => r.TicketId).ToListAsync());
            Assert.False(await db.PushDevices.AnyAsync(d => d.Token == gone));
            Assert.True(await db.PushDevices.AnyAsync(d => d.Token == delivered));
        }

        // A day later Expo has forgotten the ticket: the row is dropped unread.
        clock.Advance(TimeSpan.FromHours(24));
        var later = await checker.CheckOnceAsync(default);

        Assert.Equal(1, later.Expired);
        Assert.Single(expo.ReceiptReads);
        await using (var db = factory.CreateDbContext())
        {
            Assert.False(await db.PushReceiptsPending.AnyAsync());
        }
    }

    [PostgresFact]
    public async Task With_nothing_pending_a_pass_does_nothing()
    {
        await ClearPending();
        var expo = new StubPushHandler();
        using var app = factory.With(expo, new TestClock(Start));

        var check = await app.Services.GetRequiredService<PushReceiptChecker>().CheckOnceAsync(default);

        Assert.Equal(PushReceiptCheck.Nothing, check);
        Assert.Empty(expo.Calls);
    }

    [PostgresFact]
    public async Task When_expo_cannot_answer_the_rows_stay_for_the_next_pass()
    {
        await ClearPending();
        var expo = new StubPushHandler();
        var clock = new TestClock(Start);
        using var app = factory.With(expo, clock);
        var (_, _, tokens) = await StudentWithSentTest(app.CreateClient(), 1);
        clock.Advance(TimeSpan.FromMinutes(20));
        expo.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);

        await Assert.ThrowsAsync<PushServiceException>(() =>
            app.Services.GetRequiredService<PushReceiptChecker>().CheckOnceAsync(default));

        await using var db = factory.CreateDbContext();
        Assert.Equal([expo.Tickets[tokens[0]]], await db.PushReceiptsPending.Select(r => r.TicketId).ToListAsync());
    }

    /// <summary>Everything push does, with Expo quoting tokens in its error texts: no log line or
    /// scope value carries a token, an oid, a user id or an email.</summary>
    [PostgresFact]
    public async Task No_log_line_names_a_token_or_a_student()
    {
        await ClearPending();
        var logs = new CollectingLoggerProvider();
        var expo = new StubPushHandler();
        var clock = new TestClock(Start);
        using var app = factory.With(expo, clock, logs);
        var client = app.CreateClient();
        var (oid, userId, tokens) = await StudentWithSentTest(client, 2);
        var other = NewOid();
        var otherId = await SignIn(client, other);
        await Register(client, other, tokens[0]);                                     // a move
        await Register(client, oid, "ExponentPushToken[not valid]");                  // a refusal
        expo.Respond = call => expo.AcceptAllBut(call, new HashSet<string> { tokens[1] });
        clock.Advance(TimeSpan.FromMinutes(2));
        await client.SendAsync(Request(HttpMethod.Post, "/me/notifications/test", oid)); // a DeviceNotRegistered ticket
        clock.Advance(TimeSpan.FromMinutes(20));
        expo.Respond = call => StubPushHandler.Json(new
        {
            data = call.Body.GetProperty("ids").EnumerateArray().ToDictionary(id => id.GetString()!,
                _ => (object)new { status = "error", message = $"\"{tokens[0]}\" is not a registered push notification recipient", details = new { error = "DeviceNotRegistered" } }),
        });
        await app.Services.GetRequiredService<PushReceiptChecker>().CheckOnceAsync(default);
        await client.SendAsync(Request(HttpMethod.Delete, "/me/devices", other, new { token = tokens[0] }));

        Assert.Contains(logs.Entries, e => e.Message.StartsWith("Push receipts:", StringComparison.Ordinal));
        Assert.Contains(logs.Entries, e => e.Message.Contains("DeviceNotRegistered", StringComparison.Ordinal));
        var secrets = tokens.Concat(["not valid", oid, other, userId.ToString(), otherId.ToString(), "ana@student.upt.ro"]).ToList();
        Assert.DoesNotContain(logs.Entries, e => secrets.Any(secret =>
            e.Message.Contains(secret, StringComparison.OrdinalIgnoreCase)
            || (e.Exception?.ToString().Contains(secret, StringComparison.OrdinalIgnoreCase) ?? false)
            || e.ScopeValues.Any(kv => kv.Value?.ToString()?.Contains(secret, StringComparison.OrdinalIgnoreCase) == true)));
    }
}
