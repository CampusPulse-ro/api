namespace CampusPulse.Domain.Timetable;

/// <summary>
/// A semester of one academic year ("2026-2027", semester 1): its teaching weeks with their dates and
/// the days without classes inside them. Everything else in the timetable belongs to one semester.
/// </summary>
public sealed class Semester
{
    public const int AcademicYearMaxLength = 9;

    private readonly List<TeachingWeek> _weeks = [];
    private readonly List<NonTeachingDay> _nonTeachingDays = [];

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

    public int WeekCount => _weeks.Count;

    /// <summary>Replaces the calendar: week <c>n</c> starts on <c>weekStarts[n - 1]</c> (a Monday).</summary>
    public void SetCalendar(IReadOnlyList<DateOnly> weekStarts, IEnumerable<(DateOnly Date, string Reason)> nonTeachingDays)
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

/// <summary>A day inside a teaching week without classes (a public holiday).</summary>
public sealed class NonTeachingDay
{
    public const int ReasonMaxLength = 200;

    private NonTeachingDay()
    {
    }

    internal NonTeachingDay(Guid semesterId, DateOnly date, string reason)
    {
        SemesterId = semesterId;
        Date = date;
        Reason = reason.Length > ReasonMaxLength ? reason[..ReasonMaxLength] : reason;
    }

    public Guid SemesterId { get; private set; }

    public DateOnly Date { get; private set; }

    public string Reason { get; private set; } = "";
}
