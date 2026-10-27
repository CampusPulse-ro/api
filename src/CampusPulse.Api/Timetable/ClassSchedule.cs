using CampusPulse.Domain.Timetable;
using CampusPulse.Infrastructure;
using Microsoft.EntityFrameworkCore;

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

/// <summary>What a student's own timetable is made of: their study group, if they picked one, and the subjects
/// they chose (with the offered slot picked, if any).</summary>
public sealed record StudentTimetableChoices(Guid UserId, Guid? StudyGroupId, IReadOnlyList<StudentSubject> Subjects)
{
    /// <summary>The choices of each of <paramref name="userIds"/>, in that order (a student without settings has
    /// no group and no subjects). Two queries, however many students.</summary>
    public static async Task<IReadOnlyList<StudentTimetableChoices>> LoadAsync(CampusPulseDbContext db, IReadOnlyList<Guid> userIds,
        CancellationToken cancellationToken)
    {
        var ids = userIds.ToList();
        var groups = await db.StudentSettings.Where(s => ids.Contains(s.UserId))
            .ToDictionaryAsync(s => s.UserId, s => s.StudyGroupId, cancellationToken);
        var subjects = (await db.StudentSubjects.Where(s => ids.Contains(s.UserId)).ToListAsync(cancellationToken))
            .ToLookup(s => s.UserId);
        return [.. ids.Select(id => new StudentTimetableChoices(id, groups.GetValueOrDefault(id), [.. subjects[id]]))];
    }
}

/// <summary>
/// The classes of study groups and students, on dates: a group's grid slots, and for a student those minus the
/// optional subjects they didn't choose, plus the offered slots they picked. Each lands on the dates of its weeks
/// that have classes (teaching weeks minus non-teaching days), in Romanian dates and times. Loaded once for a date
/// range and any number of groups and students, then expanded in memory: <c>GET /me/timetable</c> and
/// <c>GET /timetable/groups/{id}</c> use it for one, the class reminders for every student who has them on.
/// </summary>
public sealed class ClassSchedule
{
    private readonly DateOnly _from;
    private readonly DateOnly _to;
    private readonly ILookup<Guid, TimetableSlot> _slotsByGroup;
    private readonly Dictionary<Guid, OfferedSlot> _offers;
    private readonly Dictionary<Guid, Subject> _subjects;
    private readonly Dictionary<Guid, string> _rooms;
    private readonly Dictionary<Guid, Semester> _semesters;

    private ClassSchedule(DateOnly from, DateOnly to, List<TimetableSlot> slots, List<OfferedSlot> offers,
        Dictionary<Guid, Subject> subjects, Dictionary<Guid, string> rooms, Dictionary<Guid, Semester> semesters)
    {
        _from = from;
        _to = to;
        _slotsByGroup = slots.SelectMany(s => s.Groups.Select(g => (g.StudyGroupId, Slot: s))).ToLookup(x => x.StudyGroupId, x => x.Slot);
        _offers = offers.ToDictionary(o => o.Id);
        _subjects = subjects;
        _rooms = rooms;
        _semesters = semesters;
    }

    /// <summary>What the classes of <paramref name="groupIds"/> and the offered slots <paramref name="offeredSlotIds"/>
    /// need from <paramref name="from"/> to <paramref name="to"/> (both included): their slots on those weekdays, with
    /// their subjects, rooms and semesters.</summary>
    public static async Task<ClassSchedule> LoadAsync(CampusPulseDbContext db, IReadOnlyList<Guid> groupIds,
        IReadOnlyList<Guid> offeredSlotIds, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var (groups, offerIds) = (groupIds.ToList(), offeredSlotIds.ToList());
        // A range shorter than a week (a day's timetable, the reminders' today and tomorrow) loads only its weekdays.
        var weekdays = Enumerable.Range(0, Math.Min(7, Math.Max(0, to.DayNumber - from.DayNumber + 1)))
            .Select(i => from.AddDays(i).DayOfWeek).ToList();
        var slotQuery = db.TimetableSlots.Include(s => s.Groups).Where(s => s.Groups.Any(g => groups.Contains(g.StudyGroupId)));
        var offerQuery = db.OfferedSlots.Where(o => offerIds.Contains(o.Id));
        if (weekdays.Count < 7)
        {
            slotQuery = slotQuery.Where(s => weekdays.Contains(s.Weekday));
            offerQuery = offerQuery.Where(o => weekdays.Contains(o.Weekday));
        }

        var slots = groups.Count == 0 || weekdays.Count == 0 ? [] : await slotQuery.ToListAsync(cancellationToken);
        var offers = offerIds.Count == 0 || weekdays.Count == 0 ? [] : await offerQuery.ToListAsync(cancellationToken);
        var subjectIds = slots.Select(s => s.SubjectId).Concat(offers.Select(o => o.SubjectId)).Distinct().ToList();
        var subjects = subjectIds.Count == 0
            ? []
            : await db.Subjects.Where(s => subjectIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);
        var rooms = await RoomCodesAsync(db, slots.Select(s => s.RoomId).Concat(offers.Select(o => o.RoomId)), cancellationToken);
        var semesterIds = subjects.Values.Select(s => s.SemesterId).Distinct().ToList();
        var semesters = semesterIds.Count == 0
            ? []
            : await db.Semesters.Include(s => s.Weeks).Include(s => s.NonTeachingDays)
                .Where(s => semesterIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);
        return new ClassSchedule(from, to, slots, offers, subjects, rooms, semesters);
    }

