using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CampusPulse.Api.Timetable;
using CampusPulse.Api.Timetable.Import;
using CampusPulse.Domain.Timetable;
using CampusPulse.Domain.Users;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CampusPulse.Api.Tests;

/// <summary>The import against a real, migrated Postgres. Each test imports into its own academic year,
/// so they don't see each other's rows (programmes and rooms are shared, as in production). Every name in
/// the workbooks is invented.</summary>
public sealed class TimetableImportTests(PostgresAppFactory factory) : IClassFixture<PostgresAppFactory>
{
    private static string OverridesFor(string academicYear, string? firstWeek = null, string? renameSource = null)
    {
        var yaml = TimetableParserTests.Overrides.Replace("academicYear: \"2030-2031\"", $"academicYear: \"{academicYear}\"");
        if (firstWeek is not null)
        {
            yaml = yaml.Replace("firstWeekStartsOn: \"2030-09-30\"", $"firstWeekStartsOn: \"{firstWeek}\"");
        }

        return renameSource is null ? yaml : yaml.Replace("orar: { sheets:", $"{renameSource}: {{ sheets:");
    }

    private static ImportFile File(ImportOverrides overrides, TimetableWorkbook workbook, string fileName = "orar.xlsx")
    {
        var bytes = workbook.Bytes();
        using var stream = new MemoryStream(bytes);
        var sheets = SheetGrid.Load(stream);
        var source = overrides.SourceOf([.. sheets.Select(s => s.Name)], out var problem) ?? throw new InvalidOperationException(problem);
        return new ImportFile(source, fileName, ImportTimetableCommand.Sha256(bytes), new SheetParser(overrides).Parse(source, fileName, sheets));
    }

    private async Task<(int Exit, string Output)> Import(string overridesYaml, IReadOnlyList<(TimetableWorkbook Workbook, string Name)> workbooks,
        bool dryRun = false, bool force = false, string overridesSha = "overrides-1")
    {
        var overrides = ImportOverrides.Parse(overridesYaml);
        var output = new StringWriter();
        await using var db = factory.CreateDbContext();
        var exit = await new TimetableImporter(db, TimeProvider.System, output)
            .RunAsync(overrides, overridesSha.PadRight(64, '0')[..64], [.. workbooks.Select(w => File(overrides, w.Workbook, w.Name))],
                dryRun, force, CancellationToken.None);
        return (exit, output.ToString());
    }

    private Task<(int Exit, string Output)> Import(string academicYear, TimetableWorkbook workbook, string fileName = "orar.xlsx",
        bool dryRun = false, bool force = false, string overridesSha = "overrides-1") =>
        Import(OverridesFor(academicYear), [(workbook, fileName)], dryRun, force, overridesSha);

    private async Task<(Guid Semester, int Groups, int Offered, int Subjects, int Slots, int Offers, int Notes, int Runs)> Counts(string academicYear)
    {
        await using var db = factory.CreateDbContext();
        var semester = await db.Semesters.SingleAsync(s => s.AcademicYear == academicYear);
        var subjectIds = await db.Subjects.Where(s => s.SemesterId == semester.Id).Select(s => s.Id).ToListAsync();
        return (semester.Id,
            await db.StudyGroups.CountAsync(g => g.SemesterId == semester.Id),
            await db.StudyGroups.CountAsync(g => g.SemesterId == semester.Id && g.RetiredAt == null),
            subjectIds.Count,
            await db.TimetableSlots.CountAsync(s => s.SemesterId == semester.Id),
            await db.OfferedSlots.CountAsync(o => subjectIds.Contains(o.SubjectId)),
            await db.TimetableNotes.CountAsync(n => n.SemesterId == semester.Id),
            await db.TimetableImportRuns.CountAsync(r => r.SemesterId == semester.Id));
    }

