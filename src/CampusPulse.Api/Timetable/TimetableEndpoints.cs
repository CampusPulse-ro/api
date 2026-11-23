using System.Security.Claims;
using CampusPulse.Api.Http;
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

public sealed record StudyGroupResponse(Guid Id, string ProgrammeCode, string ProgrammeName, int Year, string Series, string Label)
{
    public static StudyGroupResponse Of(StudyGroup group, Programme programme) =>
        new(group.Id, programme.Code, programme.Name, group.Year, group.Series, group.Label);
}

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

public sealed record RoomResponse(string Code, string? Building, string? Floor, int? Capacity)
{
    public static RoomResponse Of(Room room) => new(room.Code, room.Building, room.Floor, room.Capacity);
}

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

    /// <summary>The timetable cache (<see cref="TimetableCache"/>) and its <c>TimetableCache</c> options.</summary>
    public static IServiceCollection AddCampusPulseTimetable(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TimetableCacheOptions>(configuration.GetSection(TimetableCacheOptions.Section));
        services.AddSingleton<TimetableCache>();
        return services;
    }

    public static IEndpointRouteBuilder MapTimetable(this IEndpointRouteBuilder app)
    {
        RomanianTime.EnsureAvailable();
        var timetable = app.MapGroup("").RequireAuthorization().WithTags("Timetable");

        timetable.MapGet("/semesters/current", GetCurrentSemester)
            .WithName("GetCurrentSemester")
            .WithHttpCaching(HttpCacheScope.SharedToday)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        timetable.MapGet("/timetable/programmes", GetProgrammes)
            .WithName("GetProgrammes")
            .WithHttpCaching(HttpCacheScope.SharedToday)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        timetable.MapGet("/timetable/groups/{id:guid}", GetGroupTimetable)
            .WithName("GetGroupTimetable")
            .WithHttpCaching(request => request.Query.ContainsKey("from") || request.Query.ContainsKey("to") ? HttpCacheScope.Shared : HttpCacheScope.SharedToday)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        timetable.MapGet("/timetable/groups/{id:guid}/choices", GetGroupChoices)
            .WithName("GetGroupChoices")
            .WithHttpCaching(HttpCacheScope.Shared)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        timetable.MapGet("/me/settings", GetSettings)
            .WithName("GetStudentSettings")
            .WithHttpCaching(HttpCacheScope.PerStudent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        timetable.MapPut("/me/settings", UpdateSettings)
            .WithName("UpdateStudentSettings")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        timetable.MapGet("/me/subjects", GetSubjects)
            .WithName("GetStudentSubjects")
            .WithHttpCaching(HttpCacheScope.PerStudent)
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
            .WithHttpCaching(HttpCacheScope.PerStudent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        timetable.MapGet("/rooms", GetRooms)
            .WithName("GetRooms")
            .WithHttpCaching(HttpCacheScope.Shared)
            .Produces(StatusCodes.Status401Unauthorized);

        timetable.MapGet("/rooms/{code}/timetable", GetRoomTimetable)
            .WithName("GetRoomTimetable")
            .WithHttpCaching(request => request.Query.ContainsKey("date") ? HttpCacheScope.Shared : HttpCacheScope.SharedToday)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }

    /// <summary>The semester that contains today, else the next one to start, else the last one.</summary>
    internal static async Task<Results<Ok<SemesterResponse>, ProblemHttpResult>> GetCurrentSemester(TimetableCache cache,
        TimeProvider time, CancellationToken cancellationToken)
    {
        var today = Today(time);
        var timetable = await cache.GetAsync(t => t.CurrentSemester(today) is not null, idsFromDatabase: false, cancellationToken);
        if (timetable.CurrentSemester(today) is not { } semester)
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
    internal static async Task<Results<Ok<IReadOnlyList<ProgrammeResponse>>, ProblemHttpResult>> GetProgrammes(TimetableCache cache,
        TimeProvider time, CancellationToken cancellationToken)
    {
        var today = Today(time);
        var timetable = await cache.GetAsync(t => t.CurrentSemester(today) is not null, idsFromDatabase: false, cancellationToken);
        return timetable.CurrentSemester(today) is { } semester ? TypedResults.Ok(timetable.Programmes(semester.Id)) : NoTimetable();
    }

    /// <summary>A study group's classes, optional subjects included (marked).</summary>
    internal static async Task<Results<Ok<TimetableResponse>, ProblemHttpResult>> GetGroupTimetable(Guid id, DateOnly? from,
        DateOnly? to, TimetableCache cache, TimeProvider time, CancellationToken cancellationToken)
    {
        if (Range(from, to, time) is not { } range)
        {
            return BadRange();
        }

        var (first, last) = range;
        var timetable = await cache.GetAsync(t => t.Group(id) is not null, idsFromDatabase: false, cancellationToken);
        if (timetable.Group(id) is null)
        {
            return NoGroup();
        }

        return TypedResults.Ok(TimetableResponseOf(timetable, id, first, last, timetable.ForGroup(id, first, last)));
    }

    /// <summary>The optional and pick-a-time subjects of a group's year, with every slot they're offered at.</summary>
    internal static async Task<Results<Ok<IReadOnlyList<ChoiceSubjectResponse>>, ProblemHttpResult>> GetGroupChoices(Guid id,
        TimetableCache cache, CancellationToken cancellationToken)
    {
        var timetable = await cache.GetAsync(t => t.Group(id) is not null, idsFromDatabase: false, cancellationToken);
        return timetable.Group(id) is { } group ? TypedResults.Ok(timetable.Choices(group)) : NoGroup();
    }

    internal static async Task<Results<Ok<StudentSettingsResponse>, ProblemHttpResult>> GetSettings(ClaimsPrincipal principal,
        CampusPulseDbContext db, TimetableCache cache, TimeProvider time, CancellationToken cancellationToken)
    {
        if (await StudyGroupOfAsync(db, principal, cancellationToken) is not { } student)
        {
            return MeEndpoint.NoAccount();
        }

        return TypedResults.Ok(await SettingsResponseAsync(cache, student.StudyGroupId, time, cancellationToken));
    }

    /// <summary>The settings body for <paramref name="groupId"/>, which comes from the database (the student's row, or a
    /// group just checked there), so the cache checks at once if it doesn't have it yet.</summary>
    private static async Task<StudentSettingsResponse> SettingsResponseAsync(TimetableCache cache, Guid? groupId, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var timetable = await cache.GetAsync(t => groupId is null || t.Group(groupId.Value) is not null, idsFromDatabase: true, cancellationToken);
        if (groupId is null || timetable.Group(groupId.Value) is not { } group)
        {
            return new StudentSettingsResponse(null, false, false);
        }

        var current = timetable.CurrentSemester(Today(time));
        return new StudentSettingsResponse(timetable.GroupResponse(group.Id), group.RetiredAt is not null,
            current is not null && current.Id != group.SemesterId);
    }

    /// <summary>Sets the student's study group (null clears it). 400 for a group that doesn't exist.</summary>
    internal static async Task<Results<Ok<StudentSettingsResponse>, ProblemHttpResult>> UpdateSettings(
        UpdateStudentSettingsRequest request, ClaimsPrincipal principal, CampusPulseDbContext db, TimetableCache cache, TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (await UserAsync(db, principal, cancellationToken) is not { } user)
        {
            return MeEndpoint.NoAccount();
        }

        // Checked in the database, not the cache: a write goes by what is stored now.
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
                return TypedResults.Ok(await SettingsResponseAsync(cache, request.StudyGroupId, time, cancellationToken));
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
        ClaimsPrincipal principal, CampusPulseDbContext db, TimetableCache cache, CancellationToken cancellationToken)
    {
        if (await StudentTimetableChoices.ForOidAsync(db, Oid(principal), cancellationToken) is not { } student)
        {
            return MeEndpoint.NoAccount();
        }

        var timetable = await cache.GetAsync(t => student.Subjects.All(p => t.Subject(p.SubjectId) is not null), idsFromDatabase: true, cancellationToken);
        IReadOnlyList<StudentSubjectResponse> body = [.. student.Subjects
            .Select(p => (Pick: p, Subject: timetable.Subject(p.SubjectId)))
            .Where(x => x.Subject is not null)
            .OrderBy(x => x.Subject!.Name, StringComparer.InvariantCulture)
            .Select(x => new StudentSubjectResponse(x.Subject!.Id, x.Subject.Name, x.Subject.ShortName,
                CampusVirtual.CourseUrl(x.Subject.CampusVirtualCourseId), x.Subject.Choice, x.Pick.OfferedSlotId, x.Subject.RetiredAt is not null))];
        return TypedResults.Ok(body);
    }

    /// <summary>
    /// Takes an optional subject, or picks a time for a pick-a-time one: <c>offeredSlotId</c> is one of its
    /// offered slots (GET /timetable/groups/{id}/choices), or null for "taking it, no time picked yet".
    /// 404 for an unknown subject; 400 for a subject with nothing to choose or an offer of another subject.
    /// Checked in the database, not the cache: a write goes by what is stored now.
    /// </summary>
    internal static async Task<Results<Ok<StudentSubjectResponse>, ProblemHttpResult>> PickSubject(Guid subjectId,
        PickSubjectRequest request, ClaimsPrincipal principal, CampusPulseDbContext db, TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (await UserAsync(db, principal, cancellationToken) is not { } user)
        {
            return MeEndpoint.NoAccount();
        }

        var subject = await db.Subjects.AsNoTracking().SingleOrDefaultAsync(s => s.Id == subjectId, cancellationToken);
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
    /// the picks. One query (the student's own choices); the rest is the cached timetable.
    /// </summary>
    internal static async Task<Results<Ok<TimetableResponse>, ProblemHttpResult>> GetMyTimetable(DateOnly? from, DateOnly? to,
        ClaimsPrincipal principal, CampusPulseDbContext db, TimetableCache cache, TimeProvider time, CancellationToken cancellationToken)
    {
        if (Range(from, to, time) is not { } range)
        {
            return BadRange();
        }

        var (first, last) = range;

        if (await StudentTimetableChoices.ForOidAsync(db, Oid(principal), cancellationToken) is not { } student)
        {
            return MeEndpoint.NoAccount();
        }

        var timetable = await cache.GetAsync(t => (student.StudyGroupId is not { } groupId || t.Group(groupId) is not null)
            && student.Subjects.All(s => s.OfferedSlotId is not { } offerId || t.HasOffer(offerId)), idsFromDatabase: true, cancellationToken);
        return TypedResults.Ok(TimetableResponseOf(timetable, student.StudyGroupId, first, last, timetable.ForStudent(student, first, last)));
    }

    internal static async Task<Ok<IReadOnlyList<RoomResponse>>> GetRooms(TimetableCache cache, CancellationToken cancellationToken) =>
        TypedResults.Ok((await cache.GetAsync(cancellationToken)).Rooms);

    /// <summary>Everything in a room on a date (default today): grid slots and offered slots.</summary>
    internal static async Task<Results<Ok<RoomTimetableResponse>, ProblemHttpResult>> GetRoomTimetable(string code, DateOnly? date,
        TimetableCache cache, TimeProvider time, CancellationToken cancellationToken)
    {
        var day = date ?? Today(time);
        if (day < Earliest || day > Latest)
        {
            return BadRange();
        }

        var normalized = Import.CellText.NormalizeRoom(code);
        var timetable = await cache.GetAsync(t => t.Room(normalized) is not null, idsFromDatabase: false, cancellationToken);
        if (timetable.Room(normalized) is not { } room)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such room");
        }

        var (week, occurrences) = timetable.RoomDay(room, day);
        return TypedResults.Ok(new RoomTimetableResponse(day, week, RoomResponse.Of(room), occurrences));
    }

    /// <summary>The body of a timetable: the classes, the group, and the notes of the group's year.</summary>
    private static TimetableResponse TimetableResponseOf(TimetableSnapshot timetable, Guid? groupId, DateOnly from, DateOnly to,
        IReadOnlyList<OccurrenceResponse> occurrences) =>
        new(from, to, timetable.GroupResponse(groupId), occurrences, timetable.Notes(groupId));

    private static string Oid(ClaimsPrincipal principal) => MeEndpoint.Identity.From(principal).Oid;

    /// <summary>The signed-in student's id and study group, or null without an account. One query.</summary>
    private static async Task<(Guid UserId, Guid? StudyGroupId)?> StudyGroupOfAsync(CampusPulseDbContext db, ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var oid = Oid(principal);
        var row = await db.Users.AsNoTracking()
            .Where(u => u.EntraObjectId == oid)
            .Select(u => new { u.Id, StudyGroupId = db.StudentSettings.Where(s => s.UserId == u.Id).Select(s => s.StudyGroupId).FirstOrDefault() })
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : (row.Id, row.StudyGroupId);
    }

    private static Task<User?> UserAsync(CampusPulseDbContext db, ClaimsPrincipal principal, CancellationToken cancellationToken) =>
        MeEndpoint.FindUserAsync(db, principal, cancellationToken);

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

    internal static DateOnly Today(TimeProvider time) => RomanianTime.Today(time);

    private static ProblemHttpResult BadRange() => TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Bad date range",
        detail: $"from must not be after to, the range is at most {MaxRangeDays} days, and both are in 2000-2100 (dates as yyyy-MM-dd).");

    private static ProblemHttpResult NoGroup() => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such study group");

    private static ProblemHttpResult NoTimetable() => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound,
        title: "No timetable", detail: "No semester has been imported yet.");
}
