using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CampusPulse.Api.Timetable;
using CampusPulse.Api.Timetable.Import;
using CampusPulse.Domain.Timetable;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CampusPulse.Api.Tests;

/// <summary>A migrated Postgres with the (invented) test workbook imported once: 2030-31, semester 1, week 1
/// from Monday 30 Sep 2030, 2 Dec a non-teaching day, 23 Dec - 5 Jan the break. Two subjects have a Campus
/// Virtual course (<see cref="TimetableParserTests.CampusVirtualSection"/>).</summary>
public sealed class ImportedTimetable : IAsyncLifetime
{
    public PostgresAppFactory Factory { get; } = new();

    public async Task InitializeAsync()
    {
        await Factory.InitializeAsync();
        if (Factory.ConnectionString is null)
        {
            return; // no Postgres here: the tests are skipped
        }

        var overrides = ImportOverrides.Parse(TimetableParserTests.Overrides + "\n" + TimetableParserTests.CampusVirtualSection);
        using var workbook = TimetableParserTests.Workbook();
        var bytes = workbook.Bytes();
        using var stream = new MemoryStream(bytes);
        var file = new ImportFile("orar", "orar.xlsx", ImportTimetableCommand.Sha256(bytes),
            new SheetParser(overrides).Parse("orar", "orar.xlsx", SheetGrid.Load(stream)));
        await using var db = Factory.CreateDbContext();
        var exit = await new TimetableImporter(db, TimeProvider.System, TextWriter.Null)
            .RunAsync(overrides, new string('0', 64), [file], dryRun: false, force: false, CancellationToken.None);
        if (exit != ImportExit.Done)
        {
            throw new InvalidOperationException($"the test import exited {exit}");
        }
    }

    public Task DisposeAsync() => ((IAsyncLifetime)Factory).DisposeAsync();
}

