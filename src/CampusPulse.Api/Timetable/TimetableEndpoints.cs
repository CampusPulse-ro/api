using System.Security.Claims;
using CampusPulse.Api.Users;
using CampusPulse.Domain.Timetable;
using CampusPulse.Domain.Users;
using CampusPulse.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CampusPulse.Api.Timetable;

public sealed record TeachingWeekResponse(int Number, DateOnly StartsOn);

/// <summary>A calendar name in Romanian and English: a client shows the one of its UI language. <see cref="En"/> is the
/// Romanian name when the timetable gives no English one.</summary>
public sealed record CalendarNameResponse(string Ro, string En)
{
    public static CalendarNameResponse Of(CalendarName name) => new(name.Ro, name.En ?? name.Ro);
}

public sealed record NonTeachingDayResponse(DateOnly Date, CalendarNameResponse Reason);

/// <summary>A break between teaching weeks, both dates included; <see cref="Name"/> is null when the timetable names none.</summary>
public sealed record BreakResponse(DateOnly From, DateOnly To, CalendarNameResponse? Name);

/// <summary>Body of <c>GET /semesters/current</c>. <see cref="TodayWeek"/> is null outside teaching weeks.</summary>
public sealed record SemesterResponse(
    Guid Id,
    string AcademicYear,
    int Number,
    IReadOnlyList<TeachingWeekResponse> Weeks,
    IReadOnlyList<NonTeachingDayResponse> NonTeachingDays,
    IReadOnlyList<BreakResponse> Breaks,
    DateOnly Today,
    int? TodayWeek);

public sealed record StudyGroupResponse(Guid Id, string ProgrammeCode, string ProgrammeName, int Year, string Series, string Label);

public sealed record ProgrammeYearResponse(int Year, IReadOnlyList<StudyGroupResponse> Groups);

public sealed record ProgrammeResponse(string Code, string Name, IReadOnlyList<ProgrammeYearResponse> Years);

public sealed record OfferedSlotResponse(
    Guid Id,
    IReadOnlyList<ActivityKind> Kinds,
    DayOfWeek Weekday,
    TimeOnly StartsAt,
    TimeOnly EndsAt,
    string? Room,
    IReadOnlyList<int> Weeks);

/// <summary>A subject a student chooses (optional) or picks a time for (pick-slot), with its offers.
/// <see cref="CampusVirtualUrl"/>: its course page on Campus Virtual, null when it has none we know of.</summary>
public sealed record ChoiceSubjectResponse(Guid Id, string Name, string? ShortName, string? CampusVirtualUrl, SubjectChoice Choice,
    IReadOnlyList<OfferedSlotResponse> Offers);

/// <summary>
/// One class on one date. <see cref="Optional"/>: the subject is optional (in a student's own
/// timetable only when they chose it). <see cref="OfferedSlotId"/>: it's the student's pick of an
/// offered slot rather than a grid slot. <see cref="CampusVirtualUrl"/>: the subject's course page on Campus
/// Virtual, null when it has none we know of.
/// </summary>
public sealed record OccurrenceResponse(
    DateOnly Date,
    int Week,
    TimeOnly StartsAt,
    TimeOnly EndsAt,
    Guid SubjectId,
    string Subject,
    string? ShortName,
    string? CampusVirtualUrl,
    IReadOnlyList<ActivityKind> Kinds,
    string? Room,
    string? Teacher,
    string? Note,
    bool Optional,
    Guid? SlotId,
    Guid? OfferedSlotId);

public sealed record TimetableNoteResponse(string Text);

/// <summary>Body of <c>GET /me/timetable</c> and <c>GET /timetable/groups/{id}</c>: the classes from
/// <see cref="From"/> to <see cref="To"/> (dates included), in date and time order, and the notes of the
/// group's year (sport, tutoring hours, rotas written out).</summary>
public sealed record TimetableResponse(
    DateOnly From,
    DateOnly To,
    StudyGroupResponse? StudyGroup,
    IReadOnlyList<OccurrenceResponse> Occurrences,
    IReadOnlyList<TimetableNoteResponse> Notes);