    /// <summary>A student (by oid) in a group, with a subject and maybe an offered slot picked.</summary>
    private async Task<(Guid UserId, string Oid)> Student(Guid? groupId, Guid? subjectId = null, Guid? offerId = null)
    {
        await using var db = factory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var oid = "oid-" + Guid.NewGuid().ToString("N");
        var user = new User(oid, "ana@student.example", "Ana", now);
        db.Users.Add(user);
        var settings = new StudentSettings(user.Id, now);
        settings.SetStudyGroup(groupId, now);
        db.StudentSettings.Add(settings);
        if (subjectId is { } subject)
        {
            var pick = new StudentSubject(user.Id, subject, now);
            pick.Pick(offerId, now);
            db.StudentSubjects.Add(pick);
        }

        await db.SaveChangesAsync();
        return (user.Id, oid);
    }

    [PostgresFact]
    public async Task An_import_writes_the_workbook_and_the_same_workbook_again_is_skipped()
    {
        using var workbook = TimetableParserTests.Workbook();
        var parsed = File(ImportOverrides.Parse(OverridesFor("2031-2032")), workbook).Parsed;

        var (exit, output) = await Import("2031-2032", workbook);

        Assert.Equal(ImportExit.Done, exit);
        var counts = await Counts("2031-2032");
        Assert.Equal((parsed.Groups.Count, parsed.Subjects.Count, parsed.Slots.Count, parsed.Offers.Count, parsed.Notes.Count, 1),
            (counts.Groups, counts.Subjects, counts.Slots, counts.Offers, counts.Notes, counts.Runs));
        Assert.Contains("Imported 1 workbook(s) into 2031-2032 semester 1.", output);

        await using (var db = factory.CreateDbContext())
        {
            var semester = await db.Semesters.Include(s => s.Weeks).Include(s => s.NonTeachingDays).SingleAsync(s => s.Id == counts.Semester);
            Assert.Equal((14, new DateOnly(2031, 1, 6)), (semester.WeekCount, semester.Weeks.Single(w => w.Number == 13).StartsOn));
            var run = await db.TimetableImportRuns.SingleAsync(r => r.SemesterId == counts.Semester);
            Assert.Equal(("orar", "orar.xlsx", SheetParser.Version), (run.Source, run.FileName, run.ParserVersion));
            Assert.Equal(["An 1", "An 2", "An 3", "An 1 MDATA"], run.Sheets);
            var course = await db.TimetableSlots.Include(s => s.Groups).SingleAsync(s => s.SemesterId == counts.Semester && s.SourceCell == "An 1!C5:H5");
            Assert.Equal((6, "A. Testescu", "1-10"), (course.Groups.Count, course.Teacher, course.Weeks.ToString()));
        }

        var again = await Import("2031-2032", workbook);

        Assert.Contains("already imported", again.Output);
        Assert.Equal(counts, await Counts("2031-2032"));
    }

    [PostgresFact]
    public async Task Subjects_get_their_Campus_Virtual_course_and_a_changed_mapping_updates_it()
    {
        using var workbook = TimetableParserTests.Workbook();
        var yaml = OverridesFor("2042-2043") + "\n" + TimetableParserTests.CampusVirtualSection;

        var (exit, output) = await Import(yaml, [(workbook, "orar.xlsx")]);

        Assert.Equal(ImportExit.Done, exit);
        var subjects = File(ImportOverrides.Parse(yaml), workbook).Parsed.Subjects.Count;
        Assert.Contains($"; {subjects - 2} subjects without a Campus Virtual course.", output);
        Assert.Equal(4101, await CourseOf("2042-2043", "Geometria fractalilor"));
        Assert.Equal(4102, await CourseOf("2042-2043", "Pedagogia jocului"));
        Assert.Null(await CourseOf("2042-2043", "Logica senzorilor virtuali"));

        // A corrected mapping: the overrides changed, so the same workbook is imported again.
        var (again, againOutput) = await Import(yaml.Replace("id: 4101", "id: 4201"), [(workbook, "orar.xlsx")], overridesSha: "overrides-2");

        Assert.Equal(ImportExit.Done, again);
        Assert.Contains("   - ALFA y1 Geometria fractalilor [Fixed] cv:4101", againOutput);
        Assert.Contains("   + ALFA y1 Geometria fractalilor [Fixed] cv:4201", againOutput);
        Assert.Equal(4201, await CourseOf("2042-2043", "Geometria fractalilor"));
    }

