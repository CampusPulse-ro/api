namespace CampusPulse.Domain.Timetable;

/// <summary>
/// The teaching weeks an activity runs in, as a bit mask: bit <c>n - 1</c> set = week <c>n</c>. A
/// semester has at most <see cref="MaxWeeks"/> weeks (UPT's have 14). Parity is a set too: odd weeks
/// are 1, 3, 5, …, so "odd weeks in 1-7" is <c>Range(1, 7) ∩ Odd(n)</c>.
/// </summary>
public readonly record struct WeekSet(int Mask)
{
    public const int MaxWeeks = 30;

    public static WeekSet None => new(0);

    /// <summary>Every week of a semester of <paramref name="weekCount"/> weeks.</summary>
    public static WeekSet All(int weekCount) => Range(1, weekCount);

    /// <summary>Weeks <paramref name="first"/> to <paramref name="last"/>, both included; out-of-range
    /// weeks are dropped (a sheet's "weeks 1-16" in a 14-week semester means weeks 1-14).</summary>
    public static WeekSet Range(int first, int last)
    {
        var mask = 0;
        for (var week = Math.Max(1, first); week <= Math.Min(MaxWeeks, last); week++)
        {
            mask |= Bit(week);
        }

        return new WeekSet(mask);
    }

    public static WeekSet Of(IEnumerable<int> weeks) =>
        new(weeks.Where(w => w is >= 1 and <= MaxWeeks).Aggregate(0, (mask, week) => mask | Bit(week)));

    public static WeekSet Odd(int weekCount) => Of(Enumerable.Range(1, weekCount).Where(w => w % 2 == 1));

    public static WeekSet Even(int weekCount) => Of(Enumerable.Range(1, weekCount).Where(w => w % 2 == 0));

    public bool IsEmpty => Mask == 0;

    public bool Contains(int week) => week is >= 1 and <= MaxWeeks && (Mask & Bit(week)) != 0;

    public WeekSet Intersect(WeekSet other) => new(Mask & other.Mask);

    public WeekSet Union(WeekSet other) => new(Mask | other.Mask);

    public IReadOnlyList<int> Weeks() => Enumerable.Range(1, MaxWeeks).Where(Contains).ToList();

    /// <summary>"1-7, 9, 11-14": ranges of consecutive weeks, for logs and the import report.</summary>
    public override string ToString()
    {
        var weeks = Weeks();
        if (weeks.Count == 0)
        {
            return "none";
        }

        var parts = new List<string>();
        for (var i = 0; i < weeks.Count;)
        {
            var j = i;
            while (j + 1 < weeks.Count && weeks[j + 1] == weeks[j] + 1)
            {
                j++;
            }

            parts.Add(j > i ? $"{weeks[i]}-{weeks[j]}" : $"{weeks[i]}");
            i = j + 1;
        }

        return string.Join(", ", parts);
    }

    private static int Bit(int week) => 1 << (week - 1);
}