public sealed class TimetableEndpointsTests(ImportedTimetable timetable) : IClassFixture<ImportedTimetable>
{
    /// <summary>Monday of week 2, 12:00 in Romania.</summary>
    private static readonly DateTimeOffset Now = new(2030, 10, 7, 9, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private WebApplicationFactory<Program> App(DateTimeOffset? now = null) =>
        timetable.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(now ?? Now));
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options => options.TimeProvider = TimeProvider.System);
        }));

    private static string NewOid() => "oid-" + Guid.NewGuid().ToString("N");

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, string? oid, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (oid is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token(oid: oid));
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        return await client.SendAsync(request);
    }

    private static async Task<T> Get<T>(HttpClient client, string path, string oid)
    {
        var response = await Send(client, HttpMethod.Get, path, oid);
        Assert.True(response.IsSuccessStatusCode, $"GET {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    /// <summary>A new student: signed in (POST /me) and, if given, in that group.</summary>
    private static async Task<string> Student(HttpClient client, Guid? groupId = null)
    {
        var oid = NewOid();
        Assert.Equal(HttpStatusCode.Created, (await Send(client, HttpMethod.Post, "/me", oid)).StatusCode);
        if (groupId is not null)
        {
            Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Put, "/me/settings", oid, new { studyGroupId = groupId })).StatusCode);
        }

        return oid;
    }

    private static async Task<StudyGroupResponse> Group(HttpClient client, string programme, int year, string series, string label)
    {
        var programmes = await Get<List<ProgrammeResponse>>(client, "/timetable/programmes", NewOid());
        return programmes.Single(p => p.Code == programme).Years.Single(y => y.Year == year).Groups.Single(g => g.Series == series && g.Label == label);
    }

    private static string Lines(TimetableResponse response) =>
        string.Join("; ", response.Occurrences.Select(o => $"{o.Date:MM-dd} {o.StartsAt:HH\\:mm} {o.Subject}"));

    [PostgresFact]
    public async Task The_current_semester_has_its_weeks_non_teaching_days_and_todays_week()
    {
        var semester = await Get<SemesterResponse>(App().CreateClient(), "/semesters/current", NewOid());

        Assert.Equal(("2030-2031", 1, 14, 2), (semester.AcademicYear, semester.Number, semester.Weeks.Count, semester.TodayWeek));
        Assert.Equal(new DateOnly(2030, 10, 7), semester.Today);
        Assert.Equal([new DateOnly(2030, 12, 2)], semester.NonTeachingDays.Select(d => d.Date));
    }

    /// <summary>At 22:30 UTC on Sunday it is already Monday 01:30 in Bucharest: today is Monday.</summary>
    [PostgresFact]
    public async Task Today_is_the_date_in_Bucharest_not_in_UTC()
    {
        var semester = await Get<SemesterResponse>(App(new DateTimeOffset(2030, 10, 6, 22, 30, 0, TimeSpan.Zero)).CreateClient(), "/semesters/current", NewOid());

        Assert.Equal((new DateOnly(2030, 10, 7), 2), (semester.Today, semester.TodayWeek));
    }

    [PostgresFact]
    public async Task Programmes_list_their_years_and_groups()
    {
        var programmes = await Get<List<ProgrammeResponse>>(App().CreateClient(), "/timetable/programmes", NewOid());

        var alfa = programmes.Single(p => p.Code == "ALFA");
        Assert.Equal("Programul Alfa", alfa.Name);
        Assert.Equal(["X 1.1", "X 1.2", "X 2.1", "X 2.2", "X 3.1", "X 3.2", "Y 1.1", "Y 1.2"],
            Assert.Single(alfa.Years).Groups.Select(g => $"{g.Series} {g.Label}"));
        Assert.Equal(["1"], Assert.Single(programmes.Single(p => p.Code == "MDATA").Years).Groups.Select(g => g.Label));
    }

    [PostgresFact]
    public async Task A_student_picks_a_group_and_gets_its_week()
    {
        var client = App().CreateClient();
        var oid = await Student(client);
        var empty = await Get<StudentSettingsResponse>(client, "/me/settings", oid);
        Assert.Equal((null, false, false), (empty.StudyGroup, empty.NoLongerOffered, empty.FromAnotherSemester));
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client, HttpMethod.Put, "/me/settings", oid, new { studyGroupId = Guid.NewGuid() })).StatusCode);

        var group = await Group(client, "ALFA", 1, "X", "1.2");
        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Put, "/me/settings", oid, new { studyGroupId = group.Id })).StatusCode);
        var settings = await Get<StudentSettingsResponse>(client, "/me/settings", oid);
        Assert.Equal((group.Id, false, false), (settings.StudyGroup?.Id, settings.NoLongerOffered, settings.FromAnotherSemester));

        var week1 = await Get<TimetableResponse>(client, "/me/timetable?from=2030-09-30&to=2030-10-06", oid);

        Assert.Equal("09-30 08:00 Geometria fractalilor; 09-30 10:00 Logica senzorilor virtuali; 10-01 12:00 Esperanto; 10-01 12:00 Volapuk; "
            + "10-01 18:00 Teh.avans. moderne; 10-02 08:00 Logica senzorilor virtuali; 10-03 08:00 Cromodinamica cuantica", Lines(week1));
        var lab = week1.Occurrences.Single(o => o.Date == new DateOnly(2030, 10, 2));
        Assert.Equal([ActivityKind.Lab], lab.Kinds);
        Assert.Equal(("R426", 1), (lab.Room, lab.Week));
        Assert.Contains(week1.Notes, n => n.Text == "*** SPORT");
    }

    [PostgresFact]
    public async Task An_optional_subject_shows_only_once_chosen_with_the_picked_slot()
    {
        var client = App().CreateClient();
        var group = await Group(client, "ALFA", 1, "X", "1.2");
        var oid = await Student(client, group.Id);
        var choice = Assert.Single(await Get<List<ChoiceSubjectResponse>>(client, $"/timetable/groups/{group.Id}/choices", oid));
        Assert.Equal(("Pedagogia jocului", SubjectChoice.Optional), (choice.Name, choice.Choice));
        var offer = Assert.Single(choice.Offers);
        Assert.Equal((DayOfWeek.Monday, new TimeOnly(14, 0), "Q202"), (offer.Weekday, offer.StartsAt, offer.Room));

        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Put, $"/me/subjects/{choice.Id}", oid, new { offeredSlotId = offer.Id })).StatusCode);

        var monday = await Get<TimetableResponse>(client, "/me/timetable?from=2030-09-30", oid);
        var picked = monday.Occurrences.Single(o => o.Subject == "Pedagogia jocului");
        Assert.Equal((offer.Id, true, (Guid?)null), (picked.OfferedSlotId, picked.Optional, picked.SlotId));
        var mine = Assert.Single(await Get<List<StudentSubjectResponse>>(client, "/me/subjects", oid));
        Assert.Equal((offer.Id, false), (mine.OfferedSlotId, mine.NoLongerOffered));

        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Delete, $"/me/subjects/{choice.Id}", oid)).StatusCode);
        Assert.DoesNotContain((await Get<TimetableResponse>(client, "/me/timetable?from=2030-09-30", oid)).Occurrences, o => o.Subject == "Pedagogia jocului");
    }

    [PostgresFact]
    public async Task Every_class_and_subject_links_to_its_Campus_Virtual_course_where_it_has_one()
    {
        const string Geometry = "https://cv.upt.ro/course/view.php?id=4101";
        const string Pedagogy = "https://cv.upt.ro/course/view.php?id=4102";
        var client = App().CreateClient();
        var group = await Group(client, "ALFA", 1, "X", "1.2");
        var oid = await Student(client, group.Id);

        var choice = Assert.Single(await Get<List<ChoiceSubjectResponse>>(client, $"/timetable/groups/{group.Id}/choices", oid));
        Assert.Equal(Pedagogy, choice.CampusVirtualUrl);
        var picked = await Send(client, HttpMethod.Put, $"/me/subjects/{choice.Id}", oid, new { offeredSlotId = choice.Offers[0].Id });
        Assert.Equal(Pedagogy, (await picked.Content.ReadFromJsonAsync<StudentSubjectResponse>(Json))!.CampusVirtualUrl);
        Assert.Equal(Pedagogy, Assert.Single(await Get<List<StudentSubjectResponse>>(client, "/me/subjects", oid)).CampusVirtualUrl);

        var week1 = await Get<TimetableResponse>(client, "/me/timetable?from=2030-09-30&to=2030-10-06", oid);
        Assert.Equal(
            $"Geometria fractalilor {Geometry}; Logica senzorilor virtuali -; Pedagogia jocului {Pedagogy}",
            string.Join("; ", week1.Occurrences.Where(o => o.Subject is "Geometria fractalilor" or "Logica senzorilor virtuali" or "Pedagogia jocului")
                .Select(o => $"{o.Subject} {o.CampusVirtualUrl ?? "-"}").Distinct().Order(StringComparer.Ordinal)));
        Assert.All(week1.Occurrences.Where(o => o.Subject is not ("Geometria fractalilor" or "Pedagogia jocului")), o => Assert.Null(o.CampusVirtualUrl));

        var k210 = await Get<RoomTimetableResponse>(client, "/rooms/K210/timetable?date=2030-09-30", oid);
        Assert.Equal(Geometry, Assert.Single(k210.Occurrences).CampusVirtualUrl);
        var q202 = await Get<RoomTimetableResponse>(client, "/rooms/Q202/timetable?date=2030-09-30", oid);
        Assert.Equal(Pedagogy, Assert.Single(q202.Occurrences).CampusVirtualUrl);
    }

    [PostgresFact]
    public async Task Choosing_refuses_fixed_subjects_foreign_offers_and_unknown_subjects()
    {
        var client = App().CreateClient();
        var group = await Group(client, "ALFA", 1, "X", "1.2");
        var oid = await Student(client, group.Id);
        var geometry = (await Get<TimetableResponse>(client, $"/timetable/groups/{group.Id}?from=2030-09-30", oid)).Occurrences
            .First(o => o.Subject == "Geometria fractalilor").SubjectId;
        var facultative = Assert.Single(await Get<List<ChoiceSubjectResponse>>(client, $"/timetable/groups/{group.Id}/choices", oid));
        var master = await Group(client, "MDATA", 1, "", "1");
        var foreignOffer = (await Get<List<ChoiceSubjectResponse>>(client, $"/timetable/groups/{master.Id}/choices", oid)).Single().Offers[0].Id;

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client, HttpMethod.Put, $"/me/subjects/{geometry}", oid, new { offeredSlotId = (Guid?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client, HttpMethod.Put, $"/me/subjects/{facultative.Id}", oid, new { offeredSlotId = foreignOffer })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Put, $"/me/subjects/{Guid.NewGuid()}", oid, new { offeredSlotId = (Guid?)null })).StatusCode);
    }

    [PostgresFact]
    public async Task Weeks_parity_alternation_non_teaching_days_and_the_break_are_applied()
    {
        var client = App().CreateClient();
        var oid = NewOid();
        var seminarGroup = await Group(client, "ALFA", 1, "X", "3.1");
        var plain = await Group(client, "ALFA", 1, "X", "1.1");
        var series2 = await Group(client, "ALFA", 1, "Y", "1.1");

        // The seminar is in odd weeks 1-7: Wednesday of week 1, not week 2.
        Assert.Equal("10-02 12:00 Logica senzorilor virtuali",
            Lines(await Get<TimetableResponse>(client, $"/timetable/groups/{seminarGroup.Id}?from=2030-10-02&to=2030-10-09", oid))
                .Split("; ").Single(l => l.Contains("12:00 Logica", StringComparison.Ordinal)));
        // Two labs alternating in one column: optics in odd weeks, acoustics in even ones, 08-10 both.
        Assert.Equal("10-02 08:00 Optica; 10-09 08:00 Acustica",
            string.Join("; ", Lines(await Get<TimetableResponse>(client, $"/timetable/groups/{series2.Id}?from=2030-10-02&to=2030-10-09", oid))
                .Split("; ").Where(l => l.Contains("-02 08:00", StringComparison.Ordinal) || l.Contains("-09 08:00", StringComparison.Ordinal))));
        Assert.Empty((await Get<TimetableResponse>(client, $"/timetable/groups/{plain.Id}?from=2030-12-02", oid)).Occurrences);
        // Week 11: geometry (weeks 1-10) is over, logic goes on.
        Assert.Equal("12-09 10:00 Logica senzorilor virtuali", Lines(await Get<TimetableResponse>(client, $"/timetable/groups/{plain.Id}?from=2030-12-09", oid)));
        Assert.Empty((await Get<TimetableResponse>(client, $"/timetable/groups/{plain.Id}?from=2030-12-23&to=2031-01-03", oid)).Occurrences);
    }

    [PostgresFact]
    public async Task Ranges_are_checked()
    {
        var client = App().CreateClient();
        var plain = await Group(client, "ALFA", 1, "X", "1.1");
        var oid = NewOid();

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client, HttpMethod.Get, $"/timetable/groups/{plain.Id}?from=2030-10-10&to=2030-10-09", oid)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client, HttpMethod.Get, $"/timetable/groups/{plain.Id}?from=2030-10-01&to=2030-11-01", oid)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client, HttpMethod.Get, $"/timetable/groups/{plain.Id}?from=9999-12-31", oid)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client, HttpMethod.Get, "/rooms/K210/timetable?date=9999-12-31", oid)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Get, $"/timetable/groups/{Guid.NewGuid()}", oid)).StatusCode);
        // Today by default: the fixed clock's Monday of week 2.
        Assert.Equal(new DateOnly(2030, 10, 7), (await Get<TimetableResponse>(client, $"/timetable/groups/{plain.Id}", oid)).From);
    }

    [PostgresFact]
    public async Task An_optional_subjects_grid_classes_are_in_the_group_view_but_a_students_only_once_chosen()
    {
        var client = App().CreateClient();
        var group = await Group(client, "ROB", 3, "", "1.1");
        var oid = await Student(client, group.Id);

        var groupView = await Get<TimetableResponse>(client, $"/timetable/groups/{group.Id}?from=2030-09-30", oid);
        var course = Assert.Single(groupView.Occurrences);
        Assert.Equal(("Integrarea si testarea senzorilor robotici", true), (course.Subject, course.Optional));
        Assert.Empty((await Get<TimetableResponse>(client, "/me/timetable?from=2030-09-30", oid)).Occurrences);

        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Put, $"/me/subjects/{course.SubjectId}", oid, new { offeredSlotId = (Guid?)null })).StatusCode);

        Assert.Equal(course.SlotId, Assert.Single((await Get<TimetableResponse>(client, "/me/timetable?from=2030-09-30", oid)).Occurrences).SlotId);
    }

    [PostgresFact]
    public async Task A_pick_slot_lab_shows_the_course_always_and_the_lab_at_the_picked_time()
    {
        var client = App().CreateClient();
        var group = await Group(client, "MDATA", 1, "", "1");
        var oid = await Student(client, group.Id);
        var choice = Assert.Single(await Get<List<ChoiceSubjectResponse>>(client, $"/timetable/groups/{group.Id}/choices", oid));
        Assert.Equal(SubjectChoice.PickSlot, choice.Choice);

        Assert.Equal("10-04 10:00 Fundamente de orbitare", Lines(await Get<TimetableResponse>(client, "/me/timetable?from=2030-10-04", oid)));

        var late = choice.Offers.Single(o => o.StartsAt == new TimeOnly(16, 0));
        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Put, $"/me/subjects/{choice.Id}", oid, new { offeredSlotId = late.Id })).StatusCode);

        Assert.Equal("10-04 10:00 Fundamente de orbitare; 10-04 16:00 Fundamente de orbitare",
            Lines(await Get<TimetableResponse>(client, "/me/timetable?from=2030-10-04", oid)));
    }

    [PostgresFact]
    public async Task A_room_lists_its_classes_and_offered_slots_on_a_date()
    {
        var client = App().CreateClient();
        var oid = NewOid();

        var rooms = await Get<List<RoomResponse>>(client, "/rooms", oid);
        Assert.Contains(rooms, r => r.Code == "K210");

        var k210 = await Get<RoomTimetableResponse>(client, "/rooms/k210/timetable?date=2030-09-30", oid);
        var geometry = Assert.Single(k210.Occurrences);
        Assert.Equal(("Geometria fractalilor", 6, false), (geometry.Subject, geometry.Groups.Count, geometry.Offered));
        Assert.Equal("ALFA 1 1.1", geometry.Groups[0]);

        var q202 = await Get<RoomTimetableResponse>(client, "/rooms/Q202/timetable?date=2030-09-30", oid);
        Assert.True(Assert.Single(q202.Occurrences).Offered);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Get, "/rooms/NOPE/timetable", oid)).StatusCode);
    }

    [PostgresFact]
    public async Task Without_sign_in_it_is_401_and_without_an_account_404()
    {
        var client = App().CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, HttpMethod.Get, "/me/timetable", oid: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, HttpMethod.Get, "/timetable/programmes", oid: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Get, "/me/timetable", NewOid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Put, "/me/settings", NewOid(), new { studyGroupId = (Guid?)null })).StatusCode);
    }

    [PostgresFact]
    public async Task Deleting_the_account_deletes_the_students_group_and_choices()
    {
        var client = App().CreateClient();
        var group = await Group(client, "ALFA", 1, "X", "1.2");
        var oid = await Student(client, group.Id);
        var choice = Assert.Single(await Get<List<ChoiceSubjectResponse>>(client, $"/timetable/groups/{group.Id}/choices", oid));
        await Send(client, HttpMethod.Put, $"/me/subjects/{choice.Id}", oid, new { offeredSlotId = choice.Offers[0].Id });
        Guid userId;
        await using (var db = timetable.Factory.CreateDbContext())
        {
            userId = (await db.Users.SingleAsync(u => u.EntraObjectId == oid)).Id;
            Assert.True(await db.StudentSettings.AnyAsync(s => s.UserId == userId));
        }

        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, HttpMethod.Delete, "/me", oid)).StatusCode);

        await using var after = timetable.Factory.CreateDbContext();
        Assert.False(await after.StudentSettings.AnyAsync(s => s.UserId == userId));
        Assert.False(await after.StudentSubjects.AnyAsync(s => s.UserId == userId));
    }
}
