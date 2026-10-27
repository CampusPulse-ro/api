using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CampusPulse.Api.Reminders;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CampusPulse.Api.Tests.PushTesting;

namespace CampusPulse.Api.Tests;

/// <summary>
/// The reminder sender, one tick at a time on a hand-moved clock, over the invented timetable
/// (<see cref="ImportedTimetable"/>). Group ALFA 1 X 1.1 has, every Monday, Geometria fractalilor 08:00-10:00 in
/// K210 (weeks 1-10) and Logica senzorilor virtuali 10:00 in AULA; Monday 2 Dec 2030 has no classes. Romania is UTC+3
/// until 27 Oct 2030, UTC+2 after. Each test starts by clearing every reminder setting: a tick serves every student
/// in the database, and these tests share one.
/// </summary>
public sealed class ClassReminderSenderTests(ImportedTimetable timetable) : IClassFixture<ImportedTimetable>
{
    /// <summary>Monday 7 Oct 2030 (week 2): Geometria starts at 08:00 in Bucharest, 05:00 UTC.</summary>
    private static readonly DateTimeOffset October7At0800 = new(2030, 10, 7, 5, 0, 0, TimeSpan.Zero);

    /// <summary>Monday 25 Nov 2030 (week 9, winter time): 08:00 in Bucharest is 06:00 UTC.</summary>
    private static readonly DateTimeOffset November25At0800 = new(2030, 11, 25, 6, 0, 0, TimeSpan.Zero);

    private PostgresAppFactory Factory => timetable.Factory;

    private async Task ClearReminders()
    {
        await using var db = Factory.CreateDbContext();
        await db.ReminderSettings.ExecuteDeleteAsync();
        await db.SentClassReminders.ExecuteDeleteAsync();
    }

    /// <summary>A started app: its own sender, with no tick behind it yet (as after a restart).</summary>
    private sealed class App(WebApplicationFactory<Program> app) : IDisposable
    {
        public HttpClient Client { get; } = app.CreateClient();

        public ClassReminderSender Sender => app.Services.GetRequiredService<ClassReminderSender>();

        public void Dispose() => app.Dispose();
    }

    private App Start(StubPushHandler expo, TestClock clock) => new(Factory.With(expo, clock));

