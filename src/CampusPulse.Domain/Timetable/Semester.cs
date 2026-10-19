namespace CampusPulse.Domain.Timetable;

/// <summary>A calendar name in Romanian and, when known, English ("Ziua Națională a României" /
/// "Romania's National Day"): the clients show the one of their UI language.</summary>
public sealed record CalendarName(string Ro, string? En)
{
    public const int MaxLength = 200;

    public string Ro { get; } = string.IsNullOrWhiteSpace(Ro) ? throw new ArgumentException("A Romanian name is required.", nameof(Ro))
        : Clamp(Ro)!;

    public string? En { get; } = string.IsNullOrWhiteSpace(En) ? null : Clamp(En);

    private static string? Clamp(string? text) => text is { Length: > MaxLength } ? text[..MaxLength] : text?.Trim();
}

/// <summary>
/// A semester of one academic year ("2026-2027", semester 1): its teaching weeks with their dates, the
/// days without classes inside them, and its breaks. Everything else in the timetable belongs to one semester.
/// </summary>
public sealed class Semester
{
    public const int AcademicYearMaxLength = 9;

    private readonly List<TeachingWeek> _weeks = [];
    private readonly List<NonTeachingDay> _nonTeachingDays = [];
    private readonly List<SemesterBreak> _breaks = [];

    private Semester()
    {
    }

    public Semester(string academicYear, int number, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(academicYear) || academicYear.Length > AcademicYearMaxLength)
        {
            throw new ArgumentException("An academic year like 2026-2027 is required.", nameof(academicYear));
        }

        if (number is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(number), "A semester is 1 or 2.");
        }

        Id = Guid.CreateVersion7(now);
        AcademicYear = academicYear;
        Number = number;
    }

    public Guid Id { get; private set; }

    public string AcademicYear { get; private set; } = "";

    public int Number { get; private set; }

    public IReadOnlyList<TeachingWeek> Weeks => _weeks;

    public IReadOnlyList<NonTeachingDay> NonTeachingDays => _nonTeachingDays;

    /// <summary>The breaks (holidays between teaching weeks), with their dates and names.</summary>
    public IReadOnlyList<SemesterBreak> Breaks => _breaks;

    public int WeekCount => _weeks.Count;

    /// <summary>Replaces the calendar: week <c>n</c> starts on <c>weekStarts[n - 1]</c> (a Monday).</summary>
    public void SetCalendar(IReadOnlyList<DateOnly> weekStarts, IEnumerable<(DateOnly Date, CalendarName Reason)> nonTeachingDays,
        IEnumerable<(DateOnly From, DateOnly To, CalendarName? Name)> breaks)
    {
        if (weekStarts.Count is 0 or > WeekSet.MaxWeeks)
        {
            throw new ArgumentException($"A semester has 1-{WeekSet.MaxWeeks} weeks.", nameof(weekStarts));
        }

        for (var i = 0; i < weekStarts.Count; i++)
        {
            if (weekStarts[i].DayOfWeek != DayOfWeek.Monday || (i > 0 && weekStarts[i] <= weekStarts[i - 1]))
            {
                throw new ArgumentException("Teaching weeks start on Mondays, in order.", nameof(weekStarts));
            }
        }

        _weeks.Clear();
        _weeks.AddRange(weekStarts.Select((start, i) => new TeachingWeek(Id, i + 1, start)));
        _nonTeachingDays.Clear();
        _nonTeachingDays.AddRange(nonTeachingDays.DistinctBy(d => d.Date).Select(d => new NonTeachingDay(Id, d.Date, d.Reason)));
        _breaks.Clear();
        foreach (var (from, to, name) in breaks.DistinctBy(b => b.From))
        {
            _breaks.Add(to < from
                ? throw new ArgumentException($"The break from {from:yyyy-MM-dd} ends before it starts.", nameof(breaks))
                : new SemesterBreak(Id, from, to, name));
        }
    }

    public DateOnly FirstDay => _weeks[0].StartsOn;

    public DateOnly LastDay => _weeks[^1].StartsOn.AddDays(6);

    /// <summary>The teaching week <paramref name="date"/> falls in, or null (a break, or outside the
    /// semester). Weekends count as part of their week; whether there are classes is
    /// <see cref="HasClasses"/>.</summary>
    public int? WeekOf(DateOnly date)
    {
        foreach (var week in _weeks)
        {
            if (date >= week.StartsOn && date <= week.StartsOn.AddDays(6))
            {
                return week.Number;
            }
        }

        return null;
    }

    /// <summary>A day in a teaching week that isn't a non-teaching day.</summary>
    public bool HasClasses(DateOnly date) => WeekOf(date) is not null && _nonTeachingDays.All(d => d.Date != date);
}

/// <summary>Week <see cref="Number"/> of a semester, starting on Monday <see cref="StartsOn"/>.</summary>
public sealed class TeachingWeek
{
    private TeachingWeek()
    {
    }

    internal TeachingWeek(Guid semesterId, int number, DateOnly startsOn)
    {
        SemesterId = semesterId;
        Number = number;
        StartsOn = startsOn;
    }

    public Guid SemesterId { get; private set; }

    public int Number { get; private set; }

    public DateOnly StartsOn { get; private set; }
}

/// <summary>A day inside a teaching week without classes (a public holiday), and why, in Romanian and English.</summary>
public sealed class NonTeachingDay
{
    private NonTeachingDay()
    {
    }

    internal NonTeachingDay(Guid semesterId, DateOnly date, CalendarName reason)
    {
        SemesterId = semesterId;
        Date = date;
        ReasonRo = reason.Ro;
        ReasonEn = reason.En;
    }

    public Guid SemesterId { get; private set; }

    public DateOnly Date { get; private set; }

    public string ReasonRo { get; private set; } = "";

    /// <summary>Null when the overrides give only the Romanian reason.</summary>
    public string? ReasonEn { get; private set; }

    public CalendarName Reason => new(ReasonRo, ReasonEn);
}

/// <summary>A break from <see cref="From"/> to <see cref="To"/> (dates included), maybe named ("Vacanța de iarnă").</summary>
public sealed class SemesterBreak
{
    private SemesterBreak()
    {
    }

    internal SemesterBreak(Guid semesterId, DateOnly from, DateOnly to, CalendarName? name)
    {
        SemesterId = semesterId;
        From = from;
        To = to;
        NameRo = name?.Ro;
        NameEn = name?.En;
    }

    public Guid SemesterId { get; private set; }

    public DateOnly From { get; private set; }

    public DateOnly To { get; private set; }

    public string? NameRo { get; private set; }

    public string? NameEn { get; private set; }

    /// <summary>Null for a break without a name.</summary>
    public CalendarName? Name => NameRo is null ? null : new(NameRo, NameEn);
}
