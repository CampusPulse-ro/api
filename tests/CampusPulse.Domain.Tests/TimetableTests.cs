using CampusPulse.Domain.Timetable;

namespace CampusPulse.Domain.Tests;

public sealed class WeekSetTests
{
    [Fact]
    public void Ranges_lists_and_parity_combine_as_sets()
    {
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], WeekSet.Range(1, 7).Weeks());
        Assert.Equal([1, 3, 5, 7], WeekSet.Range(1, 7).Intersect(WeekSet.Odd(14)).Weeks());
        Assert.Equal([2, 4, 6, 8, 10, 12, 14], WeekSet.Even(14).Weeks());
        Assert.Equal([3, 5, 9], WeekSet.Of([9, 3, 5, 3]).Weeks());
        Assert.Equal(14, WeekSet.All(14).Weeks().Count);
    }

    [Fact]
    public void Weeks_outside_the_possible_range_are_dropped()
    {
        Assert.Equal([12, 13, 14], WeekSet.Range(12, 16).Intersect(WeekSet.All(14)).Weeks());
        Assert.True(WeekSet.Of([0, -1, 31]).IsEmpty);
        Assert.False(WeekSet.All(14).Contains(15));
    }

    [Fact]
    public void It_prints_as_ranges() =>
        Assert.Equal("1-7, 9, 11-14", WeekSet.Range(1, 7).Union(WeekSet.Of([9])).Union(WeekSet.Range(11, 14)).ToString());
}

public sealed class SemesterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    /// <summary>2026-27 semester I: weeks 1-12 from 28 Sep, the winter break, weeks 13-14 from 11 Jan.</summary>
    private static Semester Upt()
    {
        var semester = new Semester("2026-2027", 1, Now);
        var starts = Enumerable.Range(0, 12).Select(i => new DateOnly(2026, 9, 28).AddDays(7 * i))
            .Concat([new DateOnly(2027, 1, 11), new DateOnly(2027, 1, 18)]).ToList();
        semester.SetCalendar(starts,
            [(new DateOnly(2026, 11, 30), new CalendarName("Zi libera", null)), (new DateOnly(2026, 12, 1), new CalendarName("Zi libera", "Day off"))],
            [(new DateOnly(2026, 12, 21), new DateOnly(2027, 1, 10), new CalendarName("Vacanta", "Break"))]);
        return semester;
    }

    [Fact]
    public void Dates_map_to_their_teaching_week_and_breaks_to_none()
    {
        var semester = Upt();

        Assert.Equal(1, semester.WeekOf(new DateOnly(2026, 9, 28)));
        Assert.Equal(1, semester.WeekOf(new DateOnly(2026, 10, 4)));   // the week's Sunday
        Assert.Equal(12, semester.WeekOf(new DateOnly(2026, 12, 18)));
        Assert.Null(semester.WeekOf(new DateOnly(2026, 12, 28)));      // winter break
        Assert.Equal(13, semester.WeekOf(new DateOnly(2027, 1, 11)));
        Assert.Null(semester.WeekOf(new DateOnly(2027, 1, 25)));       // after the semester
        Assert.Equal(new DateOnly(2027, 1, 24), semester.LastDay);
    }

    [Fact]
    public void Non_teaching_days_have_no_classes()
    {
        var semester = Upt();

        Assert.False(semester.HasClasses(new DateOnly(2026, 11, 30)));
        Assert.False(semester.HasClasses(new DateOnly(2026, 12, 1)));
        Assert.True(semester.HasClasses(new DateOnly(2026, 12, 2)));
        Assert.False(semester.HasClasses(new DateOnly(2026, 12, 23)));
    }

    [Fact]
    public void Names_keep_both_languages_and_a_break_may_have_none()
    {
        var semester = Upt();

        Assert.Equal(new CalendarName("Zi libera", "Day off"), semester.NonTeachingDays.Single(d => d.Date == new DateOnly(2026, 12, 1)).Reason);
        Assert.Null(semester.NonTeachingDays.Single(d => d.Date == new DateOnly(2026, 11, 30)).ReasonEn);
        var winter = Assert.Single(semester.Breaks);
        Assert.Equal((new DateOnly(2026, 12, 21), new DateOnly(2027, 1, 10), "Break"), (winter.From, winter.To, winter.Name?.En));
        Assert.Null(new CalendarName("Vacanta", " ").En);
        Assert.Throws<ArgumentException>(() => new CalendarName(" ", "Break"));

        semester.SetCalendar([new DateOnly(2026, 9, 28)], [], [(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2), null)]);
        Assert.Null(Assert.Single(semester.Breaks).Name);
        Assert.Throws<ArgumentException>(() => semester.SetCalendar([new DateOnly(2026, 9, 28)], [],
            [(new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 1), null)]));
    }

    [Fact]
    public void Weeks_start_on_Mondays_in_order()
    {
        var semester = new Semester("2026-2027", 1, Now);

        Assert.Throws<ArgumentException>(() => semester.SetCalendar([new DateOnly(2026, 9, 29)], [], []));
        Assert.Throws<ArgumentException>(() => semester.SetCalendar([new DateOnly(2026, 10, 5), new DateOnly(2026, 9, 28)], [], []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Semester("2026-2027", 3, Now));
    }

    [Fact]
    public void Kinds_list_in_a_fixed_order() =>
        Assert.Equal([ActivityKind.Lab, ActivityKind.Project], (ActivityKinds.Project | ActivityKinds.Lab).List());

    [Fact]
    public void A_group_without_subgroups_is_labelled_by_its_number()
    {
        Assert.Equal("2.1", new StudyGroup(Guid.NewGuid(), Guid.NewGuid(), 3, "", 2, 1, "f.xlsx", Now).Label);
        Assert.Equal("1", new StudyGroup(Guid.NewGuid(), Guid.NewGuid(), 1, "", 1, 0, "f.xlsx", Now).Label);
    }
}