    [PostgresFact]
    public async Task A_removed_entry_or_a_hand_entry_without_an_id_clears_the_course()
    {
        using var workbook = TimetableParserTests.Workbook();
        var yaml = OverridesFor("2043-2044") + "\n" + TimetableParserTests.CampusVirtualSection;
        Assert.Equal(ImportExit.Done, (await Import(yaml, [(workbook, "orar.xlsx")])).Exit);
        Assert.Equal((4101, 4102), (await CourseOf("2043-2044", "Geometria fractalilor"), await CourseOf("2043-2044", "Pedagogia jocului")));

        const string Geometry = "        \"GEOMETRIA fractalilor\": { id: 4101, shortname: \"S1-X-FAC-ALFA1-GF\" }\n";
        const string Pedagogy = "\"Pedagogia jocului\": { id: 4102, shortname: \"S1-X-FAC-PJ\" }";
        Assert.Contains(Geometry, yaml);
        Assert.Contains(Pedagogy, yaml);
        var changed = yaml.Replace(Geometry, "").Replace(Pedagogy, "\"Pedagogia jocului\": { hand: true }");

        Assert.Equal(ImportExit.Done, (await Import(changed, [(workbook, "orar.xlsx")], overridesSha: "overrides-2")).Exit);
        Assert.Null(await CourseOf("2043-2044", "Geometria fractalilor"));
        Assert.Null(await CourseOf("2043-2044", "Pedagogia jocului"));
    }

    private async Task<int?> CourseOf(string academicYear, string subject)
    {
        await using var db = factory.CreateDbContext();
        var semester = await db.Semesters.SingleAsync(s => s.AcademicYear == academicYear);
        return await db.Subjects.Where(s => s.SemesterId == semester.Id && s.Name == subject).Select(s => s.CampusVirtualCourseId).SingleAsync();
    }

    [PostgresFact]
    public async Task A_renamed_file_replaces_what_it_brought_instead_of_doubling_it()
    {
        using var first = TimetableParserTests.Workbook();
        await Import("2032-2033", first);
        var counts = await Counts("2032-2033");

        // The same workbook, downloaded again under another name.
        Assert.Equal(ImportExit.Done, (await Import("2032-2033", first, fileName: "Orar (1).xlsx", force: true)).Exit);
        var again = await Counts("2032-2033");
        Assert.Equal((counts.Groups, counts.Subjects, counts.Slots, counts.Offers, counts.Notes),
            (again.Groups, again.Subjects, again.Slots, again.Offers, again.Notes));

        // A newer version under a third name: its classes, not both.
        using var second = TimetableParserTests.Workbook(facultativeTimes: "Luni 16-18 Q202");
        Assert.Equal(ImportExit.Done, (await Import("2032-2033", second, fileName: "orar_v2.xlsx")).Exit);
        var newer = await Counts("2032-2033");
        Assert.Equal((counts.Slots, counts.Offers), (newer.Slots, newer.Offers));
    }

    [PostgresFact]
    public async Task A_source_id_that_changed_for_sheets_already_imported_is_refused()
    {
        using var workbook = TimetableParserTests.Workbook();
        await Import("2033-2034", workbook);
        var counts = await Counts("2033-2034");

        var (exit, output) = await Import(OverridesFor("2033-2034", renameSource: "orar-nou"), [(workbook, "orar.xlsx")]);

        Assert.Equal(ImportExit.Failed, exit);
        Assert.Contains("were imported under source orar", output);
        Assert.Equal(counts, await Counts("2033-2034"));
    }

    [PostgresFact]
    public async Task Changed_overrides_or_force_import_the_same_workbook_again()
    {
        using var workbook = TimetableParserTests.Workbook();
        await Import("2034-2035", workbook);

        Assert.DoesNotContain("already imported", (await Import("2034-2035", workbook, overridesSha: "overrides-2")).Output);
        Assert.DoesNotContain("already imported", (await Import("2034-2035", workbook, overridesSha: "overrides-2", force: true)).Output);

        Assert.Equal(3, (await Counts("2034-2035")).Runs);
    }

