namespace CampusPulse.Domain.Reminders;

/// <summary>
/// A student's class reminders: a push notification <see cref="MinutesBefore"/> minutes before each of their
/// classes. No row means reminders are off. Deleted with the account (cascade from <c>users</c>).
/// </summary>
public sealed class ReminderSettings
{
    /// <summary>The leads a student can pick, in minutes.</summary>
    public static readonly IReadOnlyList<int> AllowedMinutes = [5, 10, 15, 30, 60];

    /// <summary>The longest lead: how far ahead of a class a reminder can be due.</summary>
    public const int MaxMinutesBefore = 60;

    private ReminderSettings()
    {
    }

    public ReminderSettings(Guid userId, int minutesBefore, DateTimeOffset now)
    {
        if (!IsAllowed(minutesBefore))
        {
            throw new ArgumentOutOfRangeException(nameof(minutesBefore), minutesBefore,
                $"A reminder is {string.Join(", ", AllowedMinutes)} minutes before the class.");
        }

        UserId = userId;
        MinutesBefore = minutesBefore;
        UpdatedAt = now;
    }

    public Guid UserId { get; private set; }

    public int MinutesBefore { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static bool IsAllowed(int minutesBefore) => AllowedMinutes.Contains(minutesBefore);
}

/// <summary>
/// A class reminder that went out: one per student, date, start time and subject (Romanian date and time, as
/// in the timetable), so a reminder is never sent twice, across ticks and restarts. Written just before the
/// send; kept two days, then deleted by the sender. Deleted with the account (cascade from <c>users</c>).
/// </summary>
public sealed class SentClassReminder
{
    private SentClassReminder()
    {
    }

    public SentClassReminder(Guid userId, DateOnly date, TimeOnly startsAt, Guid subjectId, DateTimeOffset now)
    {
        UserId = userId;
        Date = date;
        StartsAt = startsAt;
        SubjectId = subjectId;
        SentAt = now;
    }

    public Guid UserId { get; private set; }

    public DateOnly Date { get; private set; }

    public TimeOnly StartsAt { get; private set; }

    public Guid SubjectId { get; private set; }

    public DateTimeOffset SentAt { get; private set; }
}