/// <summary>Body of <c>GET /me/settings</c>. <see cref="NoLongerOffered"/>: the group isn't in the latest
/// import of its workbook (the student picks again). <see cref="FromAnotherSemester"/>: it belongs to a
/// semester that isn't the current one (a new semester was imported).</summary>
public sealed record StudentSettingsResponse(StudyGroupResponse? StudyGroup, bool NoLongerOffered, bool FromAnotherSemester);

/// <summary>Body of <c>PUT /me/settings</c>: the student's study group, or null to clear it.</summary>
public sealed record UpdateStudentSettingsRequest(Guid? StudyGroupId);

/// <summary>A subject the student chose. <see cref="NoLongerOffered"/>: the latest import of its workbook
/// doesn't have it any more. <see cref="CampusVirtualUrl"/>: its course page on Campus Virtual, null when it has
/// none we know of.</summary>
public sealed record StudentSubjectResponse(Guid SubjectId, string Name, string? ShortName, string? CampusVirtualUrl, SubjectChoice Choice,
    Guid? OfferedSlotId, bool NoLongerOffered);

/// <summary>Body of <c>PUT /me/subjects/{subjectId}</c>: the offered slot picked, or null for none yet.</summary>
public sealed record PickSubjectRequest(Guid? OfferedSlotId);

public sealed record RoomResponse(string Code, string? Building, string? Floor, int? Capacity);

/// <summary>A class or offered slot in a room. <see cref="CampusVirtualUrl"/>: the subject's course page on
/// Campus Virtual, null when it has none we know of.</summary>
public sealed record RoomOccurrenceResponse(
    TimeOnly StartsAt,
    TimeOnly EndsAt,
    string Subject,
    string? CampusVirtualUrl,
    IReadOnlyList<ActivityKind> Kinds,
    IReadOnlyList<string> Groups,
    bool Offered);

public sealed record RoomTimetableResponse(DateOnly Date, int? Week, RoomResponse Room, IReadOnlyList<RoomOccurrenceResponse> Occurrences);

/// <summary>
/// The timetable (CampusPulse-ro/api#5). Everything needs sign-in: the faculty shares these files with
/// UPT accounts only. Dates are Romanian dates, and the API expands the weekly slots into dates itself
/// (teaching weeks, breaks, non-teaching days, each slot's weeks), so no client handles parity.
/// </summary>
public static class TimetableEndpoints
{
    public const int MaxRangeDays = 31;

    /// <summary>Found when the routes are mapped, at startup: without the time-zone database the API
    /// would date every class in UTC, so it doesn't start.</summary>
    private static readonly TimeZoneInfo Romania = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");

    public static IEndpointRouteBuilder MapTimetable(this IEndpointRouteBuilder app)
    {
        var timetable = app.MapGroup("").RequireAuthorization().WithTags("Timetable");

        timetable.MapGet("/semesters/current", GetCurrentSemester)
            .WithName("GetCurrentSemester")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        timetable.MapGet("/timetable/programmes", GetProgrammes)
            .WithName("GetProgrammes")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        timetable.MapGet("/timetable/groups/{id:guid}", GetGroupTimetable)
            .WithName("GetGroupTimetable")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        timetable.MapGet("/timetable/groups/{id:guid}/choices", GetGroupChoices)
            .WithName("GetGroupChoices")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        timetable.MapGet("/me/settings", GetSettings)
            .WithName("GetStudentSettings")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        timetable.MapPut("/me/settings", UpdateSettings)
            .WithName("UpdateStudentSettings")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        timetable.MapGet("/me/subjects", GetSubjects)
            .WithName("GetStudentSubjects")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        timetable.MapPut("/me/subjects/{subjectId:guid}", PickSubject)
            .WithName("PickStudentSubject")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        timetable.MapDelete("/me/subjects/{subjectId:guid}", DropSubject)
            .WithName("DropStudentSubject")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        timetable.MapGet("/me/timetable", GetMyTimetable)
            .WithName("GetMyTimetable")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        timetable.MapGet("/rooms", GetRooms)
            .WithName("GetRooms")
            .Produces(StatusCodes.Status401Unauthorized);

        timetable.MapGet("/rooms/{code}/timetable", GetRoomTimetable)
            .WithName("GetRoomTimetable")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }

