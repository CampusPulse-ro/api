using CampusPulse.Domain.Timetable;
using CampusPulse.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CampusPulse.Api.Timetable;

/// <summary>Romanian local time (Europe/Bucharest), which every date and time of the timetable is in.</summary>
public static class RomanianTime
{
    /// <summary>Without the time-zone database the API would date every class in UTC, so the first timetable
    /// request (or reminder tick) fails instead.</summary>
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");

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
/// that have classes (teaching weeks minus non-teaching days), in Romanian dates and times. Loaded once for any
/// number of groups and students, then expanded in memory: <c>GET /me/timetable</c> and
/// <c>GET /timetable/groups/{id}</c> use it for one, the class reminders for every student who has them on.
/// </summary>
public sealed class ClassSchedule
{
    private readonly List<TimetableSlot> _slots;
    private readonly List<OfferedSlot> _offers;
    private readonly Dictionary<Guid, Subject> _subjects;
    private readonly Dictionary<Guid, string> _rooms;
    private readonly Dictionary<Guid, Semester> _semesters;

    private ClassSchedule(List<TimetableSlot> slots, List<OfferedSlot> offers, Dictionary<Guid, Subject> subjects,
        Dictionary<Guid, string> rooms, Dictionary<Guid, Semester> semesters)
    {
        _slots = slots;
        _offers = offers;
        _subjects = subjects;
        _rooms = rooms;
        _semesters = semesters;
    }

    /// <summary>The slots of <paramref name="groupIds"/> and the offered slots <paramref name="offeredSlotIds"/>,
    /// with their subjects, rooms and semesters.</summary>
    public static async Task<ClassSchedule> LoadAsync(CampusPulseDbContext db, IReadOnlyList<Guid> groupIds,
        IReadOnlyList<Guid> offeredSlotIds, CancellationToken cancellationToken)
    {
        var (groups, offerIds) = (groupIds.ToList(), offeredSlotIds.ToList());
        var slots = groups.Count == 0
            ? []
            : await db.TimetableSlots.Include(s => s.Groups)
                .Where(s => s.Groups.Any(g => groups.Contains(g.StudyGroupId)))
                .ToListAsync(cancellationToken);
        var offers = offerIds.Count == 0
            ? []
            : await db.OfferedSlots.Where(o => offerIds.Contains(o.Id)).ToListAsync(cancellationToken);
        var subjectIds = slots.Select(s => s.SubjectId).Concat(offers.Select(o => o.SubjectId)).Distinct().ToList();
        var subjects = subjectIds.Count == 0
            ? []
            : await db.Subjects.Where(s => subjectIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);
        var roomIds = slots.Select(s => s.RoomId).Concat(offers.Select(o => o.RoomId)).OfType<Guid>().Distinct().ToList();
        var rooms = roomIds.Count == 0
            ? []
            : await db.Rooms.Where(r => roomIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Code, cancellationToken);
        var semesterIds = subjects.Values.Select(s => s.SemesterId).Distinct().ToList();
        var semesters = semesterIds.Count == 0
            ? []
            : await db.Semesters.Include(s => s.Weeks).Include(s => s.NonTeachingDays)
                .Where(s => semesterIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);
        return new ClassSchedule(slots, offers, subjects, rooms, semesters);
    }

    /// <summary>Everything the timetables of <paramref name="students"/> need.</summary>
    public static Task<ClassSchedule> ForStudentsAsync(CampusPulseDbContext db, IEnumerable<StudentTimetableChoices> students,
        CancellationToken cancellationToken)
    {
        var list = students.ToList();
        return LoadAsync(db,
            [.. list.Select(s => s.StudyGroupId).OfType<Guid>().Distinct()],
            [.. list.SelectMany(s => s.Subjects).Select(s => s.OfferedSlotId).OfType<Guid>().Distinct()],
            cancellationToken);
    }

    /// <summary>A study group's classes from <paramref name="from"/> to <paramref name="to"/> (both included),
    /// optional subjects included (marked).</summary>
    public IReadOnlyList<OccurrenceResponse> ForGroup(Guid groupId, DateOnly from, DateOnly to) => Expand(groupId, choices: null, from, to);

    /// <summary>A student's classes from <paramref name="from"/> to <paramref name="to"/> (both included): their
    /// group's, minus optional subjects they didn't choose, plus the offered slots they picked. Without a group
    /// only the picks.</summary>
    public IReadOnlyList<OccurrenceResponse> ForStudent(StudentTimetableChoices student, DateOnly from, DateOnly to) =>
        Expand(student.StudyGroupId, student.Subjects, from, to);

    /// <summary>In date and time order, then by subject name.</summary>
    private List<OccurrenceResponse> Expand(Guid? groupId, IReadOnlyList<StudentSubject>? choices, DateOnly from, DateOnly to)
    {
        IEnumerable<TimetableSlot> slots = groupId is { } id ? _slots.Where(s => s.Groups.Any(g => g.StudyGroupId == id)) : [];
        var pickedOfferIds = choices?.Select(c => c.OfferedSlotId).OfType<Guid>().ToHashSet() ?? [];
        var offers = _offers.Where(o => pickedOfferIds.Contains(o.Id)).ToList();
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
                        CampusVirtual.CourseUrl(subject.CampusVirtualCourseId), slot.Kinds.List(), Room(slot.RoomId), slot.Teacher, slot.Note,
                        subject.Choice == SubjectChoice.Optional, slot.Id, null));
                }
            }

            foreach (var offer in offers.Where(o => o.Weekday == date.DayOfWeek))
            {
                var subject = _subjects[offer.SubjectId];
                if (OnDate(_semesters[subject.SemesterId], offer.Weeks, date) is { } week)
                {
                    occurrences.Add(new OccurrenceResponse(date, week, offer.StartsAt, offer.EndsAt, subject.Id, subject.Name, subject.ShortName,
                        CampusVirtual.CourseUrl(subject.CampusVirtualCourseId), offer.Kinds.List(), Room(offer.RoomId), null, null,
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

    private string? Room(Guid? roomId) => roomId is { } id ? _rooms.GetValueOrDefault(id) : null;
}
