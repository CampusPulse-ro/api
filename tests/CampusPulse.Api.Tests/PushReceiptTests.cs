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

    private async Task<bool> DeviceExists(string token)
    {
        await using var db = factory.CreateDbContext();
        return await db.PushDevices.AnyAsync(d => d.Token == token);
    }

    private async Task<List<string>> PendingTickets()
    {
        await using var db = factory.CreateDbContext();
        return await db.PushReceiptsPending.Select(r => r.TicketId).ToListAsync();
    }

    /// <summary>A student with <paramref name="count"/> devices registered a minute before
    /// <paramref name="clock"/>'s now, who then sends a test notification to all of them.</summary>
    private static async Task<(string Oid, Guid UserId, List<string> Tokens)> StudentWithSentTest(HttpClient client,
        TestClock clock, int count)
    {
        var oid = NewOid();
        var userId = await SignIn(client, oid);
        var tokens = Enumerable.Range(0, count).Select(_ => NewToken()).ToList();
        foreach (var token in tokens)
        {
            Assert.Equal(HttpStatusCode.Created, (await Register(client, oid, token)).StatusCode);
        }

        clock.Advance(TimeSpan.FromMinutes(1));
        var sent = await client.SendAsync(Request(HttpMethod.Post, "/me/notifications/test", oid));
        Assert.Equal(count, (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sent").GetInt32());
        return (oid, userId, tokens);
    }

    private static object NotRegistered(string token) =>
        new { status = "error", message = $"\"{token}\" is not a registered push notification recipient", details = new { error = "DeviceNotRegistered" } };

    [PostgresFact]
    public async Task Receipts_are_read_after_15_minutes_and_acted_on_and_unanswered_ones_expire_after_a_day()
    {
        await ClearPending();
        var expo = new StubPushHandler();
        var clock = new TestClock(Start);
        using var app = factory.With(expo, clock);
        var checker = app.Services.GetRequiredService<PushReceiptChecker>();
        var (_, _, tokens) = await StudentWithSentTest(app.CreateClient(), clock, 3);
        var (delivered, gone, unanswered) = (tokens[0], tokens[1], tokens[2]);

        // Too young: nothing is asked.
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(PushReceiptCheck.Nothing, await checker.CheckOnceAsync(default));
        Assert.Empty(expo.ReceiptReads);

        clock.Advance(TimeSpan.FromMinutes(6));
        expo.Respond = _ => StubPushHandler.Json(new
        {
            data = new Dictionary<string, object>
            {
                [expo.Tickets[delivered]] = new { status = "ok" },
                [expo.Tickets[gone]] = NotRegistered(gone),
            },
        });
        var check = await checker.CheckOnceAsync(default);

        Assert.Equal(new PushReceiptCheck(Requested: 3, Answered: 2, DevicesRemoved: 1, Expired: 0), check);
        var read = Assert.Single(expo.ReceiptReads);
        Assert.Equal(3, read.Body.GetProperty("ids").GetArrayLength());
        Assert.Equal([expo.Tickets[unanswered]], await PendingTickets());
        Assert.False(await DeviceExists(gone));
        Assert.True(await DeviceExists(delivered));

        // A day later Expo has forgotten the ticket: the row is dropped unread.
        clock.Advance(TimeSpan.FromHours(24));
        var later = await checker.CheckOnceAsync(default);

        Assert.Equal(1, later.Expired);
        Assert.Single(expo.ReceiptReads);
        Assert.Empty(await PendingTickets());
    }

    /// <summary>The app was reinstalled and registered the same token again after the message was
    /// sent: the late DeviceNotRegistered receipt is about the old install.</summary>
    [PostgresFact]
    public async Task A_device_registered_again_after_the_send_survives_a_device_not_registered_receipt()
    {
        await ClearPending();
        var expo = new StubPushHandler();
        var clock = new TestClock(Start);
        using var app = factory.With(expo, clock);
        var client = app.CreateClient();
        var (oid, _, tokens) = await StudentWithSentTest(client, clock, 1);
        var token = tokens[0];
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(HttpStatusCode.OK, (await Register(client, oid, token)).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(11));
        expo.Respond = _ => StubPushHandler.Json(new { data = new Dictionary<string, object> { [expo.Tickets[token]] = NotRegistered(token) } });

        var check = await app.Services.GetRequiredService<PushReceiptChecker>().CheckOnceAsync(default);

        Assert.Equal(new PushReceiptCheck(Requested: 1, Answered: 1, DevicesRemoved: 0, Expired: 0), check);
        Assert.True(await DeviceExists(token));
        Assert.Empty(await PendingTickets());
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

    public static TheoryData<string> UnreadableAnswers => new() { "502", "data is an array", "not JSON" };

    [PostgresTheory]
    [MemberData(nameof(UnreadableAnswers))]
    public async Task When_expo_cannot_answer_the_rows_and_devices_stay_for_the_next_pass(string answer)
    {
        await ClearPending();
        var expo = new StubPushHandler();
        var clock = new TestClock(Start);
        using var app = factory.With(expo, clock);
        var (_, _, tokens) = await StudentWithSentTest(app.CreateClient(), clock, 1);
        clock.Advance(TimeSpan.FromMinutes(20));
        expo.Respond = answer switch
        {
            "502" => _ => new HttpResponseMessage(HttpStatusCode.BadGateway),
            "data is an array" => _ => StubPushHandler.Json(new { data = new[] { 1, 2, 3 } }),
            _ => _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>busy</html>") },
        };

        var check = await app.Services.GetRequiredService<PushReceiptChecker>().CheckOnceAsync(default);

        Assert.Equal(new PushReceiptCheck(Requested: 1, Answered: 0, DevicesRemoved: 0, Expired: 0, FailedBatches: 1), check);
        Assert.Equal([expo.Tickets[tokens[0]]], await PendingTickets());
        Assert.True(await DeviceExists(tokens[0]));
    }

    /// <summary>A pass reads in batches; one that fails doesn't undo what an earlier one read.</summary>
    [PostgresFact]
    public async Task A_failing_batch_keeps_its_rows_and_does_not_discard_a_batch_already_read()
    {
        await ClearPending();
        var expo = new StubPushHandler();
        var clock = new TestClock(Start);
        using var app = factory.With(expo, clock);
        var checker = app.Services.GetRequiredService<PushReceiptChecker>();
        checker.BatchSize = 2;
        var (_, _, tokens) = await StudentWithSentTest(app.CreateClient(), clock, 3);
        clock.Advance(TimeSpan.FromMinutes(20));
        var byTicket = tokens.ToDictionary(t => expo.Tickets[t]);
        var reads = 0;
        expo.Respond = call => Interlocked.Increment(ref reads) == 1
            ? StubPushHandler.Json(new
            {
                data = call.Body.GetProperty("ids").EnumerateArray().ToDictionary(id => id.GetString()!,
                    id => NotRegistered(byTicket[id.GetString()!])),
            })
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        var check = await checker.CheckOnceAsync(default);

        Assert.Equal(new PushReceiptCheck(Requested: 3, Answered: 2, DevicesRemoved: 2, Expired: 0, FailedBatches: 1), check);
        var firstBatch = expo.ReceiptReads[0].Body.GetProperty("ids").EnumerateArray().Select(id => id.GetString()!).ToList();
        var secondBatch = expo.ReceiptReads[1].Body.GetProperty("ids").EnumerateArray().Select(id => id.GetString()!).ToList();
        Assert.Equal(2, firstBatch.Count);
        Assert.Equal(secondBatch, await PendingTickets());
        foreach (var ticket in firstBatch)
        {
            Assert.False(await DeviceExists(byTicket[ticket]));
        }

        Assert.True(await DeviceExists(byTicket[Assert.Single(secondBatch)]));
    }

    /// <summary>Everything push does, with Expo quoting tokens in its error texts and request errors
    /// and EF logging a unique-violation race at Error: no log line, exception or scope value carries
    /// a token, an oid, a user id or an email. (Turning on Npgsql's "Include Error Detail" would put
    /// a token into the race's exception: this test is what stops that.)</summary>
    [PostgresFact]
    public async Task No_log_line_names_a_token_or_a_student()
    {
        await ClearPending();
        var logs = new CollectingLoggerProvider();
        var expo = new StubPushHandler();
        var clock = new TestClock(Start);
        var other = NewOid();
        var raced = NewToken();
        Guid otherId;
        using (var setup = factory.With(expo, clock))
        {
            otherId = await SignIn(setup.CreateClient(), other);
        }

        var rival = new PushTests.RivalInsertsFirst(factory, raced, otherId);
        using var app = factory.With(expo, clock, logs, rival);
        var client = app.CreateClient();
        var (oid, userId, tokens) = await StudentWithSentTest(client, clock, 2);
        Assert.Equal(HttpStatusCode.OK, (await Register(client, oid, raced)).StatusCode);  // the race, then a move
        Assert.Equal(1, rival.Fired);
        await Register(client, other, tokens[0]);                                          // a move
        await Register(client, oid, "ExponentPushToken[not valid]");                       // a refusal
        await client.SendAsync(RawBody(oid, $$"""{"token":"{{tokens[1]}}","platform":7}""")); // a 400 body

        // Expo refuses a whole request, listing the request's tokens in the error's details.
        expo.Respond = _ => StubPushHandler.Json(new
        {
            errors = new[] { new { code = "PUSH_TOO_MANY_EXPERIENCE_IDS", message = $"Tokens {tokens[1]}, {raced}", details = new { a = new[] { tokens[1] }, b = new[] { raced } } } },
        }, HttpStatusCode.BadRequest);
        clock.Advance(TimeSpan.FromMinutes(2));
        await client.SendAsync(Request(HttpMethod.Post, "/me/notifications/test", oid));

        expo.Respond = call => expo.AcceptAllBut(call, new HashSet<string> { tokens[1] });
        await client.SendAsync(Request(HttpMethod.Post, "/me/notifications/test", oid));  // a DeviceNotRegistered ticket

        clock.Advance(TimeSpan.FromMinutes(20));
        expo.Respond = call => StubPushHandler.Json(new
        {
            data = call.Body.GetProperty("ids").EnumerateArray().ToDictionary(id => id.GetString()!, _ => NotRegistered(tokens[0])),
        });
        await app.Services.GetRequiredService<PushReceiptChecker>().CheckOnceAsync(default);
        await client.SendAsync(Request(HttpMethod.Delete, "/me/devices", other, new { token = tokens[0] }));

        Assert.Contains(logs.Entries, e => e.Message.StartsWith("Push receipts:", StringComparison.Ordinal));
        Assert.Contains(logs.Entries, e => e.Message.Contains("DeviceNotRegistered", StringComparison.Ordinal));
        Assert.Contains(logs.Entries, e => e.Message.Contains("PUSH_TOO_MANY_EXPERIENCE_IDS", StringComparison.Ordinal));
        Assert.Contains(logs.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error && e.Exception is DbUpdateException);
        var secrets = tokens.Concat([raced, "not valid", oid, other, userId.ToString(), otherId.ToString(), "ana@student.upt.ro"]).ToList();
        Assert.DoesNotContain(logs.Entries, e => secrets.Any(secret =>
            e.Message.Contains(secret, StringComparison.OrdinalIgnoreCase)
            || (e.Exception?.ToString().Contains(secret, StringComparison.OrdinalIgnoreCase) ?? false)
            || e.ScopeValues.Any(kv => kv.Value?.ToString()?.Contains(secret, StringComparison.OrdinalIgnoreCase) == true)));
    }

    private static HttpRequestMessage RawBody(string oid, string json)
    {
        var request = Request(HttpMethod.Post, "/me/devices", oid);
        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        return request;
    }
}