    [PostgresFact]
    public async Task A_dry_run_prints_the_changes_checks_everything_and_writes_nothing()
    {
        using var workbook = TimetableParserTests.Workbook();

        var (exit, output) = await Import("2035-2036", workbook, dryRun: true);

        Assert.Equal(ImportExit.Done, exit);
        Assert.Contains("   + ALFA y1 X 1.2 | Wednesday 08:00-10:00 | Logica senzorilor virtuali Lab | room R426 | weeks 1-14 | -", output);
        Assert.Contains("Dry run: everything was checked and rolled back; nothing was written.", output);
        await using var db = factory.CreateDbContext();
        Assert.False(await db.Semesters.AnyAsync(s => s.AcademicYear == "2035-2036"));
    }

    [PostgresFact]
    public async Task A_missing_sheet_is_refused_and_with_force_its_groups_and_subjects_are_retired_not_deleted()
    {
        using (var full = TimetableParserTests.Workbook())
        {
            await Import("2036-2037", full);
        }

        var before = await Counts("2036-2037");
        using var thinner = TimetableParserTests.Workbook(withAn2: false);

        var refused = await Import("2036-2037", thinner);

        Assert.Equal(ImportExit.Refused, refused.Exit);
        Assert.Contains("sheet \"An 2\" (in sources.orar) isn't in the workbook", refused.Output);
        Assert.Contains("Refused: ", refused.Output);
        Assert.Equal(before, await Counts("2036-2037"));

        var dry = await Import("2036-2037", thinner, dryRun: true);
        Assert.Equal((ImportExit.Refused, true), (dry.Exit, dry.Output.Contains("Dry run: this would be refused", StringComparison.Ordinal)));

        Assert.Equal(ImportExit.Done, (await Import("2036-2037", thinner, force: true)).Exit);
        var after = await Counts("2036-2037");
        Assert.Equal(before.Groups, after.Groups);              // nothing deleted
        Assert.Equal(before.Offered - 2, after.Offered);        // the An 2 groups (1.1, 1.2) are no longer offered
        await using var db = factory.CreateDbContext();
        Assert.All(await db.Subjects.Where(s => s.SemesterId == before.Semester && s.Name == "Programare Kotlin").ToListAsync(),
            s => Assert.NotNull(s.RetiredAt));
    }

    [PostgresFact]
    public async Task A_renamed_subject_and_a_vanished_group_with_students_are_counted_refused_and_with_force_kept_as_no_longer_offered()
    {
        using (var first = TimetableParserTests.Workbook())
        {
            await Import("2037-2038", first);
        }

        var semesterId = (await Counts("2037-2038")).Semester;
        Guid groupX, groupY, facultative, facultativeOffer, kotlin;
        await using (var db = factory.CreateDbContext())
        {
            var groups = await db.StudyGroups.Where(g => g.SemesterId == semesterId).ToListAsync();
            groupX = groups.Single(g => g.Series == "X" && g.Label == "1.2").Id;
            groupY = groups.Single(g => g.Series == "Y" && g.Label == "1.1").Id;
            var subjects = await db.Subjects.Where(s => s.SemesterId == semesterId).ToListAsync();
            facultative = subjects.Single(s => s.Name == "Pedagogia jocului").Id;
            kotlin = subjects.Single(s => s.Name == "Programare Kotlin").Id;
            facultativeOffer = (await db.OfferedSlots.SingleAsync(o => o.SubjectId == facultative)).Id;
        }

        var inX = await Student(groupX, facultative, facultativeOffer);
        var inY = await Student(groupY);

        // The next file: series Y is gone, and the facultative subject is renamed.
        using var second = TimetableParserTests.Workbook(withSeries2: false, facultativeName: "Pedagogia jocului aplicata");
        var refused = await Import("2037-2038", second);

        Assert.Equal(ImportExit.Refused, refused.Exit);
        Assert.Contains("for students: 1 whose study group is no longer offered, 1 chosen subjects no longer offered (both kept, shown as such), "
            + "1 picked offered slots cleared", refused.Output);
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(facultativeOffer, (await db.StudentSubjects.SingleAsync(s => s.UserId == inX.UserId)).OfferedSlotId);
        }

        Assert.Equal(ImportExit.Done, (await Import("2037-2038", second, force: true)).Exit);

