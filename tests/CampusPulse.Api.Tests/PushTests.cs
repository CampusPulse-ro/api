using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CampusPulse.Api.Push;
using CampusPulse.Domain.Push;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

    private static Task<HttpResponseMessage> SendTest(HttpClient client, string oid) =>
        client.SendAsync(Request(HttpMethod.Post, "/me/notifications/test", oid));

    private static HttpRequestMessage RawRegister(string oid, string json)
    {
        var request = Request(HttpMethod.Post, "/me/devices", oid);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.True(problem.TryGetProperty("requestId", out _));
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
    public async Task Two_students_registering_one_token_at_once_never_fail_and_leave_one_device()
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

    /// <summary>Just before this registration's insert, another student's row with the same token
    /// appears (their registration won the race): the insert hits the unique index.</summary>
    public sealed class RivalInsertsFirst(PostgresAppFactory factory, string token, Guid rivalId) : SaveChangesInterceptor
    {
        public int Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Fired == 0 && eventData.Context!.ChangeTracker.Entries<PushDevice>()
                    .Any(e => e.State == EntityState.Added && e.Entity.Token == token))
            {
                Fired++;
                await using var other = factory.CreateDbContext();
                other.PushDevices.Add(new PushDevice(rivalId, token, PushPlatform.Android, DateTimeOffset.UtcNow));
                await other.SaveChangesAsync(cancellationToken);
            }

            return result;
        }
    }

    [PostgresFact]
    public async Task Losing_the_race_for_a_token_retries_once_and_the_later_student_owns_it()
    {
        var (ana, ion) = (NewOid(), NewOid());
        var token = NewToken();
        Guid anaId, ionId;
        using (var setup = factory.With(new StubPushHandler(), new TestClock(Start)))
        {
            anaId = await SignIn(setup.CreateClient(), ana);
            ionId = await SignIn(setup.CreateClient(), ion);
        }

        var rival = new RivalInsertsFirst(factory, token, anaId);
        using var app = factory.With(new StubPushHandler(), new TestClock(Start), null, rival);

        var response = await Register(app.CreateClient(), ion, token);

        // The first try would have answered 201; the retry found Ana's row and moved it.
        Assert.Equal(1, rival.Fired);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ionId, (await Device(token))!.UserId);
        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.PushDevices.CountAsync(d => d.Token == token));
    }

    /// <summary>Deletes the token's row just before this registration updates it (the device was
    /// forgotten in between): the update matches no row.</summary>
    private sealed class DeleteBeforeUpdate(PostgresAppFactory factory, string token) : SaveChangesInterceptor
    {
        public int Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Fired == 0 && eventData.Context!.ChangeTracker.Entries<PushDevice>()
                    .Any(e => e.State == EntityState.Modified && e.Entity.Token == token))
            {
                Fired++;
                await using var other = factory.CreateDbContext();
                await other.PushDevices.Where(d => d.Token == token).ExecuteDeleteAsync(cancellationToken);
            }

            return result;
        }
    }

    [PostgresFact]
    public async Task A_token_deleted_between_the_read_and_the_update_is_registered_anew()
    {
        var clock = new TestClock(Start);
        var oid = NewOid();
        var token = NewToken();
        using (var setup = factory.With(new StubPushHandler(), clock))
        {
            await SignIn(setup.CreateClient(), oid);
            Assert.Equal(HttpStatusCode.Created, (await Register(setup.CreateClient(), oid, token)).StatusCode);
        }

        var interceptor = new DeleteBeforeUpdate(factory, token);
        using var app = factory.With(new StubPushHandler(), clock, null, interceptor);

        var response = await Register(app.CreateClient(), oid, token);

        Assert.Equal(1, interceptor.Fired);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(await Device(token));
    }

    /// <summary>Deletes the student's account just before the registration takes its lock: a
    /// DELETE /me that landed first.</summary>
    private sealed class DeleteAccountBeforeLock(PostgresAppFactory factory, string oid) : DbCommandInterceptor
    {
        public int Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Fired == 0 && command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
            {
                Fired++;
                await using var other = factory.CreateDbContext();
                await other.Users.Where(u => u.EntraObjectId == oid).ExecuteDeleteAsync(cancellationToken);
            }

            return result;
        }
    }

    [PostgresFact]
    public async Task A_registration_overlapping_the_account_deletion_is_404_and_stores_nothing()
    {
        var oid = NewOid();
        using (var setup = factory.With(new StubPushHandler(), new TestClock(Start)))
        {
            await SignIn(setup.CreateClient(), oid);
        }

        var interceptor = new DeleteAccountBeforeLock(factory, oid);
        using var app = factory.With(new StubPushHandler(), new TestClock(Start), null, interceptor);
        var token = NewToken();

        var response = await Register(app.CreateClient(), oid, token);

        Assert.Equal(1, interceptor.Fired);
        await AssertProblem(response, HttpStatusCode.NotFound);
        Assert.Null(await Device(token));
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

    /// <summary>Bodies outside the contract: a platform that isn't one of its names (a number, an
    /// unknown name, none at all), no token, no JSON.</summary>
    public static TheoryData<string, string> BadBodies => new()
    {
        { "an integer platform", """{"token":"TOKEN","platform":7}""" },
        { "an unknown platform", """{"token":"TOKEN","platform":"windows"}""" },
        { "no platform", """{"token":"TOKEN"}""" },
        { "no token", """{"platform":"android"}""" },
        { "not JSON", "token=TOKEN" },
    };

    [PostgresTheory]
    [MemberData(nameof(BadBodies))]
    public async Task A_body_outside_the_contract_is_400_problem_details_and_stores_nothing(string why, string json)
    {
        using var app = factory.With(new StubPushHandler(), new TestClock(Start));
        var client = app.CreateClient();
        var oid = NewOid();
        await SignIn(client, oid);
        var token = NewToken();

        var response = await client.SendAsync(RawRegister(oid, json.Replace("TOKEN", token, StringComparison.Ordinal)));

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{why}: {(int)response.StatusCode}");
        await AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.DoesNotContain(token, await response.Content.ReadAsStringAsync());
        Assert.Null(await Device(token));
    }

    [PostgresFact]
    public async Task Without_an_account_registering_is_404()
    {
        using var app = factory.With(new StubPushHandler(), new TestClock(Start));
        var token = NewToken();

        var response = await Register(app.CreateClient(), NewOid(), token);

        await AssertProblem(response, HttpStatusCode.NotFound);
        Assert.Null(await Device(token));
    }

    [PostgresFact]
    public async Task Deleting_removes_only_your_own_device_and_is_204_for_any_token()
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
        var malformedToken = await client.SendAsync(Request(HttpMethod.Delete, "/me/devices", ana, new { token = "not a token" }));

        Assert.Equal(HttpStatusCode.NoContent, own.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, others.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, malformedToken.StatusCode);
        Assert.Null(await Device(anas));
        Assert.NotNull(await Device(ions));
    }

    [PostgresFact]
    public async Task Deleting_without_a_token_in_the_body_is_400()
    {
        using var app = factory.With(new StubPushHandler(), new TestClock(Start));
        var client = app.CreateClient();
        var oid = NewOid();
        await SignIn(client, oid);

        var noBody = await client.SendAsync(Request(HttpMethod.Delete, "/me/devices", oid));
        var emptyObject = await client.SendAsync(Request(HttpMethod.Delete, "/me/devices", oid, new { }));

        await AssertProblem(noBody, HttpStatusCode.BadRequest);
        await AssertProblem(emptyObject, HttpStatusCode.BadRequest);
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

        var response = await SendTest(client, oid);

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
        await Register(client, oid, NewToken());
        Assert.Equal(HttpStatusCode.OK, (await SendTest(client, oid)).StatusCode);

        clock.Advance(TimeSpan.FromSeconds(30));
        var tooSoon = await SendTest(client, oid);
        clock.Advance(TimeSpan.FromSeconds(31));
        var later = await SendTest(client, oid);

        await AssertProblem(tooSoon, HttpStatusCode.TooManyRequests);
        Assert.Equal("30", tooSoon.Headers.GetValues("Retry-After").Single());
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
    }

    [PostgresFact]
    public async Task No_devices_sends_nothing_answers_zero_and_does_not_use_up_the_minute()
    {
        var expo = new StubPushHandler();
        using var app = factory.With(expo, new TestClock(Start));
        var client = app.CreateClient();
        var oid = NewOid();
        await SignIn(client, oid);

        var response = await SendTest(client, oid);
        var again = await SendTest(client, oid);

        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sent").GetInt32());
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Empty(expo.Calls);
    }

    [PostgresFact]
    public async Task Without_an_account_the_test_is_404()
    {
        using var app = factory.With(new StubPushHandler(), new TestClock(Start));

        var response = await SendTest(app.CreateClient(), NewOid());

        await AssertProblem(response, HttpStatusCode.NotFound);
    }

    [PostgresFact]
    public async Task A_device_not_registered_ticket_deletes_that_device_at_once()
    {
        var expo = new StubPushHandler();
        var clock = new TestClock(Start);
        using var app = factory.With(expo, clock);
        var client = app.CreateClient();
        var oid = NewOid();
        await SignIn(client, oid);
        var (kept, gone) = (NewToken(), NewToken());
        await Register(client, oid, kept);
        await Register(client, oid, gone);
        clock.Advance(TimeSpan.FromMinutes(1));
        expo.Respond = call => expo.AcceptAllBut(call, new HashSet<string> { gone });

        var response = await SendTest(client, oid);

        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sent").GetInt32());
        Assert.Null(await Device(gone));
        Assert.NotNull(await Device(kept));
    }

    /// <summary>The app was reinstalled and registered the same token again while the message was on
    /// its way: Expo's DeviceNotRegistered is about the old install.</summary>
    [PostgresFact]
    public async Task A_device_registered_again_during_the_send_survives_a_device_not_registered_ticket()
    {
        var expo = new StubPushHandler();
        var clock = new TestClock(Start);
        using var app = factory.With(expo, clock);
        var client = app.CreateClient();
        var oid = NewOid();
        await SignIn(client, oid);
        var token = NewToken();
        await Register(client, oid, token);
        clock.Advance(TimeSpan.FromMinutes(1));
        expo.RespondAsync = async call =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(HttpStatusCode.OK, (await Register(client, oid, token)).StatusCode);
            return expo.AcceptAllBut(call, new HashSet<string> { token });
        };

        var response = await SendTest(client, oid);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Start.AddMinutes(1).AddSeconds(1), (await Device(token))!.LastRegisteredAt);
    }

    /// <summary>One of two devices is forgotten (sign-out) while the message is on its way: its
    /// receipt has nothing to belong to, the other one's is still kept.</summary>
    [PostgresFact]
    public async Task A_device_deleted_during_the_send_loses_only_its_own_receipt()
    {
        var expo = new StubPushHandler();
        using var app = factory.With(expo, new TestClock(Start));
        var client = app.CreateClient();
        var oid = NewOid();
        await SignIn(client, oid);
        var (kept, gone) = (NewToken(), NewToken());
        await Register(client, oid, kept);
        await Register(client, oid, gone);
        expo.RespondAsync = async call =>
        {
            await client.SendAsync(Request(HttpMethod.Delete, "/me/devices", oid, new { token = gone }));
            return expo.AcceptAll(call);
        };

        var response = await SendTest(client, oid);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sent").GetInt32());
        await using var db = factory.CreateDbContext();
        var pending = await db.PushReceiptsPending.Select(r => r.TicketId).ToListAsync();
        Assert.Contains(expo.Tickets[kept], pending);
        Assert.DoesNotContain(expo.Tickets[gone], pending);
    }

    public static TheoryData<string> ServiceFailures => new() { "503", "timeout", "unreachable", "not JSON" };

    /// <summary>Expo can't be used: 502, nothing is deleted or stored, and the next tap may send
    /// again right away (the minute wasn't used up).</summary>
    [PostgresTheory]
    [MemberData(nameof(ServiceFailures))]
    public async Task When_expo_fails_the_test_is_502_nothing_changes_and_the_next_tap_works(string failure)
    {
        var expo = new StubPushHandler
        {
            Respond = failure switch
            {
                "503" => _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                "timeout" => _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()),
                "unreachable" => _ => throw new HttpRequestException("Connection refused"),
                _ => _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>busy</html>", Encoding.UTF8, "text/html") },
            },
        };
        using var app = factory.With(expo, new TestClock(Start));
        var client = app.CreateClient();
        var oid = NewOid();
        await SignIn(client, oid);
        var token = NewToken();
        await Register(client, oid, token);

        var response = await SendTest(client, oid);

        await AssertProblem(response, HttpStatusCode.BadGateway);
        var device = await Device(token);
        Assert.NotNull(device);
        await using (var db = factory.CreateDbContext())
        {
            Assert.False(await db.PushReceiptsPending.AnyAsync(r => r.DeviceId == device.Id));
        }

        expo.Respond = expo.Default;
        Assert.Equal(HttpStatusCode.OK, (await SendTest(client, oid)).StatusCode);
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
        await SendTest(client, oid);
        var ticket = expo.Tickets[token];

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Request(HttpMethod.Delete, "/me", oid))).StatusCode);

        Assert.Null(await Device(token));
        await using var db = factory.CreateDbContext();
        Assert.False(await db.PushReceiptsPending.AnyAsync(r => r.TicketId == ticket));
    }
}