    private static async Task<Guid> GroupId(HttpClient client, string oid, string label)
    {
        var programmes = await client.SendAsync(Request(HttpMethod.Get, "/timetable/programmes", oid));
        var alfa = (await programmes.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Single(p => p.GetProperty("code").GetString() == "ALFA");
        return alfa.GetProperty("years")[0].GetProperty("groups").EnumerateArray()
            .Single(g => g.GetProperty("series").GetString() == "X" && g.GetProperty("label").GetString() == label)
            .GetProperty("id").GetGuid();
    }

    /// <summary>A signed-in student of ALFA 1 X 1.1 with one device and, unless null, a reminder lead.</summary>
    private static async Task<(string Oid, Guid UserId, string Token)> Student(HttpClient client, int? minutesBefore, bool withDevice = true)
    {
        var oid = NewOid();
        var userId = await SignIn(client, oid);
        var group = await GroupId(client, oid, "1.1");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request(HttpMethod.Put, "/me/settings", oid, new { studyGroupId = group }))).StatusCode);
        var token = NewToken();
        if (withDevice)
        {
            Assert.Equal(HttpStatusCode.Created, (await Register(client, oid, token)).StatusCode);
        }

        if (minutesBefore is not null)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request(HttpMethod.Put, "/me/reminders", oid, new { minutesBefore }))).StatusCode);
        }

        return (oid, userId, token);
    }

    private static List<JsonElement> MessagesTo(StubPushHandler expo, string token) =>
        [.. expo.Sends.SelectMany(s => s.Body.EnumerateArray()).Where(m => m.GetProperty("to").GetString() == token)];

    private static string Title(JsonElement message) => message.GetProperty("title").GetString()!;

    private async Task<int> SentRows(Guid userId)
    {
        await using var db = Factory.CreateDbContext();
        return await db.SentClassReminders.CountAsync(r => r.UserId == userId);
    }

    [PostgresFact]
    public async Task A_reminder_goes_out_once_in_the_tick_that_crosses_start_minus_lead()
    {
        await ClearReminders();
        var expo = new StubPushHandler();
        var clock = new TestClock(October7At0800 - TimeSpan.FromMinutes(20) + TimeSpan.FromSeconds(30));
        using var app = Start(expo, clock);
        var (oid, userId, token) = await Student(app.Client, minutesBefore: 15);

        // 07:40:30 and 07:44:30 in Bucharest: not yet due.
        Assert.Equal(ClassReminderTick.Nothing, await app.Sender.TickAsync(default));
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(ClassReminderTick.Nothing, await app.Sender.TickAsync(default));
        Assert.Empty(expo.Sends);

        // 07:45:30: the tick from 07:44:30 crosses 07:45, Geometria's 08:00 minus 15 minutes.
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(new ClassReminderTick(Due: 1, Sent: 1, AlreadySent: 0, Retrying: 0), await app.Sender.TickAsync(default));

        var message = Assert.Single(MessagesTo(expo, token));
        var timetable = await app.Client.SendAsync(Request(HttpMethod.Get, "/me/timetable?from=2030-10-07", oid));
        var geometry = (await timetable.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("occurrences").EnumerateArray()
            .First(o => o.GetProperty("subject").GetString() == "Geometria fractalilor");
        var name = geometry.GetProperty("shortName").GetString() ?? "Geometria fractalilor";
        Assert.Equal($"{name} in 15 min", Title(message));
        Assert.Equal("Course · K210", message.GetProperty("body").GetString());
        Assert.Equal("high", message.GetProperty("priority").GetString());
        Assert.Equal(870, message.GetProperty("ttl").GetInt32()); // until the class starts, 14.5 minutes
        var data = message.GetProperty("data");
        Assert.Equal("classReminder", data.GetProperty("type").GetString());
        // As GET /me/timetable writes them, so the app can find the class.
        Assert.Equal(geometry.GetProperty("date").GetString(), data.GetProperty("date").GetString());
        Assert.Equal(geometry.GetProperty("startsAt").GetString(), data.GetProperty("startsAt").GetString());
        Assert.Equal(geometry.GetProperty("subjectId").GetString(), data.GetProperty("subjectId").GetString());
        Assert.Equal(1, await SentRows(userId));

        // Nothing new until Logica (10:00) is due at 09:45: not at 07:46:30, nor at 09:44:30 after a long gap.
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(ClassReminderTick.Nothing, await app.Sender.TickAsync(default));
        clock.Advance(TimeSpan.FromMinutes(118));
        Assert.Equal(ClassReminderTick.Nothing, await app.Sender.TickAsync(default));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(new ClassReminderTick(1, 1, 0, 0), await app.Sender.TickAsync(default));

        var messages = MessagesTo(expo, token);
        Assert.Equal(2, messages.Count);
        Assert.Equal("Course · AULA", messages[1].GetProperty("body").GetString());
    }

    [PostgresFact]
    public async Task Skipped_ticks_send_once_with_the_minutes_actually_left()
    {
        await ClearReminders();
        var expo = new StubPushHandler();
        var clock = new TestClock(November25At0800 - TimeSpan.FromMinutes(40));
        using var app = Start(expo, clock);
        var (_, _, token) = await Student(app.Client, minutesBefore: 30);
        Assert.Equal(ClassReminderTick.Nothing, await app.Sender.TickAsync(default));

        // Due at 07:30; the next tick only comes at 07:36:20.
        clock.Advance(TimeSpan.FromMinutes(16) + TimeSpan.FromSeconds(20));
        Assert.Equal(new ClassReminderTick(1, 1, 0, 0), await app.Sender.TickAsync(default));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(ClassReminderTick.Nothing, await app.Sender.TickAsync(default));

        Assert.EndsWith(" in 24 min", Title(Assert.Single(MessagesTo(expo, token))));
    }

    [PostgresFact]
    public async Task No_reminder_without_a_setting_or_without_a_device()
    {
        await ClearReminders();
        var expo = new StubPushHandler();
        var clock = new TestClock(October7At0800 - TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(30));
        using var app = Start(expo, clock);
        var off = await Student(app.Client, minutesBefore: null);
        var turnedOff = await Student(app.Client, minutesBefore: 15);
        Assert.Equal(HttpStatusCode.OK,
            (await app.Client.SendAsync(Request(HttpMethod.Put, "/me/reminders", turnedOff.Oid, new { minutesBefore = (int?)null }))).StatusCode);
        var noDevice = await Student(app.Client, minutesBefore: 15, withDevice: false);

        Assert.Equal(ClassReminderTick.Nothing, await app.Sender.TickAsync(default));

        Assert.Empty(expo.Sends);
        Assert.Equal(0, await SentRows(off.UserId) + await SentRows(turnedOff.UserId) + await SentRows(noDevice.UserId));
    }

    [PostgresFact]
    public async Task After_a_restart_a_missed_reminder_goes_out_late_once_but_never_once_the_class_started()
    {
        await ClearReminders();
        var expo = new StubPushHandler();
        // Due at 07:45; the API was down then and its first tick after the restart is at 07:50:30.
        var clock = new TestClock(November25At0800 - TimeSpan.FromMinutes(9) - TimeSpan.FromSeconds(30));
        (string Oid, Guid UserId, string Token) student;
        using (var first = Start(expo, clock))
        {
            student = await Student(first.Client, minutesBefore: 15);
            Assert.Equal(new ClassReminderTick(1, 1, 0, 0), await first.Sender.TickAsync(default));
        }

        Assert.EndsWith(" in 10 min", Title(Assert.Single(MessagesTo(expo, student.Token))));

        // Restarted again two minutes later: the class is still in its reminder window, and the record of the
        // send keeps it from going out twice.
        clock.Advance(TimeSpan.FromMinutes(2));
        using (var second = Start(expo, clock))
        {
            Assert.Equal(ClassReminderTick.Nothing, await second.Sender.TickAsync(default));
        }

        Assert.Single(MessagesTo(expo, student.Token));

        // Another student, whose record never existed: after 08:00 the class has started, so nothing goes out.
        await ClearReminders();
        clock.Advance(TimeSpan.FromMinutes(8));
        using var third = Start(expo, clock);
        var late = await Student(third.Client, minutesBefore: 15);
        Assert.Equal(ClassReminderTick.Nothing, await third.Sender.TickAsync(default));
        Assert.Empty(MessagesTo(expo, late.Token));
    }

    [PostgresFact]
    public async Task A_non_teaching_day_has_no_reminders_and_the_next_week_does()
    {
        await ClearReminders();
        var expo = new StubPushHandler();
        // Monday 2 Dec 2030, 07:45:30 and 09:45:30 in Bucharest: a day off.
        var clock = new TestClock(new DateTimeOffset(2030, 12, 2, 5, 45, 30, TimeSpan.Zero));
        using var app = Start(expo, clock);
        var (_, _, token) = await Student(app.Client, minutesBefore: 15);

        Assert.Equal(ClassReminderTick.Nothing, await app.Sender.TickAsync(default));
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(ClassReminderTick.Nothing, await app.Sender.TickAsync(default));

        // Monday 9 Dec (week 11): Geometria (weeks 1-10) is over, Logica at 10:00 goes on.
        clock.Advance(TimeSpan.FromDays(7));
        Assert.Equal(new ClassReminderTick(1, 1, 0, 0), await app.Sender.TickAsync(default));
        Assert.EndsWith(" in 15 min", Title(Assert.Single(MessagesTo(expo, token))));
    }

    [PostgresFact]
    public async Task A_send_the_push_service_cannot_take_is_retried_on_the_next_tick()
    {
        await ClearReminders();
        var expo = new StubPushHandler();
        var clock = new TestClock(November25At0800 - TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(30));
        using var app = Start(expo, clock);
        var (_, userId, token) = await Student(app.Client, minutesBefore: 15);
        expo.Respond = call => call.Path == "/--/api/v2/push/send" ? new HttpResponseMessage(HttpStatusCode.BadGateway) : expo.Default(call);

        Assert.Equal(new ClassReminderTick(Due: 1, Sent: 0, AlreadySent: 0, Retrying: 1), await app.Sender.TickAsync(default));
        Assert.Equal(0, await SentRows(userId));

        expo.Respond = expo.Default;
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(new ClassReminderTick(1, 1, 0, 0), await app.Sender.TickAsync(default));
        Assert.Equal(2, MessagesTo(expo, token).Count);
        Assert.Equal(1, await SentRows(userId));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(ClassReminderTick.Nothing, await app.Sender.TickAsync(default));
    }

    [PostgresFact]
    public async Task Each_student_gets_their_own_lead_on_every_device_and_an_uninstalled_one_is_dropped()
    {
        await ClearReminders();
        var expo = new StubPushHandler();
        var clock = new TestClock(November25At0800 - TimeSpan.FromMinutes(60));
        using var app = Start(expo, clock);
        var early = await Student(app.Client, minutesBefore: 60);
        var secondPhone = NewToken();
        Assert.Equal(HttpStatusCode.Created, (await Register(app.Client, early.Oid, secondPhone)).StatusCode);
        var gone = NewToken();
        Assert.Equal(HttpStatusCode.Created, (await Register(app.Client, early.Oid, gone)).StatusCode);
        var late = await Student(app.Client, minutesBefore: 5);
        expo.Respond = call => call.Path == "/--/api/v2/push/send" ? expo.AcceptAllBut(call, new HashSet<string> { gone }) : expo.Default(call);
        // (A device registered at the very instant of a send isn't deleted for it: it may be a reinstall.)
        clock.Advance(TimeSpan.FromSeconds(30));

        // 07:00:30: the 60-minute reminder, on both remaining phones.
        Assert.Equal(new ClassReminderTick(1, 1, 0, 0), await app.Sender.TickAsync(default));
        Assert.Single(MessagesTo(expo, early.Token));
        Assert.Single(MessagesTo(expo, secondPhone));
        Assert.Empty(MessagesTo(expo, late.Token));
        await using (var db = Factory.CreateDbContext())
        {
            Assert.False(await db.PushDevices.AnyAsync(d => d.Token == gone));
        }

        // 07:55:30: the 5-minute one, ticking every minute in between.
        for (var i = 0; i < 54; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(ClassReminderTick.Nothing, await app.Sender.TickAsync(default));
        }

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(new ClassReminderTick(1, 1, 0, 0), await app.Sender.TickAsync(default));
        Assert.EndsWith(" in 5 min", Title(Assert.Single(MessagesTo(expo, late.Token))));
        Assert.Single(MessagesTo(expo, early.Token));
    }

    /// <summary>Turning reminders on (or a longer lead, or a first device) after a class's due time still reminds
    /// before it starts, on a running instance as after a restart.</summary>
    [PostgresFact]
    public async Task Turning_reminders_on_late_still_reminds_before_the_class()
    {
        await ClearReminders();
        var expo = new StubPushHandler();
        var clock = new TestClock(November25At0800 - TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(30));
        using var app = Start(expo, clock);
        Assert.Equal(ClassReminderTick.Nothing, await app.Sender.TickAsync(default));
        var (_, _, token) = await Student(app.Client, minutesBefore: 30);

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(new ClassReminderTick(1, 1, 0, 0), await app.Sender.TickAsync(default));

        Assert.EndsWith(" in 10 min", Title(Assert.Single(MessagesTo(expo, token))));
    }

    /// <summary>An answer that can't be read (or a timeout) may hide an accepted message: the reminder stays recorded
    /// rather than risk a second notification.</summary>
    [PostgresFact]
    public async Task An_unreadable_answer_is_not_sent_again()
    {
        await ClearReminders();
        var expo = new StubPushHandler();
        var clock = new TestClock(November25At0800 - TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(30));
        using var app = Start(expo, clock);
        var (_, userId, token) = await Student(app.Client, minutesBefore: 15);
        expo.Respond = call => call.Path == "/--/api/v2/push/send"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>busy</html>") }
            : expo.Default(call);

        Assert.Equal(new ClassReminderTick(Due: 1, Sent: 0, AlreadySent: 0, Retrying: 0), await app.Sender.TickAsync(default));
        expo.Respond = expo.Default;
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(ClassReminderTick.Nothing, await app.Sender.TickAsync(default));

        Assert.Single(MessagesTo(expo, token));
        Assert.Equal(1, await SentRows(userId));
    }

    [PostgresFact]
    public async Task Records_of_sent_reminders_are_deleted_after_two_days_and_with_the_account()
    {
        await ClearReminders();
        var expo = new StubPushHandler();
        var clock = new TestClock(November25At0800 - TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(30));
        using var app = Start(expo, clock);
        var kept = await Student(app.Client, minutesBefore: 15);
        var deleted = await Student(app.Client, minutesBefore: 15);
        Assert.Equal(new ClassReminderTick(2, 2, 0, 0), await app.Sender.TickAsync(default));
        Assert.Equal((1, 1), (await SentRows(kept.UserId), await SentRows(deleted.UserId)));

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.SendAsync(Request(HttpMethod.Delete, "/me", deleted.Oid))).StatusCode);
        Assert.Equal(0, await SentRows(deleted.UserId));

        clock.Advance(ClassReminderSender.KeepSent - TimeSpan.FromMinutes(1));
        await app.Sender.TickAsync(default);
        Assert.Equal(1, await SentRows(kept.UserId));
        clock.Advance(TimeSpan.FromMinutes(2));
        await app.Sender.TickAsync(default);
        Assert.Equal(0, await SentRows(kept.UserId));
    }
}
