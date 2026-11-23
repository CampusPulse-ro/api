namespace CampusPulse.Api.Timetable;

/// <summary>Romanian local time (Europe/Bucharest), which every date and time of the timetable is in.</summary>
public static class RomanianTime
{
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");

    /// <summary>Called at startup (<see cref="TimetableEndpoints.MapTimetable"/>): without the time-zone database the
    /// API would date every class in UTC, so it doesn't start, rather than failing every timetable request and
    /// reminder tick.</summary>
    public static void EnsureAvailable() => _ = Zone.BaseUtcOffset;

    public static DateTime Local(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone).DateTime;

    public static DateOnly DateOf(DateTimeOffset instant) => DateOnly.FromDateTime(Local(instant));

    public static DateOnly Today(TimeProvider time) => DateOf(time.GetUtcNow());

    /// <summary>The instant <paramref name="time"/> on <paramref name="date"/> is in Romania. A time the clocks skip
    /// (03:00-04:00 on the last Sunday of March) is read in winter time, an hour later than it would be on the wall;
    /// a time that happens twice (the last Sunday of October) is the second one. Classes are never at that hour.</summary>
    public static DateTimeOffset Instant(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time);
        var offset = Zone.IsInvalidTime(local) ? Zone.BaseUtcOffset : Zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
