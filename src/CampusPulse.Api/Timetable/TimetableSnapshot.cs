using System.Data;
using CampusPulse.Domain.Timetable;
using CampusPulse.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CampusPulse.Api.Timetable;

/// <summary>
/// The timetable as the imports left it: everything that is the same for every student (semesters and their
/// calendars, programmes, study groups, rooms, subjects, slots, offered slots, notes), read from one database
/// snapshot and never changed afterwards. <see cref="TimetableCache"/> keeps the current one in memory and swaps
/// in a new one after an import.
/// <para>Built once per import: each study group's weekly schedule (its slots with their subject, room and
/// semester resolved), the programme lists, the choices of each year and each room's weekly classes. A request
/// then only expands weeks into dates. <c>GET /me/timetable</c>, <c>GET /timetable/groups/{id}</c> and the class
/// reminders all use <see cref="ForGroup"/> / <see cref="ForStudent"/>, so they compute a class the same way.</para>
/// </summary>
public sealed class TimetableSnapshot
{
    private readonly IReadOnlyList<Semester> _semesters;
    private readonly Dictionary<Guid, StudyGroup> _groups;
    private readonly Dictionary<Guid, StudyGroupResponse> _groupResponses;
    private readonly Dictionary<Guid, Subject> _subjects;
    private readonly Dictionary<Guid, WeeklyClass[]> _weekByGroup;
    private readonly Dictionary<Guid, WeeklyClass> _offers;
    private readonly Dictionary<Guid, IReadOnlyList<ProgrammeResponse>> _programmesBySemester;
    private readonly Dictionary<(Guid Semester, Guid Programme, int Year), IReadOnlyList<ChoiceSubjectResponse>> _choices;
    private readonly ILookup<(Guid Semester, Guid Programme, int Year), TimetableNoteResponse> _notes;
    private readonly Dictionary<string, Room> _roomsByCode;
    private readonly ILookup<Guid, RoomClass> _classesByRoom;