        await using (var db = factory.CreateDbContext())
        {
            // Retired, not deleted: the student keeps both, the pick of the offer that went is cleared.
            Assert.NotNull((await db.StudyGroups.SingleAsync(g => g.Id == groupY)).RetiredAt);
            Assert.Equal(groupY, (await db.StudentSettings.SingleAsync(s => s.UserId == inY.UserId)).StudyGroupId);
            Assert.NotNull((await db.Subjects.SingleAsync(s => s.Id == facultative)).RetiredAt);
            var choice = await db.StudentSubjects.SingleAsync(s => s.UserId == inX.UserId);
            Assert.Equal((facultative, (Guid?)null), (choice.SubjectId, choice.OfferedSlotId));
            Assert.True(await db.Subjects.AnyAsync(s => s.SemesterId == semesterId && s.Name == "Pedagogia jocului aplicata" && s.RetiredAt == null));
            Assert.Null((await db.Subjects.SingleAsync(s => s.Id == kotlin)).RetiredAt);
        }

        var client = factory.CreateClient();
        var settings = await Get<StudentSettingsResponse>(client, "/me/settings", inY.Oid);
        Assert.Equal((groupY, true), (settings.StudyGroup?.Id, settings.NoLongerOffered));
        var chosen = Assert.Single(await Get<List<StudentSubjectResponse>>(client, "/me/subjects", inX.Oid));
        Assert.Equal((facultative, true, (Guid?)null), (chosen.SubjectId, chosen.NoLongerOffered, chosen.OfferedSlotId));

        // Back in a later file: offered again, with the student still in it.
        using var third = TimetableParserTests.Workbook();
        Assert.Equal(ImportExit.Done, (await Import("2037-2038", third, force: true)).Exit);
        await using (var db = factory.CreateDbContext())
        {
            Assert.Null((await db.StudyGroups.SingleAsync(g => g.Id == groupY)).RetiredAt);
        }
    }

    [PostgresFact]
    public async Task Two_workbooks_in_one_run_share_their_groups_and_subjects()
    {
        TimetableWorkbook Omega(string sheet)
        {
            var workbook = new TimetableWorkbook();
            workbook.Sheet(sheet).Activity(0, 8, "C", "Seminar comun (s) N. Fictivu\nsala Q101", lastColumn: "D");
            return workbook;
        }

        using var a = Omega("Omega 4");
        using var b = Omega("Omega 4 bis");

        var (exit, output) = await Import(OverridesFor("2038-2039"), [(a, "omega.xlsx"), (b, "omega-bis.xlsx")]);

        Assert.True(exit == ImportExit.Done, output);
        var counts = await Counts("2038-2039");
        Assert.Equal((1, 1, 2), (counts.Groups, counts.Subjects, counts.Slots));
    }

    [PostgresFact]
    public async Task A_group_of_an_older_semester_is_flagged_after_a_new_semester_is_imported()
    {
        using (var older = TimetableParserTests.Workbook())
        {
            await Import(OverridesFor("2040-2041", firstWeek: "2040-10-01"), [(older, "orar.xlsx")]);
        }

        using (var newer = TimetableParserTests.Workbook())
        {
            await Import(OverridesFor("2041-2042", firstWeek: "2041-09-30"), [(newer, "orar.xlsx")]);
        }

        Guid oldGroup;
        await using (var db = factory.CreateDbContext())
        {
            var semester = await db.Semesters.SingleAsync(s => s.AcademicYear == "2040-2041");
            oldGroup = (await db.StudyGroups.FirstAsync(g => g.SemesterId == semester.Id)).Id;
        }

        var student = await Student(oldGroup);
        var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(new DateTimeOffset(2041, 10, 8, 9, 0, 0, TimeSpan.Zero)));
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options => options.TimeProvider = TimeProvider.System);
        }));

        var settings = await Get<StudentSettingsResponse>(app.CreateClient(), "/me/settings", student.Oid);

        Assert.Equal((oldGroup, true, false), (settings.StudyGroup?.Id, settings.FromAnotherSemester, settings.NoLongerOffered));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static async Task<T> Get<T>(HttpClient client, string path, string oid)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token(oid: oid));
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"GET {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }
}