    /// <summary>The semester that contains today, else the next one to start, else the last one.</summary>
    internal static async Task<Results<Ok<SemesterResponse>, ProblemHttpResult>> GetCurrentSemester(CampusPulseDbContext db,
        TimeProvider time, CancellationToken cancellationToken)
    {
        var today = Today(time);
        var semester = await CurrentSemesterAsync(db, today, cancellationToken);
        if (semester is null)
        {
            return NoTimetable();
        }

        return TypedResults.Ok(new SemesterResponse(semester.Id, semester.AcademicYear, semester.Number,
            [.. semester.Weeks.OrderBy(w => w.Number).Select(w => new TeachingWeekResponse(w.Number, w.StartsOn))],
            [.. semester.NonTeachingDays.OrderBy(d => d.Date).Select(d => new NonTeachingDayResponse(d.Date, CalendarNameResponse.Of(d.Reason)))],
            [.. semester.Breaks.OrderBy(b => b.From).Select(b => new BreakResponse(b.From, b.To, b.Name is { } name ? CalendarNameResponse.Of(name) : null))],
            today, semester.WeekOf(today)));
    }

    /// <summary>The current semester's programmes, their years and study groups: what a student picks
    /// their group from.</summary>
    internal static async Task<Results<Ok<IReadOnlyList<ProgrammeResponse>>, ProblemHttpResult>> GetProgrammes(CampusPulseDbContext db,
        TimeProvider time, CancellationToken cancellationToken)
    {
        var semester = await CurrentSemesterAsync(db, Today(time), cancellationToken);
        if (semester is null)
        {
            return NoTimetable();
        }

        var groups = await db.StudyGroups.Where(g => g.SemesterId == semester.Id && g.RetiredAt == null).ToListAsync(cancellationToken);
        var programmeIds = groups.Select(g => g.ProgrammeId).Distinct().ToList();
        var programmes = await db.Programmes.Where(p => programmeIds.Contains(p.Id)).ToListAsync(cancellationToken);
        IReadOnlyList<ProgrammeResponse> body = [.. programmes.OrderBy(p => p.Code, StringComparer.Ordinal).Select(p => new ProgrammeResponse(
            p.Code, p.Name,
            [.. groups.Where(g => g.ProgrammeId == p.Id).GroupBy(g => g.Year).OrderBy(y => y.Key).Select(y => new ProgrammeYearResponse(y.Key,
                [.. y.OrderBy(g => g.Series, StringComparer.Ordinal).ThenBy(g => g.Group).ThenBy(g => g.Subgroup).Select(g => GroupResponse(g, p))]))]))];
        return TypedResults.Ok(body);
    }

    /// <summary>A study group's classes, optional subjects included (marked).</summary>
    internal static async Task<Results<Ok<TimetableResponse>, ProblemHttpResult>> GetGroupTimetable(Guid id, DateOnly? from,
        DateOnly? to, CampusPulseDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (Range(from, to, time) is not { } range)
        {
            return BadRange();
        }

        var (first, last) = range;

        var group = await db.StudyGroups.SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (group is null)
        {
            return NoGroup();
        }

        return TypedResults.Ok(await TimetableAsync(db, group, first, last, choices: null, cancellationToken));
    }