    private TimetableSnapshot(Guid? version, List<Semester> semesters, List<Programme> programmes, List<StudyGroup> groups,
        List<Room> rooms, List<Subject> subjects, List<TimetableSlot> slots, List<OfferedSlot> offers, List<TimetableNote> notes)
    {
        Version = version;
        _semesters = semesters;
        var semestersById = semesters.ToDictionary(s => s.Id);
        var programmesById = programmes.ToDictionary(p => p.Id);
        _groups = groups.ToDictionary(g => g.Id);
        _groupResponses = groups.ToDictionary(g => g.Id, g => StudyGroupResponse.Of(g, programmesById[g.ProgrammeId]));
        _subjects = subjects.ToDictionary(s => s.Id);
        var roomCodes = rooms.ToDictionary(r => r.Id, r => r.Code);
        _roomsByCode = rooms.ToDictionary(r => r.Code, StringComparer.Ordinal);
        Rooms = [.. rooms.OrderBy(r => r.Code, StringComparer.Ordinal).Select(RoomResponse.Of)];

        string? RoomCode(Guid? roomId) => roomId is { } id ? roomCodes.GetValueOrDefault(id) : null;

        WeeklyClass Weekly(TimetableSlot slot)
        {
            var subject = _subjects[slot.SubjectId];
            return new WeeklyClass(slot.Weekday, slot.StartsAt, slot.EndsAt, subject, CampusVirtual.CourseUrl(subject.CampusVirtualCourseId),
                semestersById[subject.SemesterId], slot.Weeks, slot.Kinds.List(), RoomCode(slot.RoomId), slot.Teacher, slot.Note, slot.Id, null);
        }

        WeeklyClass Offered(OfferedSlot offer)
        {
            var subject = _subjects[offer.SubjectId];
            return new WeeklyClass(offer.Weekday, offer.StartsAt, offer.EndsAt, subject, CampusVirtual.CourseUrl(subject.CampusVirtualCourseId),
                semestersById[subject.SemesterId], offer.Weeks, offer.Kinds.List(), RoomCode(offer.RoomId), null, null, null, offer.Id);
        }

        _weekByGroup = slots.SelectMany(s => s.Groups.Select(g => (g.StudyGroupId, Slot: s)))
            .GroupBy(x => x.StudyGroupId, x => x.Slot)
            .ToDictionary(g => g.Key, g => g.Select(Weekly).ToArray());
        _offers = offers.ToDictionary(o => o.Id, Offered);

        // GET /timetable/programmes: each semester's offered groups, by programme code, year, series, group, subgroup.
        _programmesBySemester = groups.Where(g => g.RetiredAt == null).GroupBy(g => g.SemesterId).ToDictionary(s => s.Key,
            s => (IReadOnlyList<ProgrammeResponse>)[.. s.GroupBy(g => g.ProgrammeId).Select(p => programmesById[p.Key])
                .OrderBy(p => p.Code, StringComparer.Ordinal)
                .Select(p => new ProgrammeResponse(p.Code, p.Name,
                    [.. s.Where(g => g.ProgrammeId == p.Id).GroupBy(g => g.Year).OrderBy(y => y.Key).Select(y => new ProgrammeYearResponse(y.Key,
                        [.. y.OrderBy(g => g.Series, StringComparer.Ordinal).ThenBy(g => g.Group).ThenBy(g => g.Subgroup).Select(g => _groupResponses[g.Id])]))]))]);

        // GET /timetable/groups/{id}/choices: the optional and pick-a-time subjects of a year, with their offers.
        var offersBySubject = offers.ToLookup(o => o.SubjectId);
        _choices = subjects.Where(s => s.Choice != SubjectChoice.Fixed && s.RetiredAt == null)
            .GroupBy(s => (s.SemesterId, s.ProgrammeId, s.Year))
            .ToDictionary(y => y.Key, y => (IReadOnlyList<ChoiceSubjectResponse>)[.. y.OrderBy(s => s.Name, StringComparer.InvariantCulture)
                .Select(s => new ChoiceSubjectResponse(s.Id, s.Name, s.ShortName, CampusVirtual.CourseUrl(s.CampusVirtualCourseId), s.Choice,
                    [.. offersBySubject[s.Id].OrderBy(o => o.Weekday).ThenBy(o => o.StartsAt)
                        .Select(o => new OfferedSlotResponse(o.Id, o.Kinds.List(), o.Weekday, o.StartsAt, o.EndsAt, RoomCode(o.RoomId), o.Weeks.Weeks()))]))]);

        // Notes come ordered by their cell, as the database collates it.
        _notes = notes.ToLookup(n => (n.SemesterId, n.ProgrammeId, n.Year), n => new TimetableNoteResponse(n.Text));

        // GET /rooms/{code}/timetable: what is in each room, per weekday. A slot belongs to its own semester, an offer
        // to its subject's.
        var groupLabels = groups.ToDictionary(g => g.Id, g => $"{programmesById[g.ProgrammeId].Code} {g.Year} {g.Label}");
        _classesByRoom = slots.Where(s => s.RoomId is not null).Select(s => (Room: s.RoomId!.Value, Class: new RoomClass(s.SemesterId, s.Weekday, s.Weeks,
                new RoomOccurrenceResponse(s.StartsAt, s.EndsAt, _subjects[s.SubjectId].Name, CampusVirtual.CourseUrl(_subjects[s.SubjectId].CampusVirtualCourseId),
                    s.Kinds.List(), [.. s.Groups.Select(g => groupLabels[g.StudyGroupId]).Order(StringComparer.Ordinal)], Offered: false))))
            .Concat(offers.Where(o => o.RoomId is not null).Select(o => (Room: o.RoomId!.Value, Class: new RoomClass(_subjects[o.SubjectId].SemesterId, o.Weekday,
                o.Weeks, new RoomOccurrenceResponse(o.StartsAt, o.EndsAt, _subjects[o.SubjectId].Name,
                    CampusVirtual.CourseUrl(_subjects[o.SubjectId].CampusVirtualCourseId), o.Kinds.List(), [], Offered: true)))))
            .ToLookup(x => x.Room, x => x.Class);

        StudyGroups = groups.Count;
        Slots = slots.Count;
    }

    /// <summary>The newest import run's id: what this snapshot is of. Null when nothing was imported yet.</summary>
    public Guid? Version { get; }

    public int StudyGroups { get; }

    public int Slots { get; }

    /// <summary>Every room, by code (<c>GET /rooms</c>).</summary>
    public IReadOnlyList<RoomResponse> Rooms { get; }

