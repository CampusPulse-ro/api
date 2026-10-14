using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CampusPulse.Api.Push;
using CampusPulse.Domain.Push;
using Microsoft.EntityFrameworkCore;
using static CampusPulse.Api.Tests.PushTesting;

namespace CampusPulse.Api.Tests;

/// <summary>Sign-in configured, real Postgres, Expo stubbed: registering and forgetting devices, and
/// the test notification.</summary>
public sealed class PushTests(PostgresAppFactory factory) : IClassFixture<PostgresAppFactory>
{
    private static readonly DateTimeOffset Start = new(2030, 1, 1, 8, 0, 0, TimeSpan.Zero);

    private async Task<PushDevice?> Device(string token)
    {
        await using var db = factory.CreateDbContext();
        return await db.PushDevices.SingleOrDefaultAsync(d => d.Token == token);
    }

    [PostgresFact]
    public async Task A_new_token_is_201_and_the_same_token_again_is_200_with_a_fresh_registration_time()
    {
        var clock = new TestClock(Start);
        using var app = factory.With(new StubPushHandler(), clock);
        var client = app.CreateClient();
        var oid = NewOid();
        var userId = await SignIn(client, oid);
        var token = NewToken();

        var first = await Register(client, oid, token);
        clock.Advance(TimeSpan.FromHours(1));
        var again = await Register(client, oid, token, platform: "ios");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var body = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ios", body.GetProperty("platform").GetString());
        var device = await Device(token);
        Assert.Equal(userId, device!.UserId);
        Assert.Equal(PushPlatform.Ios, device.Platform);
        Assert.Equal(Start, device.CreatedAt);
        Assert.Equal(Start.AddHours(1), device.LastRegisteredAt);
    }

    [PostgresFact]
    public async Task A_token_registered_by_another_student_moves_to_them()
    {
        using var app = factory.With(new StubPushHandler(), new TestClock(Start));
        var client = app.CreateClient();
        var (ana, ion) = (NewOid(), NewOid());
        await SignIn(client, ana);
        var ionId = await SignIn(client, ion);
        var token = NewToken();
        Assert.Equal(HttpStatusCode.Created, (await Register(client, ana, token)).StatusCode);

        var moved = await Register(client, ion, token);

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal(ionId, (await Device(token))!.UserId);
    }

