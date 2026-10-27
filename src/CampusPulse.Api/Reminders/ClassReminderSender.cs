using System.Globalization;
using CampusPulse.Api.Push;
using CampusPulse.Api.Timetable;
using CampusPulse.Domain.Push;
using CampusPulse.Domain.Reminders;
using CampusPulse.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CampusPulse.Api.Reminders;

/// <summary>The <c>Reminders</c> configuration section.</summary>
public sealed class ReminderOptions
{
    public const string Section = "Reminders";

    /// <summary>False: no reminders are sent (tests, local development). The setting endpoints work either way.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>What one tick of <see cref="ClassReminderSender"/> did, in reminders (one per class, whatever the
/// student's number of devices): <see cref="Due"/> found due and not yet sent, of which <see cref="Sent"/> reached
/// the push service for at least one device, <see cref="AlreadySent"/> turned out to be recorded meanwhile (or the
/// student's account was deleted), and <see cref="Retrying"/> certainly didn't reach it (it was unreachable or
/// refused the request) and are left for the next tick. The rest were refused for every device (an uninstalled app).</summary>
public sealed record ClassReminderTick(int Due, int Sent, int AlreadySent, int Retrying)
{
    public static readonly ClassReminderTick Nothing = new(0, 0, 0, 0);
}

/// <summary>
/// Sends class reminders (CampusPulse-ro/api#53). Once a minute, for every student with a reminder setting and at
/// least one device, it takes their classes, computed exactly as <c>GET /me/timetable</c> computes them
/// (<see cref="ClassSchedule"/>). A class's reminder is due from its start (Romanian time) minus the student's lead
/// until it starts, and goes out once: one push, on every device. Whether it went out is recorded in
/// <c>class_reminders_sent</c>, not in this process, so a skipped tick, a restart or a deploy neither repeats nor
/// misses one, and a class that has started gets none. Records older than <see cref="KeepSent"/> are deleted here.
/// Logs counts only.
/// </summary>
public sealed partial class ClassReminderSender(IServiceScopeFactory scopes, TimeProvider time, IOptions<ReminderOptions> options,
    ILogger<ClassReminderSender> logger) : BackgroundService
{
    public const string ReminderType = "classReminder";

    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>The longest lead: how far ahead of now a tick looks for classes.</summary>
    public static readonly TimeSpan MaxLead = TimeSpan.FromMinutes(ReminderSettings.MaxMinutesBefore);

    public static readonly TimeSpan KeepSent = TimeSpan.FromDays(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            LogOff(logger);
            return;
        }

        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                // Whatever wasn't sent is still due on the next tick. Never stop the host over it.
                LogTickFailed(logger, e.GetType().Name, e);
            }
        }
    }

    /// <summary>One tick: sends the reminders that are due now and not yet sent.</summary>
    public async Task<ClassReminderTick> TickAsync(CancellationToken cancellationToken)
    {
        var tick = await SendDueAsync(time.GetUtcNow(), cancellationToken);
        if (tick.Due > 0)
        {
            LogTick(logger, tick.Due, tick.Sent, tick.AlreadySent, tick.Retrying);
        }

        return tick;
    }

    /// <summary>A reminder <paramref name="lead"/> ahead of a class starting at <paramref name="startsAt"/> is due from
    /// <c>startsAt - lead</c> until the class starts.</summary>
    public static bool IsDue(DateTimeOffset startsAt, TimeSpan lead, DateTimeOffset now) => startsAt - lead <= now && now < startsAt;

    /// <summary>"MSA in 15 min" (the short name, else the name; the minutes left, which is the lead unless the
    /// reminder is late) and "Lab · 303CP" (the kinds and the room, whichever there are).</summary>
    public static (string Title, string Body) Text(OccurrenceResponse occurrence, int minutesLeft)
    {
        var kinds = string.Join(" + ", occurrence.Kinds);
        var body = string.Join(" · ", new[] { kinds, occurrence.Room }.Where(part => !string.IsNullOrEmpty(part)));
        return ($"{occurrence.ShortName ?? occurrence.Subject} in {minutesLeft} min", body.Length > 0 ? body : occurrence.Subject);
    }

    /// <summary>What the app gets with the notification, to open that class: its date and start as
    /// <c>GET /me/timetable</c> writes them (<c>2030-11-25</c>, <c>08:00:00</c>) and the subject.</summary>
    public static IReadOnlyDictionary<string, string> Data(OccurrenceResponse occurrence) => new Dictionary<string, string>
    {
        ["type"] = ReminderType,
        ["date"] = occurrence.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["startsAt"] = occurrence.StartsAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        ["subjectId"] = occurrence.SubjectId.ToString(),
    };

    private async Task<ClassReminderTick> SendDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CampusPulseDbContext>();

        var keepFrom = now - KeepSent;
        await db.SentClassReminders.Where(r => r.SentAt < keepFrom).ExecuteDeleteAsync(cancellationToken);

        var leads = await db.ReminderSettings
            .Where(r => db.PushDevices.Any(d => d.UserId == r.UserId))
            .ToDictionaryAsync(r => r.UserId, r => TimeSpan.FromMinutes(r.MinutesBefore), cancellationToken);
        if (leads.Count == 0)
        {
            return ClassReminderTick.Nothing;
        }

        // A due class starts within the hour: today or, late in the evening, tomorrow (Romanian dates).
        var (firstDay, lastDay) = (RomanianTime.DateOf(now), RomanianTime.DateOf(now + MaxLead));
        var students = await StudentTimetableChoices.LoadAsync(db, [.. leads.Keys], cancellationToken);
        var schedule = await ClassSchedule.ForStudentsAsync(db, students, firstDay, lastDay, cancellationToken);
        var inWindow = new List<Due>();
        foreach (var student in students)
        {
            // One reminder per subject and start: a grid class and a picked offer of the same subject at the same
            // time are one class to the student.
            foreach (var occurrence in schedule.ForStudent(student, firstDay, lastDay).DistinctBy(o => (o.Date, o.StartsAt, o.SubjectId)))
            {
                var startsAt = RomanianTime.Instant(occurrence.Date, occurrence.StartsAt);
                if (IsDue(startsAt, leads[student.UserId], now))
                {
                    inWindow.Add(new Due(student.UserId, occurrence, startsAt));
                }
            }
        }

        if (inWindow.Count == 0)
        {
            return ClassReminderTick.Nothing;
        }

        var users = inWindow.Select(d => d.UserId).Distinct().ToList();
        var sentBefore = (await db.SentClassReminders
                .Where(r => users.Contains(r.UserId) && r.Date >= firstDay && r.Date <= lastDay)
                .Select(r => new Key(r.UserId, r.Date, r.StartsAt, r.SubjectId))
                .ToListAsync(cancellationToken))
            .ToHashSet();
        var devices = (await db.PushDevices.Where(d => users.Contains(d.UserId)).ToListAsync(cancellationToken)).ToLookup(d => d.UserId);
        var due = inWindow.Where(d => !sentBefore.Contains(d.Key) && devices[d.UserId].Any()).ToList();
        if (due.Count == 0)
        {
            return ClassReminderTick.Nothing;
        }

        // From the claim to the send and the release, the tick runs to its end even when the host is stopping (a
        // deploy): cancelled in between, it would leave records of reminders that never went out. Each request to
        // the push service has its own timeout.
        var claimed = await ClaimAsync(db, due, now, CancellationToken.None);
        var toSend = due.Where(d => claimed.Contains(d.Key)).ToList();
        var messages = new List<(PushDevice Device, PushMessage Message)>();
        var owners = new List<int>();
        for (var i = 0; i < toSend.Count; i++)
        {
            var reminder = toSend[i];
            var left = reminder.StartsAt - now;
            var (title, body) = Text(reminder.Occurrence, Math.Max(1, (int)Math.Ceiling(left.TotalMinutes)));
            var data = Data(reminder.Occurrence);
            foreach (var device in devices[reminder.UserId])
            {
                // High priority: it's due in minutes, and Android may hold a normal one back. Worth delivering only
                // until the class starts.
                messages.Add((device, new PushMessage(device.Token, title, body) { Data = data, HighPriority = true, TimeToLive = left }));
                owners.Add(i);
            }
        }

        var tickets = await scope.ServiceProvider.GetRequiredService<PushDelivery>().SendEachAsync(messages, CancellationToken.None);
        var ticketsOf = owners.Zip(tickets).ToLookup(x => x.First, x => x.Second);
        var sent = Enumerable.Range(0, toSend.Count).Count(i => ticketsOf[i].Any(t => t.Ok));
        // Not taken by the push service, for certain: give them back, so the next tick sends them while their class
        // hasn't started. A timeout or an unreadable answer may hide an accepted message, so those stay recorded
        // rather than risk a second notification; so do refusals (an uninstalled app), where sending again changes
        // nothing.
        var retry = Enumerable.Range(0, toSend.Count)
            .Where(i => ticketsOf[i].All(t => PushDelivery.CertainlyNotSent(t.Error)))
            .Select(i => toSend[i])
            .ToList();
        await ReleaseAsync(db, retry, CancellationToken.None);
        return new ClassReminderTick(due.Count, sent, due.Count - toSend.Count, retry.Count);
    }

    /// <summary>Records the reminders as sent, in one statement, and returns those this tick recorded: not the ones
    /// recorded meanwhile, nor those of a deleted account. Before the send, so a crash in between loses a reminder
    /// rather than sending it twice.</summary>
    private static async Task<HashSet<Key>> ClaimAsync(CampusPulseDbContext db, IReadOnlyList<Due> reminders, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var claimed = new HashSet<Key>();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = KeysCommand(db, """
                INSERT INTO class_reminders_sent (user_id, date, starts_at, subject_id, sent_at)
                SELECT k.user_id, k.date, k.starts_at, k.subject_id, @now
                FROM unnest(@users, @dates, @starts, @subjects) AS k (user_id, date, starts_at, subject_id)
                WHERE EXISTS (SELECT 1 FROM users u WHERE u.id = k.user_id)
                ON CONFLICT DO NOTHING
                RETURNING user_id, date, starts_at, subject_id
                """, reminders.Select(r => r.Key).ToList());
            command.Parameters.AddWithValue("now", now);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                claimed.Add(new Key(reader.GetGuid(0), reader.GetFieldValue<DateOnly>(1), reader.GetFieldValue<TimeOnly>(2), reader.GetGuid(3)));
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        return claimed;
    }

    /// <summary>Deletes the records of reminders that certainly didn't go out, in one statement.</summary>
    private static async Task ReleaseAsync(CampusPulseDbContext db, IReadOnlyList<Due> reminders, CancellationToken cancellationToken)
    {
        if (reminders.Count == 0)
        {
            return;
        }

        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = KeysCommand(db, """
                DELETE FROM class_reminders_sent s
                USING unnest(@users, @dates, @starts, @subjects) AS k (user_id, date, starts_at, subject_id)
                WHERE s.user_id = k.user_id AND s.date = k.date AND s.starts_at = k.starts_at AND s.subject_id = k.subject_id
                """, reminders.Select(r => r.Key).ToList());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>A command on the context's (open) connection with the keys as four parallel arrays, for
    /// <c>unnest(@users, @dates, @starts, @subjects)</c>.</summary>
    private static NpgsqlCommand KeysCommand(CampusPulseDbContext db, string sql, List<Key> keys)
    {
        var command = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("users", keys.Select(k => k.UserId).ToArray());
        command.Parameters.AddWithValue("dates", keys.Select(k => k.Date).ToArray());
        command.Parameters.AddWithValue("starts", keys.Select(k => k.StartsAt).ToArray());
        command.Parameters.AddWithValue("subjects", keys.Select(k => k.SubjectId).ToArray());
        return command;
    }

    /// <summary>A reminder's identity, as <c>class_reminders_sent</c> keys it.</summary>
    private readonly record struct Key(Guid UserId, DateOnly Date, TimeOnly StartsAt, Guid SubjectId);

    private sealed record Due(Guid UserId, OccurrenceResponse Occurrence, DateTimeOffset StartsAt)
    {
        public Key Key => new(UserId, Occurrence.Date, Occurrence.StartsAt, Occurrence.SubjectId);
    }

    [LoggerMessage(EventId = 60, Level = LogLevel.Information,
        Message = "Class reminders: {Due} due, {Sent} sent, {AlreadySent} already sent, {Retrying} to retry")]
    private static partial void LogTick(ILogger logger, int due, int sent, int alreadySent, int retrying);

    [LoggerMessage(EventId = 61, Level = LogLevel.Warning, Message = "Class reminder tick failed: {Reason}")]
    private static partial void LogTickFailed(ILogger logger, string reason, Exception exception);

    [LoggerMessage(EventId = 62, Level = LogLevel.Information, Message = "Class reminders are off (Reminders:Enabled is false)")]
    private static partial void LogOff(ILogger logger);
}