    /// <summary>The newest import run's id, or null when nothing was imported. Every import adds its runs in the
    /// transaction that writes the timetable, so a new id means new data.</summary>
    public static Task<Guid?> VersionAsync(CampusPulseDbContext db, CancellationToken cancellationToken) =>
        db.TimetableImportRuns.AsNoTracking()
            .OrderByDescending(r => r.ImportedAt).ThenByDescending(r => r.Id)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Reads the whole timetable: nine queries, in one REPEATABLE READ transaction, so the data and the
    /// <see cref="Version"/> it is labelled with come from the same database snapshot even if an import commits
    /// meanwhile. Rows are ordered so that ties come out the same on every load.
    /// </summary>
    public static async Task<TimetableSnapshot> LoadAsync(CampusPulseDbContext db, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var version = await VersionAsync(db, cancellationToken);
        var semesters = await db.Semesters.AsNoTracking().AsSplitQuery()
            .Include(s => s.Weeks).Include(s => s.NonTeachingDays).Include(s => s.Breaks)
            .OrderBy(s => s.AcademicYear).ThenBy(s => s.Number)
            .ToListAsync(cancellationToken);
        var programmes = await db.Programmes.AsNoTracking().ToListAsync(cancellationToken);
        var groups = await db.StudyGroups.AsNoTracking().ToListAsync(cancellationToken);
        var rooms = await db.Rooms.AsNoTracking().ToListAsync(cancellationToken);
        var subjects = await db.Subjects.AsNoTracking().ToListAsync(cancellationToken);
        var slots = await db.TimetableSlots.AsNoTracking().Include(s => s.Groups).OrderBy(s => s.Id).ToListAsync(cancellationToken);
        var offers = await db.OfferedSlots.AsNoTracking().OrderBy(o => o.Id).ToListAsync(cancellationToken);
        var notes = await db.TimetableNotes.AsNoTracking().OrderBy(n => n.SourceCell).ThenBy(n => n.Id).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TimetableSnapshot(version, semesters, programmes, groups, rooms, subjects, slots, offers, notes);
    }

    /// <summary>The semester that contains <paramref name="today"/>, else the next one to start, else the last one;
    /// null when none has teaching weeks.</summary>
    public Semester? CurrentSemester(DateOnly today)
    {
        var semesters = _semesters.Where(s => s.WeekCount > 0).ToList();
        return semesters.FirstOrDefault(s => s.FirstDay <= today && today <= s.LastDay)
            ?? semesters.Where(s => s.FirstDay > today).MinBy(s => s.FirstDay)
            ?? semesters.MaxBy(s => s.LastDay);
    }

    /// <summary>The semester whose teaching weeks contain <paramref name="day"/>, if any.</summary>
    public Semester? SemesterOn(DateOnly day) => _semesters.FirstOrDefault(s => s.WeekCount > 0 && s.FirstDay <= day && day <= s.LastDay);

    public StudyGroup? Group(Guid id) => _groups.GetValueOrDefault(id);

    public StudyGroupResponse? GroupResponse(Guid? id) => id is { } groupId ? _groupResponses.GetValueOrDefault(groupId) : null;

    public Subject? Subject(Guid id) => _subjects.GetValueOrDefault(id);

    public bool HasOffer(Guid id) => _offers.ContainsKey(id);

    public Room? Room(string code) => _roomsByCode.GetValueOrDefault(code);

    /// <summary>The semester's offered programmes, years and groups (<c>GET /timetable/programmes</c>).</summary>
    public IReadOnlyList<ProgrammeResponse> Programmes(Guid semesterId) => _programmesBySemester.GetValueOrDefault(semesterId) ?? [];

    /// <summary>The optional and pick-a-time subjects of <paramref name="group"/>'s year, with their offers.</summary>
    public IReadOnlyList<ChoiceSubjectResponse> Choices(StudyGroup group) =>
        _choices.GetValueOrDefault((group.SemesterId, group.ProgrammeId, group.Year)) ?? [];

    /// <summary>The notes of <paramref name="group"/>'s year (sport, tutoring hours, rotas written out); none without a group.</summary>
    public IReadOnlyList<TimetableNoteResponse> Notes(Guid? groupId) =>
        groupId is { } id && _groups.GetValueOrDefault(id) is { } group ? [.. _notes[(group.SemesterId, group.ProgrammeId, group.Year)]] : [];