    /// <summary>Everything the timetables of <paramref name="students"/> need from <paramref name="from"/> to
    /// <paramref name="to"/>.</summary>
    public static Task<ClassSchedule> ForStudentsAsync(CampusPulseDbContext db, IEnumerable<StudentTimetableChoices> students,
        DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var list = students.ToList();
        return LoadAsync(db,
            [.. list.Select(s => s.StudyGroupId).OfType<Guid>().Distinct()],
            [.. list.SelectMany(s => s.Subjects).Select(s => s.OfferedSlotId).OfType<Guid>().Distinct()],
            from, to, cancellationToken);
    }

    /// <summary>A study group's classes from <paramref name="from"/> to <paramref name="to"/> (both included, within
    /// the loaded range), optional subjects included (marked).</summary>
    public IReadOnlyList<OccurrenceResponse> ForGroup(Guid groupId, DateOnly from, DateOnly to) => Expand(groupId, choices: null, from, to);

    /// <summary>A student's classes from <paramref name="from"/> to <paramref name="to"/> (both included, within the
    /// loaded range): their group's, minus optional subjects they didn't choose, plus the offered slots they picked.
    /// Without a group only the picks.</summary>
    public IReadOnlyList<OccurrenceResponse> ForStudent(StudentTimetableChoices student, DateOnly from, DateOnly to) =>
        Expand(student.StudyGroupId, student.Subjects, from, to);

    /// <summary>The code of each of <paramref name="roomIds"/> that is a room.</summary>
    internal static async Task<Dictionary<Guid, string>> RoomCodesAsync(CampusPulseDbContext db, IEnumerable<Guid?> roomIds,
        CancellationToken cancellationToken)
    {
        var ids = roomIds.OfType<Guid>().Distinct().ToList();
        return ids.Count == 0 ? [] : await db.Rooms.Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Code, cancellationToken);
    }

    internal static string? RoomCode(Dictionary<Guid, string> rooms, Guid? roomId) => roomId is { } id ? rooms.GetValueOrDefault(id) : null;

    /// <summary>In date and time order, then by subject name.</summary>
    private List<OccurrenceResponse> Expand(Guid? groupId, IReadOnlyList<StudentSubject>? choices, DateOnly from, DateOnly to)
    {
        if (from < _from || to > _to)
        {
            throw new ArgumentOutOfRangeException(nameof(to), $"{from:yyyy-MM-dd}..{to:yyyy-MM-dd} is outside the loaded {_from:yyyy-MM-dd}..{_to:yyyy-MM-dd}.");
        }

        IEnumerable<TimetableSlot> slots = groupId is { } id ? _slotsByGroup[id] : [];
        var offers = choices?.Select(c => c.OfferedSlotId).OfType<Guid>().Distinct()
            .Select(id => _offers.GetValueOrDefault(id)).OfType<OfferedSlot>().ToList() ?? [];
        if (choices is not null)
        {
            // A student's own timetable: an optional subject's classes only when they chose it.
            var chosen = choices.Select(c => c.SubjectId).ToHashSet();
            slots = slots.Where(s => _subjects[s.SubjectId].Choice != SubjectChoice.Optional || chosen.Contains(s.SubjectId));
        }

        var groupSlots = slots.ToList();
        var occurrences = new List<OccurrenceResponse>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            foreach (var slot in groupSlots.Where(s => s.Weekday == date.DayOfWeek))
            {
                var subject = _subjects[slot.SubjectId];
                if (OnDate(_semesters[subject.SemesterId], slot.Weeks, date) is { } week)
                {
                    occurrences.Add(new OccurrenceResponse(date, week, slot.StartsAt, slot.EndsAt, subject.Id, subject.Name, subject.ShortName,
                        CampusVirtual.CourseUrl(subject.CampusVirtualCourseId), slot.Kinds.List(), RoomCode(_rooms, slot.RoomId), slot.Teacher,
                        slot.Note, subject.Choice == SubjectChoice.Optional, slot.Id, null));
                }
            }

            foreach (var offer in offers.Where(o => o.Weekday == date.DayOfWeek))
            {
                var subject = _subjects[offer.SubjectId];
                if (OnDate(_semesters[subject.SemesterId], offer.Weeks, date) is { } week)
                {
                    occurrences.Add(new OccurrenceResponse(date, week, offer.StartsAt, offer.EndsAt, subject.Id, subject.Name, subject.ShortName,
                        CampusVirtual.CourseUrl(subject.CampusVirtualCourseId), offer.Kinds.List(), RoomCode(_rooms, offer.RoomId), null, null,
                        subject.Choice == SubjectChoice.Optional, null, offer.Id));
                }
            }
        }

        return [.. occurrences.OrderBy(o => o.Date).ThenBy(o => o.StartsAt).ThenBy(o => o.Subject, StringComparer.InvariantCulture)];
    }

    /// <summary>The teaching week when a class with these weeks happens on this date, or null (no classes
    /// that day, or not one of its weeks).</summary>
    private static int? OnDate(Semester semester, WeekSet weeks, DateOnly date) =>
        semester.HasClasses(date) && semester.WeekOf(date) is { } week && weeks.Contains(week) ? week : null;
}