    /// <summary>The optional and pick-a-time subjects of a group's year, with every slot they're offered at.</summary>
    internal static async Task<Results<Ok<IReadOnlyList<ChoiceSubjectResponse>>, ProblemHttpResult>> GetGroupChoices(Guid id,
        CampusPulseDbContext db, CancellationToken cancellationToken)
    {
        var group = await db.StudyGroups.SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (group is null)
        {
            return NoGroup();
        }

        var subjects = await db.Subjects
            .Where(s => s.SemesterId == group.SemesterId && s.ProgrammeId == group.ProgrammeId && s.Year == group.Year
                && s.Choice != SubjectChoice.Fixed && s.RetiredAt == null)
            .ToListAsync(cancellationToken);
        var subjectIds = subjects.Select(s => s.Id).ToList();
        var offers = await db.OfferedSlots.Where(o => subjectIds.Contains(o.SubjectId)).ToListAsync(cancellationToken);
        var rooms = await RoomCodesAsync(db, offers.Select(o => o.RoomId), cancellationToken);
        IReadOnlyList<ChoiceSubjectResponse> body = [.. subjects.OrderBy(s => s.Name, StringComparer.InvariantCulture).Select(s => new ChoiceSubjectResponse(
            s.Id, s.Name, s.ShortName, CampusVirtual.CourseUrl(s.CampusVirtualCourseId), s.Choice,
            [.. offers.Where(o => o.SubjectId == s.Id).OrderBy(o => o.Weekday).ThenBy(o => o.StartsAt)
                .Select(o => new OfferedSlotResponse(o.Id, o.Kinds.List(), o.Weekday, o.StartsAt, o.EndsAt, Code(rooms, o.RoomId), o.Weeks.Weeks()))]))];
        return TypedResults.Ok(body);
    }

    internal static async Task<Results<Ok<StudentSettingsResponse>, ProblemHttpResult>> GetSettings(ClaimsPrincipal principal,
        CampusPulseDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (await UserAsync(db, principal, cancellationToken) is not { } user)
        {
            return MeEndpoint.NoAccount();
        }

        var settings = await db.StudentSettings.SingleOrDefaultAsync(s => s.UserId == user.Id, cancellationToken);
        return TypedResults.Ok(await SettingsResponseAsync(db, settings?.StudyGroupId, time, cancellationToken));
    }

    private static async Task<StudentSettingsResponse> SettingsResponseAsync(CampusPulseDbContext db, Guid? groupId, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var group = groupId is null ? null : await db.StudyGroups.SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (group is null)
        {
            return new StudentSettingsResponse(null, false, false);
        }

        var current = await CurrentSemesterAsync(db, Today(time), cancellationToken);
        var programme = await db.Programmes.SingleAsync(p => p.Id == group.ProgrammeId, cancellationToken);
        return new StudentSettingsResponse(GroupResponse(group, programme), group.RetiredAt is not null,
            current is not null && current.Id != group.SemesterId);
    }

    /// <summary>Sets the student's study group (null clears it). 400 for a group that doesn't exist.</summary>
    internal static async Task<Results<Ok<StudentSettingsResponse>, ProblemHttpResult>> UpdateSettings(
        UpdateStudentSettingsRequest request, ClaimsPrincipal principal, CampusPulseDbContext db, TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (await UserAsync(db, principal, cancellationToken) is not { } user)
        {
            return MeEndpoint.NoAccount();
        }

        if (request.StudyGroupId is { } groupId && !await db.StudyGroups.AnyAsync(g => g.Id == groupId && g.RetiredAt == null, cancellationToken))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "No such study group",
                detail: "studyGroupId must be one of GET /timetable/programmes (a group no longer offered can't be picked).");
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var settings = await db.StudentSettings.SingleOrDefaultAsync(s => s.UserId == user.Id, cancellationToken);
                if (settings is null)
                {
                    settings = new StudentSettings(user.Id, time.GetUtcNow());
                    db.StudentSettings.Add(settings);
                }