    [PostgresFact]
    public async Task An_eleventh_device_drops_the_least_recently_registered()
    {
        var clock = new TestClock(Start);
        using var app = factory.With(new StubPushHandler(), clock);
        var client = app.CreateClient();
        var oid = NewOid();
        var userId = await SignIn(client, oid);
        var tokens = Enumerable.Range(0, 11).Select(_ => NewToken()).ToList();
        foreach (var token in tokens.Take(10))
        {
            Assert.Equal(HttpStatusCode.Created, (await Register(client, oid, token)).StatusCode);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        // The first one registers again, so the second is now the least recently registered.
        Assert.Equal(HttpStatusCode.OK, (await Register(client, oid, tokens[0])).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Created, (await Register(client, oid, tokens[10])).StatusCode);

        await using var db = factory.CreateDbContext();
        var kept = await db.PushDevices.Where(d => d.UserId == userId).Select(d => d.Token).ToListAsync();
        Assert.Equal(PushDevice.MaxPerUser, kept.Count);
        Assert.DoesNotContain(tokens[1], kept);
        Assert.Contains(tokens[0], kept);
        Assert.Contains(tokens[10], kept);
    }

    [PostgresFact]
    public async Task Concurrent_registrations_never_leave_more_than_the_cap()
    {
        using var app = factory.With(new StubPushHandler(), new TestClock(Start));
        var client = app.CreateClient();
        var oid = NewOid();
        var userId = await SignIn(client, oid);

        var responses = await Task.WhenAll(Enumerable.Range(0, 14).Select(_ => Register(client, oid, NewToken())));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        await using var db = factory.CreateDbContext();
        Assert.Equal(PushDevice.MaxPerUser, await db.PushDevices.CountAsync(d => d.UserId == userId));
    }

    [PostgresFact]
    public async Task Two_students_registering_one_token_at_once_leave_one_device()
    {
        using var app = factory.With(new StubPushHandler(), new TestClock(Start));
        var client = app.CreateClient();
        var (ana, ion) = (NewOid(), NewOid());
        await SignIn(client, ana);
        await SignIn(client, ion);
        var token = NewToken();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Register(client, i % 2 == 0 ? ana : ion, token)));

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"{(int)r.StatusCode}"));
        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.PushDevices.CountAsync(d => d.Token == token));
    }

    public static TheoryData<string> NotExpoTokens => new()
    {
        "",
        "ExponentPushToken[]",
        "ExponentPushToken[abc]\n",
        "ExponentPushToken[a b]",
        "ExponentPushToken[abc",
        "fcm:dGVzdA",
        "ExponentPushToken[" + new string('a', PushDevice.TokenMaxLength) + "]",
    };

    [PostgresTheory]
    [MemberData(nameof(NotExpoTokens))]
    public async Task Anything_but_an_expo_token_is_400_problem_details_and_never_echoed(string token)
    {
        using var app = factory.With(new StubPushHandler(), new TestClock(Start));
        var client = app.CreateClient();
        var oid = NewOid();
        await SignIn(client, oid);

        var response = await Register(client, oid, token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        if (token.Length > 0)
        {
            Assert.DoesNotContain(token.Trim(), await response.Content.ReadAsStringAsync());
        }

        Assert.Null(await Device(token));
    }

    [PostgresFact]
    public async Task Both_expo_token_prefixes_are_accepted()
    {
        using var app = factory.With(new StubPushHandler(), new TestClock(Start));
        var client = app.CreateClient();
        var oid = NewOid();
        await SignIn(client, oid);

        Assert.Equal(HttpStatusCode.Created, (await Register(client, oid, $"ExpoPushToken[{Guid.NewGuid():N}]")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Register(client, oid, $"ExponentPushToken[a-b_{Guid.NewGuid():N}]")).StatusCode);
    }

    [PostgresFact]
    public async Task An_unknown_platform_is_400()
    {
        using var app = factory.With(new StubPushHandler(), new TestClock(Start));
        var client = app.CreateClient();
        var oid = NewOid();
        await SignIn(client, oid);
        var token = NewToken();

        var response = await Register(client, oid, token, platform: "windows");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await Device(token));
    }

    [PostgresFact]
    public async Task Without_an_account_registering_is_404()
    {
        using var app = factory.With(new StubPushHandler(), new TestClock(Start));
        var token = NewToken();

        var response = await Register(app.CreateClient(), NewOid(), token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(await Device(token));
    }

    [PostgresFact]
    public async Task Deleting_removes_only_your_own_device_and_is_always_204()
    {
        using var app = factory.With(new StubPushHandler(), new TestClock(Start));
        var client = app.CreateClient();
        var (ana, ion) = (NewOid(), NewOid());
        await SignIn(client, ana);
        await SignIn(client, ion);
        var (anas, ions) = (NewToken(), NewToken());
        await Register(client, ana, anas);
        await Register(client, ion, ions);

        var own = await client.SendAsync(Request(HttpMethod.Delete, "/me/devices", ana, new { token = anas }));
        var others = await client.SendAsync(Request(HttpMethod.Delete, "/me/devices", ana, new { token = ions }));
        var unknown = await client.SendAsync(Request(HttpMethod.Delete, "/me/devices", ana, new { token = NewToken() }));

        Assert.Equal(HttpStatusCode.NoContent, own.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, others.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, unknown.StatusCode);
        Assert.Null(await Device(anas));
        Assert.NotNull(await Device(ions));
    }

    [PostgresFact]
    public async Task The_test_notification_goes_to_every_device_and_answers_how_many_were_accepted()
    {
        var expo = new StubPushHandler();
        using var app = factory.With(expo, new TestClock(Start));
        var client = app.CreateClient();
        var oid = NewOid();
        await SignIn(client, oid);
        var tokens = new[] { NewToken(), NewToken() };
        foreach (var token in tokens)
        {
            await Register(client, oid, token);
        }

        var response = await client.SendAsync(Request(HttpMethod.Post, "/me/notifications/test", oid));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sent").GetInt32());
        var send = Assert.Single(expo.Sends);
        Assert.Equal(tokens.Order(StringComparer.Ordinal), send.Body.EnumerateArray().Select(m => m.GetProperty("to").GetString()!).Order(StringComparer.Ordinal));
        Assert.All(send.Body.EnumerateArray(), m =>
        {
            Assert.Equal(PushEndpoints.TestTitle, m.GetProperty("title").GetString());
            Assert.Equal("CampusPulse test notification", m.GetProperty("body").GetString());
        });
        Assert.Null(send.Authorization);   // no Push:Expo:AccessToken configured
        await using var db = factory.CreateDbContext();
        var pending = await db.PushReceiptsPending.Select(r => r.TicketId).ToListAsync();
        Assert.Contains(expo.Tickets[tokens[0]], pending);
        Assert.Contains(expo.Tickets[tokens[1]], pending);
    }

    [PostgresFact]
    public async Task A_second_test_within_a_minute_is_429_and_after_it_works_again()
    {
        var clock = new TestClock(Start);
        using var app = factory.With(new StubPushHandler(), clock);
        var client = app.CreateClient();
        var oid = NewOid();
        await SignIn(client, oid);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request(HttpMethod.Post, "/me/notifications/test", oid))).StatusCode);

        clock.Advance(TimeSpan.FromSeconds(30));
        var tooSoon = await client.SendAsync(Request(HttpMethod.Post, "/me/notifications/test", oid));
        clock.Advance(TimeSpan.FromSeconds(31));
        var later = await client.SendAsync(Request(HttpMethod.Post, "/me/notifications/test", oid));

        Assert.Equal(HttpStatusCode.TooManyRequests, tooSoon.StatusCode);
        Assert.Equal("30", tooSoon.Headers.GetValues("Retry-After").Single());
        Assert.Equal("application/problem+json", tooSoon.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
    }

    [PostgresFact]
    public async Task No_devices_sends_nothing_and_answers_zero()
    {
        var expo = new StubPushHandler();
        using var app = factory.With(expo, new TestClock(Start));
        var client = app.CreateClient();
        var oid = NewOid();
        await SignIn(client, oid);

        var response = await client.SendAsync(Request(HttpMethod.Post, "/me/notifications/test", oid));

        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sent").GetInt32());
        Assert.Empty(expo.Calls);
    }

    [PostgresFact]
    public async Task Without_an_account_the_test_is_404()
    {
        using var app = factory.With(new StubPushHandler(), new TestClock(Start));

        var response = await app.CreateClient().SendAsync(Request(HttpMethod.Post, "/me/notifications/test", NewOid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [PostgresFact]
    public async Task A_device_not_registered_ticket_deletes_that_device_at_once()
    {
        var expo = new StubPushHandler();
        using var app = factory.With(expo, new TestClock(Start));
        var client = app.CreateClient();
        var oid = NewOid();
        await SignIn(client, oid);
        var (kept, gone) = (NewToken(), NewToken());
        await Register(client, oid, kept);
        await Register(client, oid, gone);
        expo.Respond = call => expo.AcceptAllBut(call, new HashSet<string> { gone });

        var response = await client.SendAsync(Request(HttpMethod.Post, "/me/notifications/test", oid));

        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sent").GetInt32());
        Assert.Null(await Device(gone));
        Assert.NotNull(await Device(kept));
    }

    [PostgresFact]
    public async Task When_expo_is_down_the_test_is_502_and_no_device_is_touched()
    {
        var expo = new StubPushHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) };
        using var app = factory.With(expo, new TestClock(Start));
        var client = app.CreateClient();
        var oid = NewOid();
        await SignIn(client, oid);
        var token = NewToken();
        await Register(client, oid, token);

        var response = await client.SendAsync(Request(HttpMethod.Post, "/me/notifications/test", oid));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.NotNull(await Device(token));
    }

    [PostgresFact]
    public async Task Deleting_the_account_deletes_its_devices_and_their_pending_receipts()
    {
        var expo = new StubPushHandler();
        using var app = factory.With(expo, new TestClock(Start));
        var client = app.CreateClient();
        var oid = NewOid();
        await SignIn(client, oid);
        var token = NewToken();
        await Register(client, oid, token);
        await client.SendAsync(Request(HttpMethod.Post, "/me/notifications/test", oid));
        var ticket = expo.Tickets[token];

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Request(HttpMethod.Delete, "/me", oid))).StatusCode);

        Assert.Null(await Device(token));
        await using var db = factory.CreateDbContext();
        Assert.False(await db.PushReceiptsPending.AnyAsync(r => r.TicketId == ticket));
    }
}
