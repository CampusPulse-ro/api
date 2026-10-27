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
/// student's number of devices): <see cref="Due"/> found due, of which <see cref="Sent"/> reached the push service for
/// at least one device, <see cref="AlreadySent"/> had gone out before, and <see cref="Retrying"/> couldn't be sent at
/// all (the push service was down) and are tried again on the next tick.</summary>
public sealed record ClassReminderTick(int Due, int Sent, int AlreadySent, int Retrying)
{
    public static readonly ClassReminderTick Nothing = new(0, 0, 0, 0);
}

/// <summary>
/// Sends class reminders (CampusPulse-ro/api#53). Once a minute, for every student with a reminder setting and at
/// least one device: their classes, computed exactly as <c>GET /me/timetable</c> does (<see cref="ClassSchedule"/>),
/// whose start (Romanian time) minus the student's lead falls since the previous tick get one push each, on every
/// device. A class that has already started gets none, so after a restart or a stalled tick the catch-up is at most
/// the classes that haven't begun. Every reminder is recorded in <c>class_reminders_sent</c> just before it goes out,
/// so none is sent twice, across ticks and restarts; a reminder the push service couldn't take at all is released and
/// tried again on the next tick. Records older than <see cref="KeepSent"/> are deleted here. Logs counts only.
/// </summary>
public sealed partial class ClassReminderSender(IServiceScopeFactory scopes, TimeProvider time, IOptions<ReminderOptions> options,
    ILogger<ClassReminderSender> logger) : BackgroundService
{
    public const string ReminderType = "classReminder";

    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>How far back a tick looks at most: the first one after a start, or one after ticks that failed. No
    /// further than the longest lead, since a reminder due earlier is for a class that has started.</summary>
    public static readonly TimeSpan LookBack = TimeSpan.FromMinutes(ReminderSettings.MaxMinutesBefore);

    public static readonly TimeSpan KeepSent = TimeSpan.FromDays(2);

    /// <summary>The end of the last tick that left nothing to retry: the next one sends what fell due since. Null
    /// until the first, which looks back <see cref="LookBack"/>.</summary>
    private DateTimeOffset? _lastTick;

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
                // The window stays open: the next tick looks back to the last good one. Never stop the host over it.
                LogTickFailed(logger, e.GetType().Name, e);
            }
        }
    }

    /// <summary>One tick: sends the reminders due since the previous good tick (at most <see cref="LookBack"/> ago).
    /// One at a time: the loop above is the only caller, besides tests with <see cref="ReminderOptions.Enabled"/> off.</summary>
    public async Task<ClassReminderTick> TickAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var since = _lastTick is { } last && last > now - LookBack ? last : now - LookBack;
        var tick = await SendDueAsync(since, now, cancellationToken);
        if (tick.Retrying == 0)
        {
            _lastTick = now;
        }

        if (tick.Due > 0)
        {
            LogTick(logger, tick.Due, tick.Sent, tick.AlreadySent, tick.Retrying);
        }

        return tick;
    }

    /// <summary>A reminder for a class starting at <paramref name="startsAt"/>, <paramref name="lead"/> ahead, is due
    /// in the tick covering (<paramref name="since"/>, <paramref name="now"/>] unless the class has started.</summary>
    public static bool IsDue(DateTimeOffset startsAt, TimeSpan lead, DateTimeOffset since, DateTimeOffset now) =>
        startsAt > now && startsAt - lead > since && startsAt - lead <= now;

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

    private async Task<ClassReminderTick> SendDueAsync(DateTimeOffset since, DateTimeOffset now, CancellationToken cancellationToken)
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

        // A due class hasn't started and starts within its student's lead, at most an hour from now: today or,
        // late in the evening, tomorrow (Romanian dates).
        var students = await StudentTimetableChoices.LoadAsync(db, [.. leads.Keys], cancellationToken);
        var schedule = await ClassSchedule.ForStudentsAsync(db, students, cancellationToken);
        var (firstDay, lastDay) = (RomanianTime.DateOf(now), RomanianTime.DateOf(now + LookBack));
        var due = new List<Due>();
        foreach (var student in students)
        {
            // One reminder per subject and start: a grid class and a picked offer of the same subject at the same
            // time are one class to the student.
            foreach (var occurrence in schedule.ForStudent(student, firstDay, lastDay).DistinctBy(o => (o.Date, o.StartsAt, o.SubjectId)))
            {
                var startsAt = RomanianTime.Instant(occurrence.Date, occurrence.StartsAt);
                if (IsDue(startsAt, leads[student.UserId], since, now))
                {
                    due.Add(new Due(student.UserId, occurrence, startsAt));
                }
            }
        }

        if (due.Count == 0)
        {
            return ClassReminderTick.Nothing;
        }

        var dueUsers = due.Select(d => d.UserId).Distinct().ToList();
        var devices = (await db.PushDevices.Where(d => dueUsers.Contains(d.UserId)).ToListAsync(cancellationToken))
            .ToLookup(d => d.UserId);
        var claimed = new List<Due>();
        var alreadySent = 0;
        foreach (var reminder in due.Where(r => devices[r.UserId].Any()))
        {
            switch (await ClaimAsync(db, reminder, now, cancellationToken))
            {
                case true:
                    claimed.Add(reminder);
                    break;
                case false:
                    alreadySent++;
                    break;
            }
        }

        var messages = new List<(PushDevice Device, PushMessage Message)>();
        var owners = new List<int>();
        for (var i = 0; i < claimed.Count; i++)
        {
            var reminder = claimed[i];
            var left = reminder.StartsAt - now;
            var (title, body) = Text(reminder.Occurrence, Math.Max(1, (int)Math.Ceiling(left.TotalMinutes)));
            var data = Data(reminder.Occurrence);
            foreach (var device in devices[reminder.UserId])
            {
                // High priority: it's due in minutes, and Android may hold a normal one back. Worth delivering only
                // until the class starts.
                messages.Add((device, new PushMessage(device.Token, title, body)
                {
                    Data = data,
                    HighPriority = true,
                    TimeToLive = left,
                }));
                owners.Add(i);
            }
        }

        var delivery = scope.ServiceProvider.GetRequiredService<PushDelivery>();
        var tickets = await delivery.SendEachAsync(messages, cancellationToken);
        var outcomes = owners.Zip(tickets).GroupBy(x => x.First, x => x.Second).ToDictionary(g => g.Key, g => g.ToList());
        int sent = 0, retrying = 0;
        for (var i = 0; i < claimed.Count; i++)
        {
            var ticketsOf = outcomes.GetValueOrDefault(i) ?? [];
            if (ticketsOf.Any(t => t.Ok))
            {
                sent++;
            }
            else if (ticketsOf.Count > 0 && ticketsOf.All(t => PushDelivery.IsServiceFailure(t.Error)))
            {
                // Nothing reached the push service: give the reminder back, so the next tick tries again (while the
                // class hasn't started). Refused ones (a device gone) stay claimed: sending again changes nothing.
                await ReleaseAsync(db, claimed[i], cancellationToken);
                retrying++;
            }
        }

        return new ClassReminderTick(due.Count, sent, alreadySent, retrying);
    }

    /// <summary>Records the reminder as sent, unless it was: true when this tick sends it, false when an earlier one
    /// did, null when the student's account was deleted meanwhile. Before the send, so a crash in between loses a
    /// reminder rather than sending it twice.</summary>
    private static async Task<bool?> ClaimAsync(CampusPulseDbContext db, Due reminder, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            return await db.Database.ExecuteSqlAsync($"""
                INSERT INTO class_reminders_sent (user_id, date, starts_at, subject_id, sent_at)
                VALUES ({reminder.UserId}, {reminder.Occurrence.Date}, {reminder.Occurrence.StartsAt}, {reminder.Occurrence.SubjectId}, {now})
                ON CONFLICT DO NOTHING
                """, cancellationToken) == 1;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return null;
        }
    }

    private static Task ReleaseAsync(CampusPulseDbContext db, Due reminder, CancellationToken cancellationToken) =>
        db.SentClassReminders
            .Where(r => r.UserId == reminder.UserId && r.Date == reminder.Occurrence.Date
                && r.StartsAt == reminder.Occurrence.StartsAt && r.SubjectId == reminder.Occurrence.SubjectId)
            .ExecuteDeleteAsync(cancellationToken);

    private sealed record Due(Guid UserId, OccurrenceResponse Occurrence, DateTimeOffset StartsAt);

    [LoggerMessage(EventId = 60, Level = LogLevel.Information,
        Message = "Class reminders: {Due} due, {Sent} sent, {AlreadySent} already sent, {Retrying} to retry")]
    private static partial void LogTick(ILogger logger, int due, int sent, int alreadySent, int retrying);

    [LoggerMessage(EventId = 61, Level = LogLevel.Warning, Message = "Class reminder tick failed: {Reason}")]
    private static partial void LogTickFailed(ILogger logger, string reason, Exception exception);

    [LoggerMessage(EventId = 62, Level = LogLevel.Information, Message = "Class reminders are off (Reminders:Enabled is false)")]
    private static partial void LogOff(ILogger logger);
}