    /// <summary>A study group's classes from <paramref name="from"/> to <paramref name="to"/> (both included), optional
    /// subjects included (marked).</summary>
    public IReadOnlyList<OccurrenceResponse> ForGroup(Guid groupId, DateOnly from, DateOnly to) =>
        Expand(_weekByGroup.GetValueOrDefault(groupId) ?? [], from, to);

    /// <summary>A student's classes from <paramref name="from"/> to <paramref name="to"/> (both included): their group's,
    /// minus the optional subjects they didn't choose, plus the offered slots they picked. Without a group only the
    /// picks.</summary>
    public IReadOnlyList<OccurrenceResponse> ForStudent(StudentTimetableChoices student, DateOnly from, DateOnly to)
    {
        var chosen = student.Subjects.Select(c => c.SubjectId).ToHashSet();
        var grid = student.StudyGroupId is { } groupId ? _weekByGroup.GetValueOrDefault(groupId) ?? [] : [];
        var picks = student.Subjects.Select(c => c.OfferedSlotId).OfType<Guid>().Distinct()
            .Select(id => _offers.GetValueOrDefault(id)).OfType<WeeklyClass>();
        // A student's own timetable: an optional subject's classes only when they chose it.
        return Expand([.. grid.Where(c => c.Subject.Choice != SubjectChoice.Optional || chosen.Contains(c.Subject.Id)), .. picks], from, to);
    }

    /// <summary>Everything in <paramref name="room"/> on <paramref name="day"/>, grid slots and offered slots, by start
    /// time then subject; nothing outside teaching days.</summary>
    public (int? Week, IReadOnlyList<RoomOccurrenceResponse> Occurrences) RoomDay(Room room, DateOnly day)
    {
        var semester = SemesterOn(day);
        var week = semester?.WeekOf(day);
        if (semester is null || week is not { } weekNumber || !semester.HasClasses(day))
        {
            return (week, []);
        }

        return (week, [.. _classesByRoom[room.Id]
            .Where(c => c.SemesterId == semester.Id && c.Weekday == day.DayOfWeek && c.Weeks.Contains(weekNumber))
            .Select(c => c.Occurrence)
            .OrderBy(o => o.StartsAt).ThenBy(o => o.Subject, StringComparer.InvariantCulture)]);
    }

    /// <summary>In date and time order, then by subject name; grid classes before picked offers when those tie.</summary>
    private static List<OccurrenceResponse> Expand(IReadOnlyList<WeeklyClass> classes, DateOnly from, DateOnly to)
    {
        var occurrences = new List<OccurrenceResponse>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            foreach (var weekly in classes)
            {
                if (weekly.Weekday == date.DayOfWeek && OnDate(weekly.Semester, weekly.Weeks, date) is { } week)
                {
                    occurrences.Add(weekly.On(date, week));
                }
            }
        }

        return [.. occurrences.OrderBy(o => o.Date).ThenBy(o => o.StartsAt).ThenBy(o => o.Subject, StringComparer.InvariantCulture)];
    }

    /// <summary>The teaching week when a class with these weeks happens on this date, or null (no classes
    /// that day, or not one of its weeks).</summary>
    private static int? OnDate(Semester semester, WeekSet weeks, DateOnly date) =>
        semester.HasClasses(date) && semester.WeekOf(date) is { } week && weeks.Contains(week) ? week : null;

    /// <summary>One weekly class of a group, or one offered slot, resolved once per import: what each of its
    /// occurrences repeats.</summary>
    private sealed record WeeklyClass(DayOfWeek Weekday, TimeOnly StartsAt, TimeOnly EndsAt, Subject Subject, string? CampusVirtualUrl,
        Semester Semester, WeekSet Weeks, IReadOnlyList<ActivityKind> Kinds, string? Room, string? Teacher, string? Note, Guid? SlotId,
        Guid? OfferedSlotId)
    {
        public OccurrenceResponse On(DateOnly date, int week) => new(date, week, StartsAt, EndsAt, Subject.Id, Subject.Name, Subject.ShortName,
            CampusVirtualUrl, Kinds, Room, Teacher, Note, Subject.Choice == SubjectChoice.Optional, SlotId, OfferedSlotId);
    }

    /// <summary>A grid slot or an offered slot in a room, with the answer it gives on each of its dates.</summary>
    private sealed record RoomClass(Guid SemesterId, DayOfWeek Weekday, WeekSet Weeks, RoomOccurrenceResponse Occurrence);
}