                settings.SetStudyGroup(request.StudyGroupId, time.GetUtcNow());
                await db.SaveChangesAsync(cancellationToken);
                return TypedResults.Ok(await SettingsResponseAsync(db, request.StudyGroupId, time, cancellationToken));
            }
            catch (DbUpdateException e) when (attempt == 1 && IsRace(e))
            {
                // Another request created the row first, or the account was deleted meanwhile: read again.
                db.ChangeTracker.Clear();
                if (await UserAsync(db, principal, cancellationToken) is null)
                {
                    return MeEndpoint.NoAccount();
                }
            }
        }
    }

    internal static async Task<Results<Ok<IReadOnlyList<StudentSubjectResponse>>, ProblemHttpResult>> GetSubjects(
        ClaimsPrincipal principal, CampusPulseDbContext db, CancellationToken cancellationToken)
    {
        if (await UserAsync(db, principal, cancellationToken) is not { } user)
        {
            return MeEndpoint.NoAccount();
        }

        var picks = await db.StudentSubjects.Where(s => s.UserId == user.Id).ToListAsync(cancellationToken);
        var subjectIds = picks.Select(p => p.SubjectId).ToList();
        var subjects = await db.Subjects.Where(s => subjectIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);
        IReadOnlyList<StudentSubjectResponse> body = [.. picks.Where(p => subjects.ContainsKey(p.SubjectId))
            .Select(p => (Pick: p, Subject: subjects[p.SubjectId]))
            .OrderBy(x => x.Subject.Name, StringComparer.InvariantCulture)
            .Select(x => new StudentSubjectResponse(x.Subject.Id, x.Subject.Name, x.Subject.ShortName,
                CampusVirtual.CourseUrl(x.Subject.CampusVirtualCourseId), x.Subject.Choice, x.Pick.OfferedSlotId, x.Subject.RetiredAt is not null))];
        return TypedResults.Ok(body);
    }

    /// <summary>
    /// Takes an optional subject, or picks a time for a pick-a-time one: <c>offeredSlotId</c> is one of its
    /// offered slots (GET /timetable/groups/{id}/choices), or null for "taking it, no time picked yet".
    /// 404 for an unknown subject; 400 for a subject with nothing to choose or an offer of another subject.
    /// </summary>
    internal static async Task<Results<Ok<StudentSubjectResponse>, ProblemHttpResult>> PickSubject(Guid subjectId,
        PickSubjectRequest request, ClaimsPrincipal principal, CampusPulseDbContext db, TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (await UserAsync(db, principal, cancellationToken) is not { } user)
        {
            return MeEndpoint.NoAccount();
        }

        var subject = await db.Subjects.SingleOrDefaultAsync(s => s.Id == subjectId, cancellationToken);
        if (subject is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such subject");
        }

        if (subject.RetiredAt is not null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "No longer offered",
                detail: "The latest timetable doesn't have this subject any more.");
        }

        if (subject.Choice == SubjectChoice.Fixed)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Nothing to choose",
                detail: "Everyone in its groups has this subject at the times on the timetable.");
        }

        if (request.OfferedSlotId is { } offerId && !await db.OfferedSlots.AnyAsync(o => o.Id == offerId && o.SubjectId == subjectId, cancellationToken))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not an offer of this subject",
                detail: "offeredSlotId must be one of this subject's offers (GET /timetable/groups/{id}/choices).");
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var pick = await db.StudentSubjects.SingleOrDefaultAsync(s => s.UserId == user.Id && s.SubjectId == subjectId, cancellationToken);
                if (pick is null)
                {
                    pick = new StudentSubject(user.Id, subjectId, time.GetUtcNow());
                    db.StudentSubjects.Add(pick);
                }

                pick.Pick(request.OfferedSlotId, time.GetUtcNow());
                await db.SaveChangesAsync(cancellationToken);
                return TypedResults.Ok(new StudentSubjectResponse(subject.Id, subject.Name, subject.ShortName,
                    CampusVirtual.CourseUrl(subject.CampusVirtualCourseId), subject.Choice, pick.OfferedSlotId, NoLongerOffered: false));
            }
            catch (DbUpdateException e) when (attempt == 1 && IsRace(e))
            {
                db.ChangeTracker.Clear();
                if (await UserAsync(db, principal, cancellationToken) is null)
                {
                    return MeEndpoint.NoAccount();
                }
            }
        }
    }

    /// <summary>Drops an optional subject (or a pick): 204, also when there was nothing to drop.</summary>
    internal static async Task<Results<NoContent, ProblemHttpResult>> DropSubject(Guid subjectId, ClaimsPrincipal principal,
        CampusPulseDbContext db, CancellationToken cancellationToken)
    {
        if (await UserAsync(db, principal, cancellationToken) is not { } user)
        {
            return MeEndpoint.NoAccount();
        }

        await db.StudentSubjects.Where(s => s.UserId == user.Id && s.SubjectId == subjectId).ExecuteDeleteAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// The signed-in student's classes: their group's, minus optional subjects they didn't choose, plus the
    /// offered slots they picked. Without a group (none picked yet, or it disappeared in a re-import) only
    /// the picks.
    /// </summary>
    internal static async Task<Results<Ok<TimetableResponse>, ProblemHttpResult>> GetMyTimetable(DateOnly? from, DateOnly? to,
        ClaimsPrincipal principal, CampusPulseDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (Range(from, to, time) is not { } range)
        {
            return BadRange();
        }

        var (first, last) = range;

        if (await UserAsync(db, principal, cancellationToken) is not { } user)
        {
            return MeEndpoint.NoAccount();
        }

        var settings = await db.StudentSettings.SingleOrDefaultAsync(s => s.UserId == user.Id, cancellationToken);
        var group = settings?.StudyGroupId is { } groupId
            ? await db.StudyGroups.SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken)
            : null;
        var picks = await db.StudentSubjects.Where(s => s.UserId == user.Id).ToListAsync(cancellationToken);
        return TypedResults.Ok(await TimetableAsync(db, group, first, last, picks, cancellationToken));
    }

    internal static async Task<Ok<IReadOnlyList<RoomResponse>>> GetRooms(CampusPulseDbContext db, CancellationToken cancellationToken)
    {
        var rooms = await db.Rooms.ToListAsync(cancellationToken);
        IReadOnlyList<RoomResponse> body = [.. rooms.OrderBy(r => r.Code, StringComparer.Ordinal).Select(RoomResponseOf)];
        return TypedResults.Ok(body);
    }

    /// <summary>Everything in a room on a date (default today): grid slots and offered slots.</summary>
    internal static async Task<Results<Ok<RoomTimetableResponse>, ProblemHttpResult>> GetRoomTimetable(string code, DateOnly? date,
        CampusPulseDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var day = date ?? Today(time);
        if (day < Earliest || day > Latest)
        {
            return BadRange();
        }

        var normalized = Import.CellText.NormalizeRoom(code);
        var room = await db.Rooms.SingleOrDefaultAsync(r => r.Code == normalized, cancellationToken);
        if (room is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such room");
        }

        var semester = await SemesterOnAsync(db, day, cancellationToken);
        var week = semester?.WeekOf(day);
        var occurrences = new List<RoomOccurrenceResponse>();
        if (semester is not null && week is { } weekNumber && semester.HasClasses(day))
        {
            var weekday = day.DayOfWeek;
            var slots = await db.TimetableSlots.Include(s => s.Groups)
                .Where(s => s.SemesterId == semester.Id && s.RoomId == room.Id && s.Weekday == weekday)
                .ToListAsync(cancellationToken);
            var offers = await db.OfferedSlots
                .Where(o => o.RoomId == room.Id && o.Weekday == weekday && db.Subjects.Any(s => s.Id == o.SubjectId && s.SemesterId == semester.Id))
                .ToListAsync(cancellationToken);
            var subjectIds = slots.Select(s => s.SubjectId).Concat(offers.Select(o => o.SubjectId)).Distinct().ToList();
            var subjects = await db.Subjects.Where(s => subjectIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);
            var groupIds = slots.SelectMany(s => s.Groups.Select(g => g.StudyGroupId)).Distinct().ToList();
            var groups = await db.StudyGroups.Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, cancellationToken);
            var programmeIds = groups.Values.Select(g => g.ProgrammeId).Distinct().ToList();
            var programmes = await db.Programmes.Where(p => programmeIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
            occurrences.AddRange(slots.Where(s => s.Weeks.Contains(weekNumber)).Select(s => new RoomOccurrenceResponse(s.StartsAt, s.EndsAt,
                subjects[s.SubjectId].Name, CampusVirtual.CourseUrl(subjects[s.SubjectId].CampusVirtualCourseId), s.Kinds.List(),
                [.. s.Groups.Select(g => groups[g.StudyGroupId]).Select(g => $"{programmes[g.ProgrammeId].Code} {g.Year} {g.Label}").Order(StringComparer.Ordinal)],
                Offered: false)));
            occurrences.AddRange(offers.Where(o => o.Weeks.Contains(weekNumber)).Select(o => new RoomOccurrenceResponse(o.StartsAt, o.EndsAt,
                subjects[o.SubjectId].Name, CampusVirtual.CourseUrl(subjects[o.SubjectId].CampusVirtualCourseId), o.Kinds.List(), [], Offered: true)));
        }

        return TypedResults.Ok(new RoomTimetableResponse(day, week, RoomResponseOf(room),
            [.. occurrences.OrderBy(o => o.StartsAt).ThenBy(o => o.Subject, StringComparer.InvariantCulture)]));
    }

    private static async Task<TimetableResponse> TimetableAsync(CampusPulseDbContext db, StudyGroup? group, DateOnly from, DateOnly to,
        IReadOnlyList<StudentSubject>? choices, CancellationToken cancellationToken)
    {
        var slots = group is null
            ? []
            : await db.TimetableSlots.Where(s => s.Groups.Any(g => g.StudyGroupId == group.Id)).ToListAsync(cancellationToken);
        var pickedOfferIds = choices?.Select(c => c.OfferedSlotId).OfType<Guid>().ToList() ?? [];
        var offers = pickedOfferIds.Count == 0 ? [] : await db.OfferedSlots.Where(o => pickedOfferIds.Contains(o.Id)).ToListAsync(cancellationToken);
        var subjectIds = slots.Select(s => s.SubjectId).Concat(offers.Select(o => o.SubjectId)).Distinct().ToList();
        var subjects = await db.Subjects.Where(s => subjectIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);
        if (choices is not null)
        {
            // A student's own timetable: an optional subject's classes only when they chose it.
            var chosen = choices.Select(c => c.SubjectId).ToHashSet();
            slots = [.. slots.Where(s => subjects[s.SubjectId].Choice != SubjectChoice.Optional || chosen.Contains(s.SubjectId))];
        }

        var rooms = await RoomCodesAsync(db, slots.Select(s => s.RoomId).Concat(offers.Select(o => o.RoomId)), cancellationToken);
        var semesterIds = subjects.Values.Select(s => s.SemesterId).Concat(group is null ? [] : [group.SemesterId]).Distinct().ToList();
        var semesters = await db.Semesters.Include(s => s.Weeks).Include(s => s.NonTeachingDays)
            .Where(s => semesterIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);

        var occurrences = new List<OccurrenceResponse>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            foreach (var slot in slots.Where(s => s.Weekday == date.DayOfWeek))
            {
                var subject = subjects[slot.SubjectId];
                if (OnDate(semesters[subject.SemesterId], slot.Weeks, date) is { } week)
                {
                    occurrences.Add(new OccurrenceResponse(date, week, slot.StartsAt, slot.EndsAt, subject.Id, subject.Name, subject.ShortName,
                        CampusVirtual.CourseUrl(subject.CampusVirtualCourseId), slot.Kinds.List(), Code(rooms, slot.RoomId), slot.Teacher, slot.Note, subject.Choice == SubjectChoice.Optional,
                        slot.Id, null));
                }
            }

            foreach (var offer in offers.Where(o => o.Weekday == date.DayOfWeek))
            {
                var subject = subjects[offer.SubjectId];
                if (OnDate(semesters[subject.SemesterId], offer.Weeks, date) is { } week)
                {
                    occurrences.Add(new OccurrenceResponse(date, week, offer.StartsAt, offer.EndsAt, subject.Id, subject.Name, subject.ShortName,
                        CampusVirtual.CourseUrl(subject.CampusVirtualCourseId), offer.Kinds.List(), Code(rooms, offer.RoomId), null, null, subject.Choice == SubjectChoice.Optional, null, offer.Id));
                }
            }
        }

        var notes = group is null
            ? []
            : await db.TimetableNotes.Where(n => n.SemesterId == group.SemesterId && n.ProgrammeId == group.ProgrammeId && n.Year == group.Year)
                .OrderBy(n => n.SourceCell).Select(n => new TimetableNoteResponse(n.Text)).ToListAsync(cancellationToken);
        return new TimetableResponse(from, to, await GroupResponseAsync(db, group?.Id, cancellationToken),
            [.. occurrences.OrderBy(o => o.Date).ThenBy(o => o.StartsAt).ThenBy(o => o.Subject, StringComparer.InvariantCulture)], notes);
    }

    /// <summary>The teaching week when a class with these weeks happens on this date, or null (no classes
    /// that day, or not one of its weeks).</summary>
    private static int? OnDate(Semester semester, WeekSet weeks, DateOnly date) =>
        semester.HasClasses(date) && semester.WeekOf(date) is { } week && weeks.Contains(week) ? week : null;

    private static async Task<Semester?> CurrentSemesterAsync(CampusPulseDbContext db, DateOnly today, CancellationToken cancellationToken)
    {
        var semesters = (await db.Semesters.Include(s => s.Weeks).Include(s => s.NonTeachingDays).Include(s => s.Breaks)
            .ToListAsync(cancellationToken))
            .Where(s => s.WeekCount > 0)
            .ToList();
        return semesters.FirstOrDefault(s => s.FirstDay <= today && today <= s.LastDay)
            ?? semesters.Where(s => s.FirstDay > today).MinBy(s => s.FirstDay)
            ?? semesters.MaxBy(s => s.LastDay);
    }

    private static async Task<Semester?> SemesterOnAsync(CampusPulseDbContext db, DateOnly day, CancellationToken cancellationToken) =>
        (await db.Semesters.Include(s => s.Weeks).Include(s => s.NonTeachingDays).ToListAsync(cancellationToken))
            .FirstOrDefault(s => s.WeekCount > 0 && s.FirstDay <= day && day <= s.LastDay);

    private static async Task<StudyGroupResponse?> GroupResponseAsync(CampusPulseDbContext db, Guid? groupId, CancellationToken cancellationToken)
    {
        if (groupId is null || await db.StudyGroups.SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken) is not { } group)
        {
            return null;
        }

        var programme = await db.Programmes.SingleAsync(p => p.Id == group.ProgrammeId, cancellationToken);
        return GroupResponse(group, programme);
    }

    private static StudyGroupResponse GroupResponse(StudyGroup group, Programme programme) =>
        new(group.Id, programme.Code, programme.Name, group.Year, group.Series, group.Label);

    private static async Task<Dictionary<Guid, string>> RoomCodesAsync(CampusPulseDbContext db, IEnumerable<Guid?> roomIds,
        CancellationToken cancellationToken)
    {
        var ids = roomIds.OfType<Guid>().Distinct().ToList();
        return ids.Count == 0 ? [] : await db.Rooms.Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Code, cancellationToken);
    }

    private static string? Code(Dictionary<Guid, string> rooms, Guid? roomId) => roomId is { } id ? rooms.GetValueOrDefault(id) : null;

    private static RoomResponse RoomResponseOf(Room room) => new(room.Code, room.Building, room.Floor, room.Capacity);

    private static Task<User?> UserAsync(CampusPulseDbContext db, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var oid = MeEndpoint.Identity.From(principal).Oid;
        return db.Users.SingleOrDefaultAsync(u => u.EntraObjectId == oid, cancellationToken);
    }

    /// <summary>A unique-index collision (two first writes at once) or a missing row (the account was
    /// deleted between the read and the write).</summary>
    private static bool IsRace(DbUpdateException e) =>
        e is DbUpdateConcurrencyException
        || e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation };

    private static readonly DateOnly Earliest = new(2000, 1, 1);
    private static readonly DateOnly Latest = new(2100, 12, 31);

    /// <summary>from..to, both defaulting to today (Romania), at most <see cref="MaxRangeDays"/> days, within
    /// 2000-2100.</summary>
    private static (DateOnly From, DateOnly To)? Range(DateOnly? from, DateOnly? to, TimeProvider time)
    {
        var first = from ?? to ?? Today(time);
        var last = to ?? first;
        return first >= Earliest && last <= Latest && last >= first && last.DayNumber - first.DayNumber < MaxRangeDays
            ? (first, last)
            : null;
    }

    internal static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), Romania).DateTime);

    private static ProblemHttpResult BadRange() => TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Bad date range",
        detail: $"from must not be after to, the range is at most {MaxRangeDays} days, and both are in 2000-2100 (dates as yyyy-MM-dd).");

    private static ProblemHttpResult NoGroup() => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such study group");

    private static ProblemHttpResult NoTimetable() => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound,
        title: "No timetable", detail: "No semester has been imported yet.");
}
