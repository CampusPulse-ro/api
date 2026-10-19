using System.Security.Cryptography;
using System.Text;
using CampusPulse.Api.Timetable;
using CampusPulse.Api.Timetable.Import;
using CampusPulse.Domain.Timetable;

namespace CampusPulse.Api.Tests;

// Every name, subject and room in these tests is invented. The cells copy the faculty's shapes and quirks
// (two-row cells, abbreviations, week phrasings, typos), never their words.

public sealed class CellTextTests
{
    [Theory]
    [InlineData("Geometria fractalilor (c)  A. Testescu", ActivityKinds.Course)]
    [InlineData("LSV (l) R426", ActivityKinds.Lab)]
    [InlineData("Analiza spectrala (s) ", ActivityKinds.Seminar)]
    [InlineData("PK (p) R529", ActivityKinds.Project)]
    [InlineData("RCX (l+p) R018", ActivityKinds.Lab | ActivityKinds.Project)]
    [InlineData("MPX (c+l+p) R027b", ActivityKinds.Course | ActivityKinds.Lab | ActivityKinds.Project)]
    [InlineData("Pedagogia jocului (c.f.) D. Fictivu", ActivityKinds.Course | ActivityKinds.Facultative)]
    [InlineData("Pedagogia jocului (seminar) ", ActivityKinds.Seminar)]
    public void Activity_marks_are_read(string text, ActivityKinds kinds) => Assert.Equal(kinds, CellText.FindKinds(text)?.Kinds);

    [Theory]
    [InlineData("*** SPORT")]
    [InlineData("ora de consultatii")]
    [InlineData("XYZW (4sgr.)")]
    public void Text_without_a_mark_has_none(string text) => Assert.Null(CellText.FindKinds(text));

    [Fact]
    public void Every_mark_in_a_cell_is_found() =>
        Assert.Equal(2, CellText.FindAllKinds("Esperanto (s) E. Inventatu\nVolapuk (s) F. Imaginaru").Count);

    [Theory]
    [InlineData("A. Testescu", "A. Testescu")]
    [InlineData(" R. E. Mockanu sapt 5-14, sala Q106", "R. E. Mockanu")]
    [InlineData("I.Fictivu (4 sgr) sala Q204", "I.Fictivu")]
    [InlineData("Ș. Țeparu (6sgr.) sala 707CP", "Ș. Țeparu")]
    [InlineData(" sala R426", null)]
    [InlineData(" s. 1-7 s. impara, sala Q110", null)]
    public void Teachers_are_initials_and_a_surname(string afterMark, string? teacher) => Assert.Equal(teacher, CellText.Teacher(afterMark));

    [Theory]
    [InlineData("saptamanile 1-10, sala K210 - NORD", "K210")]
    [InlineData(" R418a", "R418A")]
    [InlineData(" room 707CP, weeks 1-10", "707CP")]
    [InlineData(" sala  L217 doar", "L217")]
    [InlineData(" sala SPM 909", "SPM909")]
    [InlineData(" 707CP 2h/12sapt", "707CP")]
    [InlineData(" sala NORD-N301", "NORD-N301")]
    [InlineData(" A. Testescu", null)]
    public void Rooms_are_read_and_normalised(string afterMark, string? room) => Assert.Equal(room, CellText.Room(afterMark));

    [Theory]
    [InlineData("saptamanile 1-10, sala K210", "1-10")]
    [InlineData("(saptamanile 3, 5, 7, 9) sala AULA", "3, 5, 7, 9")]
    [InlineData("sapt. 8-14", "8-14")]
    [InlineData("room 707CP, weeks 1-10", "1-10")]
    [InlineData("week 1-4", "1-4")]
    [InlineData("weeks 2 & 4", "2, 4")]
    [InlineData("XT (s) s. 1-7 s. impara, sala Q110", "1, 3, 5, 7")]
    [InlineData("XT (s) s. 1-7 s. para, sala Q110", "2, 4, 6")]
    [InlineData("TPX (p) R027B, s. impara", "1, 3, 5, 7, 9, 11, 13")]
    [InlineData("SX I (l)  Sapt. para R019", "2, 4, 6, 8, 10, 12, 14")]
    [InlineData("room AULA - odd weeks", "1, 3, 5, 7, 9, 11, 13")]
    [InlineData("A. Testescu, even weeks - room AULA", "2, 4, 6, 8, 10, 12, 14")]
    [InlineData("sala L2 Est, începand cu a 2-a săptămână", "2-14")]
    [InlineData("sala 707CP 2h/12sapt", "1-12")]
    [InlineData("Cromodinamica (c) N. Fabulosu, sapt. 1- 4, sala AULA", "1-4")]
    public void Week_expressions_become_weeks(string text, string weeks) => Assert.Equal(weeks, CellText.Weeks(text, 14)?.ToString());

    [Theory]
    [InlineData("Geometria fractalilor (c) A. Testescu")]
    [InlineData("LSV (l) R426")]
    public void Without_a_week_expression_there_is_no_restriction(string text) => Assert.Null(CellText.Weeks(text, 14));

    [Fact]
    public void A_list_with_an_ampersand_is_split() => Assert.Equal("2, 4", CellText.ParseList("2 & 4").ToString());

    [Fact]
    public void Segments_keep_week_lists_and_hours_over_weeks_whole() =>
        Assert.Equal(["Joi 8-10 (saptamanile 3, 5, 7, 9)", " sala 707CP 2h/12sapt", " Vineri 10-12"],
            CellText.Segments("Joi 8-10 (saptamanile 3, 5, 7, 9), sala 707CP 2h/12sapt; Vineri 10-12"));

    [Fact]
    public void An_offers_own_week_list_with_commas_stays_with_it()
    {
        var offers = CellText.Offers("sala R520 Joi 8-10 (saptamanile 3, 5, 7, 9), Vineri 10-12", null, 14, WeekSet.All(14));

        Assert.Equal(["Thursday 3, 5, 7, 9", "Friday 1-14"], offers.Select(o => $"{o.Weekday} {o.Weeks}"));
    }

    [Fact]
    public void Offers_take_the_room_of_their_segment_or_the_last_one_and_the_last_day_named()
    {
        var offers = CellText.Offers(" [R+E] sala R521 Marti 10-12\nsala R520 Luni 16-18, Miercuri 10-12, Joi 8-10, 10-12", null, 14, WeekSet.All(14));

        Assert.Equal(
            ["Tuesday 10:00-12:00 R521", "Monday 16:00-18:00 R520", "Wednesday 10:00-12:00 R520", "Thursday 08:00-10:00 R520",
             "Thursday 10:00-12:00 R520"],
            offers.Select(o => $"{o.Weekday} {o.StartsAt:HH\\:mm}-{o.EndsAt:HH\\:mm} {o.Room}"));
    }

    [Fact]
    public void A_room_written_after_the_time_belongs_to_that_time()
    {
        var offers = CellText.Offers("Marti 16-18 R418a,  Joi 14-16 R528a", "R528B", 14, WeekSet.All(14));

        Assert.Equal(["Tuesday 16 R418A", "Thursday 14 R528A"], offers.Select(o => $"{o.Weekday} {o.StartsAt.Hour} {o.Room}"));
    }

    [Fact]
    public void A_time_before_the_next_day_keeps_the_previous_day()
    {
        var offers = CellText.Offers("Tuesday 16-18, 18-20 Wednesday 8-10", "R419", 14, WeekSet.All(14));

        Assert.Equal(["Tuesday 16", "Tuesday 18", "Wednesday 8"], offers.Select(o => $"{o.Weekday} {o.StartsAt.Hour}"));
    }

    /// <summary>Weeks written in a segment are that segment's; the rest get the default (the import passes
    /// the weeks the subject's whole text names, so "sapt 8-14" anywhere covers every offer of it).</summary>
    [Fact]
    public void Week_ranges_are_not_times_and_belong_to_their_segment()
    {
        var offers = CellText.Offers("Tuesday 16-18; Wednesday 8-10 sapt 1-7", "R419", 14, WeekSet.Range(8, 14));

        Assert.Equal(["Tuesday 16 8-14", "Wednesday 8 1-7"], offers.Select(o => $"{o.Weekday} {o.StartsAt.Hour} {o.Weeks}"));
    }

    [Fact]
    public void Subject_names_lose_tags_and_stray_marks() => Assert.Equal("ITSR", CellText.SubjectName("ITSR   [R+E] "));

    [Fact]
    public void A_name_without_a_mark_loses_its_room_teacher_and_weeks() =>
        Assert.Equal("Teh.avans. moderne", CellText.NameWithoutDetails("Teh.avans. moderne 707CP G. Plasmuitu sapt 1-7"));
}

public sealed class TimetableParserTests
{
    internal const string Overrides = """
        semester: { academicYear: "2030-2031", number: 1 }
        calendar:
          firstWeekStartsOn: "2030-09-30"
          weeks: 14
          breaks: [ { from: "2030-12-23", to: "2031-01-05", name: { ro: "Vacanta de iarna", en: "Winter break" } } ]
          nonTeachingDays: [ { date: "2030-12-02", reason: { ro: "Zi libera", en: "Day off" } } ]
        programmes:
          ALFA: "Programul Alfa"
          ROB: "Sisteme robotice"
          SUB: "Sisteme subacvatice"
          MDATA: "Master MDATA"
          OMEGA: "Programul Omega"
        sources:
          orar: { sheets: ["An 1", "An 2", "An 3", "An 1 MDATA"] }
          omega: { sheets: ["Omega 4"] }
          omega-bis: { sheets: ["Omega 4 bis"] }
        sheets:
          "An 1":
            year: 1
            columns:
              "Seria 1 (X)": { programme: ALFA, series: X }
              "Seria 2 (Y)": { programme: ALFA, series: Y }
            columnRoles: { Q: facultative }
            aliases: { GEO: "Geometria fractalilor", NOPE: "Nimic" }
          "An 2":
            year: 2
            columns: { "SISTEME ROBOTICE": { programme: ROB } }
            columnRoles: { Q: facultative }
          "An 3":
            year: 3
            columns:
              "SISTEME ROBOTICE": { programme: ROB }
              "SISTEME SUBACVATICE": { programme: SUB }
            columnRoles: { P: optional }
            tags: { R: ROB, E: SUB }
            aliases: { VIZ: "Vizualizare stiintifica" }
            optionalSubjects: ["Vizualizare stiintifica"]
          "An 1 MDATA":
            year: 1
            programme: MDATA
            columnRoles: { E: pickSlot, F: notes }
          "Omega 4": { year: 4, programme: OMEGA }
          "Omega 4 bis": { year: 4, programme: OMEGA }
          "Orfan": { year: 1, programme: ALFA }
          "Legenda": { skip: true }
        cells:
          "An 1!C19": { text: "Skipped", skip: true }
          "An 1!C47": { text: "Nu e asta", skip: true }
          "An 1!C29": { text: "Teh.avans.", kind: course }
          "An 1!L63": { text: "Optica", end: "12:00", weeks: even }
          "An 1!Z99": { skip: true }
        """;

    /// <summary>A campusVirtual section to append to <see cref="Overrides"/>: scripts/tests/fixtures/campus-virtual-section.yaml,
    /// which the script's own tests check is what it writes. Two ALFA year 1 subjects with a course (one named in other
    /// case), a hand entry saying one has none, an entry naming no subject, and comments.</summary>
    internal static readonly string CampusVirtualSection =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "campus-virtual-section.yaml"));

    /// <param name="withSeries2">false drops series Y (its group columns and cells), as a later file might.</param>
    /// <param name="facultativeTimes">the facultative subject's offered times.</param>
    /// <param name="facultativeName">the facultative subject's name.</param>
    /// <param name="withAn2">false leaves the "An 2" sheet out, as a thinner file might.</param>
    internal static TimetableWorkbook Workbook(bool withSeries2 = true, string facultativeTimes = "Luni 14-16 Q202",
        string facultativeName = "Pedagogia jocului", bool withAn2 = true)
    {
        var workbook = new TimetableWorkbook();

        var an1 = workbook.Sheet("An 1");
        an1.Merged(3, "C", 3, "H", "Seria 1 (X)").Groups(4, "C", "1.1", "1.2", "2.1", "2.2", "3.1", "3.2");
        if (withSeries2)
        {
            an1.Merged(3, "K", 3, "L", "Seria 2 (Y)").Groups(4, "K", "1.1", "1.2");
        }

        // A course for the whole series, written over two cells of the same width: name, then room and weeks.
        an1.Activity(0, 8, "C", "Geometria fractalilor (c)  A. Testescu", hours: 1, lastColumn: "H")
            .Activity(0, 9, "C", "saptamanile 1-10, sala K210 - NORD", hours: 1, lastColumn: "H");
        an1.Activity(0, 10, "C", "Logica senzorilor virtuali (c)  B. Probescu\nsala AULA", lastColumn: "H");
        an1.Activity(1, 8, "C", "Skipped (c) by the overrides", lastColumn: "H");
        // Two activities in one cell, one per line.
        an1.Activity(1, 12, "C", "Esperanto (s) E. Inventatu - sala Q310/ Q311\nVolapuk (s) F. Imaginaru", lastColumn: "H");
        // Text in group columns that isn't an activity: a note, with a warning.
        an1.Activity(1, 16, "C", "ora de consultatii", hours: 1, lastColumn: "H");
        // A course written without its mark, made an activity by the overrides.
        an1.Activity(1, 18, "C", "Teh.avans. moderne 707CP\nG. Plasmuitu", lastColumn: "H");
        // Labs by abbreviation, one subgroup each.
        an1.Activity(2, 8, "D", "LSV (l) \nR426").Activity(2, 8, "E", "GEO (s) sala Q205");
        an1.Activity(2, 10, "F", "XYZ (l) R999");
        // Seminar every other week, for a whole group.
        an1.Activity(2, 12, "G", "LSV (s) s. 1-7 s. impara, sala Q110", lastColumn: "H");
        // A course whose override's text guard doesn't match: the override is ignored.
        an1.Activity(3, 8, "C", "Cromodinamica cuantica (c) C. Exemplaru\nsala AULA", lastColumn: "H");
        // Weeks that come to none.
        an1.Activity(3, 14, "C", "Termodinamica (c) sapt 15-16", lastColumn: "H");
        if (withSeries2)
        {
            // Two labs alternating in one column: two hours every other week each.
            an1.Activity(2, 8, "K", "Optica (l) L217", hours: 1).Activity(2, 9, "K", "Acustica (l) L302", hours: 1);
            // One lab alternating between two subgroups in the same room.
            an1.Activity(3, 8, "L", "Optica (l) L217", hours: 1).Activity(3, 9, "K", "Optica (l) L217", hours: 1);
            // A one-hour lab with nothing in its block's other row: as written.
            an1.Activity(4, 8, "K", "Acustica (l) L302", hours: 1);
            // A one-hour lab fixed in the overrides.
            an1.Activity(4, 10, "L", "Optica (l) L217", hours: 1);
        }

        // Notes: sport for the series, a footnote below the grid.
        an1.Activity(4, 20, "C", "*** SPORT", hours: 1, lastColumn: "H");
        an1.Text(an1.LastGridRow + 3, "C", "Disciplina SPORT se alege la inceputul semestrului");
        // A facultative subject in column Q: the subject, then its times below.
        an1.Activity(0, 14, "Q", $"{facultativeName} (s) D. Fictivu", hours: 1).Activity(0, 15, "Q", facultativeTimes, hours: 1);

        if (withAn2)
        {
            // Abbreviation rules beyond plain initials, and typos.
            var an2 = workbook.Sheet("An 2");
            an2.Merged(3, "C", 3, "D", "SISTEME ROBOTICE").Groups(4, "C", "1.1", "1.2");
            an2.Activity(1, 8, "C", "Programare Kotlin - PK (c) I. Sperantu\nsala AULA", lastColumn: "D");
            an2.Activity(1, 10, "C", "PK (l) R012");
            an2.Activity(1, 12, "C", "Teoria nodurilor 1 (c) J. Visatoru\nsala Q106", lastColumn: "D");
            an2.Activity(1, 14, "D", "TN (l) R014");
            an2.Activity(3, 8, "C", "Hidrostatica (c) K. Nascocitu\nsala Q117", lastColumn: "D");
            an2.Activity(3, 10, "C", "HS (l) L302");
            an2.Activity(3, 12, "C", "Analiza spectrala si topologie (c) L. Iluzoriu\nsala Q110", lastColumn: "D");
            an2.Activity(3, 14, "C", "Analiza spectrala si topolgoie (s) sala Q110", lastColumn: "D");
            an2.Activity(4, 8, "C", "Proiect integrator 1 (c) M. Fantezie\nsala Q120", lastColumn: "D");
            an2.Activity(4, 10, "C", "Proiect integrator 2 (c) M. Fantezie\nsala Q120", lastColumn: "D");
            // A facultative subject with no time written: its grid position is its time.
            an2.Activity(1, 16, "Q", "Retorica 2 (s)\nsala NORD-N301");
        }

        var an3 = workbook.Sheet("An 3");
        an3.Merged(3, "C", 3, "D", "SISTEME ROBOTICE").Merged(3, "J", 3, "K", "SISTEME SUBACVATICE")
            .Groups(4, "C", "1.1", "1.2").Groups(4, "J", "1.1", "1.2");
        an3.Activity(0, 8, "C", "Integrarea si testarea senzorilor robotici (c) H. Fabulosu\nsapt 1-10 Q109", lastColumn: "K");
        an3.Activity(1, 8, "P", "ITSR (l)   [R+E]   sala R521   Marti 10-12\nsala R520   Luni 16-18,   Joi 8-10, 10-12", hours: 4);
        // An optional course whose groups aren't the timetable's: its classes are offers to the whole year.
        an3.Activity(2, 8, "C", "VIZ (l) R012").Activity(2, 8, "D", "VIZ (l) R012").Activity(2, 8, "J", "VIZ (l) R012");

        var master = workbook.Sheet("An 1 MDATA", firstGridRow: 3);
        master.Activity(4, 10, "C", "Fundamente de orbitare (c) L. Iluzoriu\nroom R419", lastColumn: "D");
        master.Activity(4, 10, "E", "FO (l) R419", hours: 1).Activity(4, 11, "E", "Friday 14-16, 16-18", hours: 1);
        master.Activity(4, 10, "F", "Observatii: laboratoarele incep in saptamana 2", hours: 1);

        workbook.Sheet("Not configured");
        workbook.Sheet("Legenda");
        return workbook;
    }

    /// <summary>Parses <paramref name="workbook"/> (the caller disposes it) or the default workbook as source orar.</summary>
    internal static ParsedTimetable Parse(string overrides = Overrides, TimetableWorkbook? workbook = null)
    {
        if (workbook is not null)
        {
            return new SheetParser(ImportOverrides.Parse(overrides)).Parse("orar", "orar.xlsx", workbook.Grids());
        }

        using var built = Workbook();
        return new SheetParser(ImportOverrides.Parse(overrides)).Parse("orar", "orar.xlsx", built.Grids());
    }

    private static ParsedSlot Slot(ParsedTimetable parsed, string cell) => Assert.Single(parsed.Slots, s => s.SourceCell.StartsWith(cell, StringComparison.Ordinal));

    [Fact]
    public void A_course_over_two_cells_is_one_slot_for_every_subgroup_it_covers()
    {
        var parsed = Parse();

        var course = Slot(parsed, "An 1!C5");
        Assert.Equal("An 1!C5:H5", course.SourceCell);
        Assert.Equal(("Geometria fractalilor", DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(10, 0)),
            (course.Subject.Name, course.Weekday, course.StartsAt, course.EndsAt));
        Assert.Equal(ActivityKinds.Course, course.Kinds);
        Assert.Equal("1-10", course.Weeks.ToString());
        Assert.Equal("K210", course.Room);
        Assert.Equal("A. Testescu", course.Teacher);
        Assert.Equal(["1.1", "1.2", "2.1", "2.2", "3.1", "3.2"], course.Groups.Select(g => g.Label));
        Assert.All(course.Groups, g => Assert.Equal(("ALFA", 1, "X"), (g.Programme, g.Year, g.Series)));
    }

    [Fact]
    public void Abbreviations_resolve_to_the_sheets_courses_by_initials_or_an_alias()
    {
        var parsed = Parse();

        var lab = Slot(parsed, "An 1!D33");
        Assert.Equal(("Logica senzorilor virtuali", ActivityKinds.Lab, "R426"), (lab.Subject.Name, lab.Kinds, lab.Room));
        Assert.Equal(["1.2"], lab.Groups.Select(g => g.Label));
        Assert.Equal("LSV", parsed.Subjects[lab.Subject].ShortName);
        Assert.Equal("Geometria fractalilor", Slot(parsed, "An 1!E33").Subject.Name);

        Assert.Equal("XYZ", Slot(parsed, "An 1!F35").Subject.Name);
        Assert.Contains(parsed.Warnings, w => w.StartsWith("An 1!F35:F36: abbreviation \"XYZ\" matches no course", StringComparison.Ordinal));
    }

    [Fact]
    public void A_course_naming_its_own_abbreviation_a_trailing_number_and_a_guess_resolve_abbreviations()
    {
        var parsed = Parse();
        string NameAt(string cell) => Slot(parsed, cell).Subject.Name;

        Assert.Equal("Programare Kotlin", NameAt("An 2!C19"));
        Assert.Equal("Programare Kotlin", NameAt("An 2!C21"));
        Assert.Equal("Teoria nodurilor 1", NameAt("An 2!D25"));
        Assert.Equal("Hidrostatica", NameAt("An 2!C49"));
        Assert.Contains(parsed.Warnings, w => w.StartsWith("An 2!C49:C50: guessed abbreviation \"HS\" = \"Hidrostatica\"", StringComparison.Ordinal));
        Assert.Equal("Analiza spectrala si topologie", NameAt("An 2!C53"));
        Assert.Contains(parsed.Warnings, w => w.Contains("read \"Analiza spectrala si topolgoie\" as \"Analiza spectrala si topologie\"", StringComparison.Ordinal));
        Assert.DoesNotContain(parsed.Warnings, w => w.StartsWith("An 2!", StringComparison.Ordinal) && w.Contains("matches no course", StringComparison.Ordinal));
    }

    [Fact]
    public void Names_that_differ_only_in_a_number_are_different_subjects_not_a_typo()
    {
        var parsed = Parse();

        Assert.Equal(("Proiect integrator 1", "Proiect integrator 2"), (Slot(parsed, "An 2!C61").Subject.Name, Slot(parsed, "An 2!C63").Subject.Name));
        Assert.DoesNotContain(parsed.Warnings, w => w.Contains("Proiect integrator", StringComparison.Ordinal));
    }

    [Fact]
    public void Weeks_and_parity_narrow_a_slot_and_a_merged_seminar_covers_the_whole_group()
    {
        var seminar = Slot(Parse(), "An 1!G37");

        Assert.Equal("1, 3, 5, 7", seminar.Weeks.ToString());
        Assert.Equal(("Q110", ActivityKinds.Seminar), (seminar.Room, seminar.Kinds));
        Assert.Equal(["3.1", "3.2"], seminar.Groups.Select(g => g.Label));
    }

    [Fact]
    public void One_hour_cells_alternating_in_a_column_are_two_hours_top_odd_bottom_even()
    {
        var parsed = Parse();

        var top = Slot(parsed, "An 1!K33");
        var bottom = Slot(parsed, "An 1!K34");
        Assert.Equal(("Optica", new TimeOnly(8, 0), new TimeOnly(10, 0), "1, 3, 5, 7, 9, 11, 13"),
            (top.Subject.Name, top.StartsAt, top.EndsAt, top.Weeks.ToString()));
        Assert.Equal(("Acustica", new TimeOnly(8, 0), new TimeOnly(10, 0), "2, 4, 6, 8, 10, 12, 14"),
            (bottom.Subject.Name, bottom.StartsAt, bottom.EndsAt, bottom.Weeks.ToString()));
        Assert.Contains(parsed.Warnings, w => w.StartsWith("An 1!K33: one hour alternating with An 1!K34: read as 2 h, 08:00-10:00, odd weeks (top row)", StringComparison.Ordinal));
        Assert.Contains(parsed.Warnings, w => w.StartsWith("An 1!K34: one hour alternating with An 1!K33: read as 2 h, 08:00-10:00, even weeks (bottom row)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_lab_alternating_between_subgroups_in_one_room_is_two_hours_for_each()
    {
        var parsed = Parse();

        var top = Slot(parsed, "An 1!L47");
        var bottom = Slot(parsed, "An 1!K48");
        Assert.Equal((new TimeOnly(8, 0), new TimeOnly(10, 0), true), (top.StartsAt, top.EndsAt, top.Weeks.Contains(1)));
        Assert.Equal((new TimeOnly(8, 0), new TimeOnly(10, 0), true), (bottom.StartsAt, bottom.EndsAt, bottom.Weeks.Contains(2)));
        Assert.Equal(["1.2"], top.Groups.Select(g => g.Label));
        Assert.Equal(["1.1"], bottom.Groups.Select(g => g.Label));
    }

    [Fact]
    public void A_sheet_can_say_its_top_row_is_even_weeks()
    {
        var parsed = Parse(Overrides.Replace("""
                aliases: { GEO: "Geometria fractalilor", NOPE: "Nimic" }
            """, """
                aliases: { GEO: "Geometria fractalilor", NOPE: "Nimic" }
                topRowWeeks: even
            """));

        Assert.Equal("2, 4, 6, 8, 10, 12, 14", Slot(parsed, "An 1!K33").Weeks.ToString());
        Assert.Equal("1, 3, 5, 7, 9, 11, 13", Slot(parsed, "An 1!K34").Weeks.ToString());
    }

    [Fact]
    public void A_one_hour_cell_with_no_partner_is_as_written_and_one_fixed_in_the_overrides_is_as_fixed()
    {
        var parsed = Parse();

        var lone = Slot(parsed, "An 1!K61");
        Assert.Equal((new TimeOnly(8, 0), new TimeOnly(9, 0), 14), (lone.StartsAt, lone.EndsAt, lone.Weeks.Weeks().Count));
        Assert.Contains(parsed.Warnings, w => w.StartsWith("An 1!K61: one hour (Acustica (l)), alternating with nothing in its block", StringComparison.Ordinal));

        var fixedUp = Slot(parsed, "An 1!L63");
        Assert.Equal((new TimeOnly(10, 0), new TimeOnly(12, 0), "2, 4, 6, 8, 10, 12, 14"), (fixedUp.StartsAt, fixedUp.EndsAt, fixedUp.Weeks.ToString()));
        Assert.DoesNotContain(parsed.Warnings, w => w.StartsWith("An 1!L63", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_marks_on_separate_lines_are_two_activities()
    {
        var parsed = Parse();

        Assert.Equal(["Esperanto", "Volapuk"], parsed.Slots.Where(s => s.SourceCell.StartsWith("An 1!C23", StringComparison.Ordinal)).Select(s => s.Subject.Name));
        Assert.Contains(parsed.Warnings, w => w.StartsWith("An 1!C23:H24: 2 activities in one cell (one per line)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_cell_without_a_mark_in_group_columns_is_a_warned_note_unless_the_overrides_give_it_a_kind()
    {
        var parsed = Parse();

        Assert.Contains(parsed.Notes, n => n.Text == "ora de consultatii");
        Assert.Contains(parsed.Warnings, w => w.StartsWith("An 1!C27:H27: in a group column but no activity mark: kept as a note", StringComparison.Ordinal));

        var course = Slot(parsed, "An 1!C29");
        Assert.Equal(("Teh.avans. moderne", ActivityKinds.Course, "707CP", "G. Plasmuitu"), (course.Subject.Name, course.Kinds, course.Room, course.Teacher));
    }

    [Fact]
    public void Overrides_that_match_nothing_or_whose_text_guard_fails_are_warned()
    {
        var parsed = Parse();

        Assert.Contains(parsed.Warnings, w => w.StartsWith("An 1!C47:H48: overrides: cells.\"An 1!C47\" expects text starting \"Nu e asta\"", StringComparison.Ordinal));
        Assert.Equal("Cromodinamica cuantica", Slot(parsed, "An 1!C47").Subject.Name); // the override was ignored
        Assert.Contains("overrides: cells.\"An 1!Z99\" matches no cell that has text", parsed.Warnings);
        Assert.Contains("An 1: overrides: sheets.\"An 1\".aliases.\"NOPE\" matched nothing", parsed.Warnings);
    }

    [Fact]
    public void Weeks_that_come_to_none_skip_the_activity_with_a_warning()
    {
        var parsed = Parse();

        Assert.DoesNotContain(parsed.Slots, s => s.Subject.Name == "Termodinamica");
        Assert.Contains(parsed.Warnings, w => w.StartsWith("An 1!C53:H54: Termodinamica: its weeks come to none", StringComparison.Ordinal));
    }

    [Fact]
    public void Skipped_cells_and_sheets_leave_nothing_and_an_unknown_sheet_warns()
    {
        var parsed = Parse();

        Assert.DoesNotContain(parsed.Slots, s => s.SourceCell.StartsWith("An 1!C19", StringComparison.Ordinal));
        Assert.DoesNotContain(parsed.Notes, n => n.Text.Contains("Skipped", StringComparison.Ordinal));
        Assert.Contains("Not configured: not in the overrides file (sheets), skipped", parsed.Warnings);
        Assert.DoesNotContain(parsed.Warnings, w => w.StartsWith("Legenda", StringComparison.Ordinal));
    }

    [Fact]
    public void Text_that_is_not_an_activity_becomes_a_note_for_its_programme_and_year()
    {
        var notes = Parse().Notes.Where(n => n.Programme == "ALFA").Select(n => n.Text).ToList();

        Assert.Contains("*** SPORT", notes);
        Assert.Contains("Disciplina SPORT se alege la inceputul semestrului", notes);
    }

    [Fact]
    public void A_facultative_column_holds_optional_subjects_with_their_offered_slots()
    {
        var parsed = Parse();

        var offer = Assert.Single(parsed.Offers, o => o.Subject.Name == "Pedagogia jocului");
        Assert.Equal((DayOfWeek.Monday, new TimeOnly(14, 0), new TimeOnly(16, 0), "Q202"), (offer.Weekday, offer.StartsAt, offer.EndsAt, offer.Room));
        Assert.Equal(ActivityKinds.Seminar | ActivityKinds.Facultative, offer.Kinds);
        Assert.Equal(SubjectChoice.Optional, parsed.Subjects[offer.Subject].Choice);
    }

    [Fact]
    public void An_optional_subject_with_no_time_written_is_offered_at_its_grid_position()
    {
        var parsed = Parse();

        var offer = Assert.Single(parsed.Offers, o => o.Subject.Name == "Retorica 2");
        Assert.Equal((DayOfWeek.Tuesday, new TimeOnly(16, 0), new TimeOnly(18, 0), "NORD-N301"), (offer.Weekday, offer.StartsAt, offer.EndsAt, offer.Room));
        Assert.Contains(parsed.Warnings, w => w.StartsWith("An 2!Q27:Q28: Retorica 2: offered with no day and time written; placed at its grid position", StringComparison.Ordinal));
    }

    [Fact]
    public void An_optional_lab_is_offered_to_the_programmes_its_tags_name_and_makes_its_subject_optional()
    {
        var parsed = Parse();

        foreach (var programme in new[] { "ROB", "SUB" })
        {
            var subject = new SubjectKey(programme, 3, "Integrarea si testarea senzorilor robotici");
            Assert.Equal(SubjectChoice.Optional, parsed.Subjects[subject].Choice);
            Assert.Equal(
                ["Monday 16 R520", "Thursday 10 R520", "Thursday 8 R520", "Tuesday 10 R521"],
                parsed.Offers.Where(o => o.Subject == subject).Select(o => $"{o.Weekday} {o.StartsAt.Hour} {o.Room}").Order(StringComparer.Ordinal));
            // The course, merged across both specialisations, is one slot per programme.
            Assert.Single(parsed.Slots, s => s.Subject == subject && s.Kinds == ActivityKinds.Course);
        }

        Assert.Contains(parsed.Warnings, w => w.Contains("Integrarea si testarea senzorilor robotici has classes on the grid and is offered here as optional", StringComparison.Ordinal));
    }

    [Fact]
    public void A_required_subject_offered_with_the_optional_ones_stays_required_and_its_offers_are_times_to_pick()
    {
        var parsed = Parse(Overrides.Replace("optionalSubjects: [\"Vizualizare stiintifica\"]",
            "optionalSubjects: [\"Vizualizare stiintifica\"]\n    requiredSubjects: [\"Integrarea si testarea senzorilor robotici\", \"Nimic\"]"));

        foreach (var programme in new[] { "ROB", "SUB" })
        {
            var subject = new SubjectKey(programme, 3, "Integrarea si testarea senzorilor robotici");
            Assert.Equal(SubjectChoice.PickSlot, parsed.Subjects[subject].Choice);
            Assert.Equal(4, parsed.Offers.Count(o => o.Subject == subject && !o.Kinds.HasFlag(ActivityKinds.Facultative)));
            Assert.Single(parsed.Slots, s => s.Subject == subject && s.Kinds == ActivityKinds.Course);
        }

        Assert.DoesNotContain(parsed.Warnings, w => w.Contains("Integrarea si testarea senzorilor robotici has classes on the grid", StringComparison.Ordinal));
        Assert.Contains("An 3: overrides: sheets.\"An 3\".requiredSubjects \"Nimic\" matched no subject", parsed.Warnings);
        Assert.Equal(SubjectChoice.Optional, parsed.Subjects[new SubjectKey("ROB", 3, "Vizualizare stiintifica")].Choice);
    }

    [Fact]
    public void An_optional_subjects_classes_on_the_grid_are_offers_to_the_whole_year_once_each()
    {
        var parsed = Parse();

        Assert.DoesNotContain(parsed.Slots, s => s.Subject.Name == "Vizualizare stiintifica");
        Assert.Equal(["ROB Wednesday 8 R012", "SUB Wednesday 8 R012"],
            parsed.Offers.Where(o => o.Subject.Name == "Vizualizare stiintifica").Select(o => $"{o.Subject.Programme} {o.Weekday} {o.StartsAt.Hour} {o.Room}")
                .Order(StringComparer.Ordinal));
        Assert.Equal(SubjectChoice.Optional, parsed.Subjects[new SubjectKey("ROB", 3, "Vizualizare stiintifica")].Choice);
    }

    [Fact]
    public void A_sheet_without_group_columns_is_one_group_whose_labs_are_picked_from_offered_times()
    {
        var parsed = Parse();

        var group = Assert.Single(parsed.Groups, g => g.Programme == "MDATA");
        Assert.Equal(("1", 1), (group.Label, group.Year));
        var subject = new SubjectKey("MDATA", 1, "Fundamente de orbitare");
        Assert.Equal(SubjectChoice.PickSlot, parsed.Subjects[subject].Choice);
        Assert.Equal(["14 R419", "16 R419"], parsed.Offers.Where(o => o.Subject == subject).Select(o => $"{o.StartsAt.Hour} {o.Room}"));
        Assert.Equal([group], Assert.Single(parsed.Slots, s => s.Subject == subject).Groups);
        Assert.Contains(parsed.Notes, n => n.Programme == "MDATA" && n.Text == "Observatii: laboratoarele incep in saptamana 2");
    }

    [Fact]
    public void A_group_label_the_overrides_do_not_map_skips_its_columns_with_a_warning()
    {
        var parsed = Parse(Overrides.Replace("\"Seria 2 (Y)\": { programme: ALFA, series: Y }", "\"Other\": { programme: X }"));

        Assert.DoesNotContain(parsed.Groups, g => g.Series == "Y");
        Assert.Contains(parsed.Warnings, w => w.Contains("group column under \"Seria 2 (Y)\"", StringComparison.Ordinal));
    }

    [Fact]
    public void A_listed_sheet_the_workbook_lacks_is_reported()
    {
        using var workbook = Workbook(withAn2: false);

        Assert.Equal(["An 2"], Parse(workbook: workbook).MissingSheets);
    }

    [Fact]
    public void Day_blocks_are_the_days_column_A_names_and_unreadable_names_with_other_than_five_blocks_are_an_error()
    {
        var overrides = Overrides.Replace("orar: { sheets: [\"An 1\", \"An 2\", \"An 3\", \"An 1 MDATA\"] }", "orar: { sheets: [\"An 1 MDATA\"] }");
        using (var relabelled = new TimetableWorkbook())
        {
            relabelled.Sheet("An 1 MDATA", firstGridRow: 3, days: ["MARTI", "LUNI", "MIERCURI", "JOI", "VINERI"])
                .Activity(0, 8, "C", "Fundamente de orbitare (c) L. Iluzoriu\nroom R419", lastColumn: "D");
            var parsed = Parse(overrides, relabelled);
            Assert.Equal(DayOfWeek.Tuesday, Assert.Single(parsed.Slots).Weekday);
        }

        using var fourDays = new TimetableWorkbook();
        fourDays.Sheet("An 1 MDATA", firstGridRow: 3, days: ["", "", "", ""]).Activity(0, 8, "C", "Fundamente de orbitare (c)", lastColumn: "D");
        var broken = Parse(overrides, fourDays);
        Assert.Equal(["An 1 MDATA: 4 day blocks, and their names in column A don't say which days they are"], broken.Errors);
        Assert.Empty(broken.Slots);
    }

    [Fact]
    public void The_calendar_skips_the_weeks_of_a_break()
    {
        var weeks = ImportOverrides.Parse(Overrides).Calendar.WeekStarts();

        Assert.Equal(14, weeks.Count);
        Assert.Equal(new DateOnly(2030, 12, 16), weeks[11]);
        Assert.Equal(new DateOnly(2031, 1, 6), weeks[12]);
    }

    private const string Breaks = """breaks: [ { from: "2030-12-23", to: "2031-01-05", name: { ro: "Vacanta de iarna", en: "Winter break" } } ]""";

    [Fact]
    public void A_break_inside_a_teaching_week_makes_its_days_non_teaching_under_its_name()
    {
        var calendar = ImportOverrides.Parse(Overrides.Replace(Breaks, Breaks[..^2]
            + """, { from: "2030-11-13", to: "2030-11-15" }, { from: "2030-10-31", to: "2030-11-01", name: "Pod" } ]""")).Calendar;

        Assert.Equal(new DateOnly(2030, 11, 11), calendar.WeekStarts()[6]); // week 7 still counts
        Assert.Equal([new DateOnly(2030, 11, 13), new DateOnly(2030, 11, 14), new DateOnly(2030, 11, 15)],
            calendar.Days().Where(d => d.Reason == CalendarOverrides.UnnamedBreak).Select(d => d.Date));
        Assert.Equal([new DateOnly(2030, 10, 31), new DateOnly(2030, 11, 1)],
            calendar.Days().Where(d => d.Reason == new CalendarName("Pod", null)).Select(d => d.Date));
        Assert.Equal(["2030-12-23 Winter break", "2030-11-13 -", "2030-10-31 Pod"],
            calendar.BreakDates().Select(b => $"{b.From:yyyy-MM-dd} {b.Name?.En ?? b.Name?.Ro ?? "-"}"));
    }

    [Fact]
    public void Calendar_names_come_in_both_languages_and_a_plain_string_is_Romanian_with_a_warning()
    {
        var both = ImportOverrides.Parse(Overrides);
        Assert.Equal((new DateOnly(2030, 12, 2), new CalendarName("Zi libera", "Day off")), Assert.Single(both.Calendar.Days()));
        Assert.Equal(new CalendarName("Vacanta de iarna", "Winter break"), Assert.Single(both.Calendar.BreakDates()).Name);
        Assert.DoesNotContain(both.Warnings(), w => w.Contains("English name", StringComparison.Ordinal));

        var plain = ImportOverrides.Parse(Overrides.Replace("""reason: { ro: "Zi libera", en: "Day off" }""", """reason: "Zi libera" """)
            .Replace("""name: { ro: "Vacanta de iarna", en: "Winter break" }""", """name: { ro: "Vacanta de iarna" }"""));
        Assert.Equal(new CalendarName("Zi libera", null), Assert.Single(plain.Calendar.Days()).Reason);
        Assert.Contains("overrides: calendar.nonTeachingDays 2030-12-02: reason has no English name, so the English UI shows the Romanian "
            + "one; write it as { ro: ..., en: ... }", plain.Warnings());
        Assert.Contains(plain.Warnings(), w => w.StartsWith("overrides: calendar.breaks 2030-12-23: name has no English name", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""reason: { ro: "Zi libera", en: "Day off" }""", "reason: ~", "calendar.nonTeachingDays 2030-12-02: reason needs a Romanian name")]
    [InlineData("""reason: { ro: "Zi libera", en: "Day off" }""", """reason: { en: "Day off" }""", "reason needs a Romanian name")]
    [InlineData("""to: "2031-01-05", name""", """to: "2030-12-01", name""", "calendar.breaks 2030-12-23: it ends (2030-12-01) before it starts")]
    [InlineData("""name: { ro: "Vacanta de iarna", en: "Winter break" }""", """name: { ro: " ", en: "Winter break" }""", "name needs a Romanian name")]
    public void Bad_calendar_names_are_refused(string from, string to, string message)
    {
        Assert.Contains(from, Overrides);
        var error = Assert.Throws<FormatException>(() => ImportOverrides.Parse(Overrides.Replace(from, to)));
        Assert.Contains(message, error.Message);
    }

    [Theory]
    [InlineData("""reason: { ro: "Zi libera", fr: "Jour" }""")]
    [InlineData("""reason: { ro: [ "Zi libera" ] }""")]
    public void A_calendar_name_with_another_key_or_shape_is_refused(string reason) =>
        Assert.Throws<YamlDotNet.Core.YamlException>(() => ImportOverrides.Parse(Overrides.Replace("""reason: { ro: "Zi libera", en: "Day off" }""", reason)));

    [Fact]
    public void A_name_without_English_is_served_in_Romanian_for_both() =>
        Assert.Equal(new CalendarNameResponse("Zi libera", "Zi libera"), CalendarNameResponse.Of(new CalendarName("Zi libera", null)));

    [Theory]
    [InlineData("firstWeekStartsOn: \"2030-09-30\"", "firstWeekStartsOn: \"2030-10-01\"")]
    [InlineData("number: 1", "number: 3")]
    [InlineData("columnRoles: { Q: facultative }", "columnRoles: { Q: maybe }")]
    [InlineData("omega: { sheets: [\"Omega 4\"] }", "omega: { sheets: [\"An 1\"] }")]
    [InlineData("omega: { sheets: [\"Omega 4\"] }", "omega: { sheets: [\"No such sheet\"] }")]
    [InlineData("kind: course", "kind: lecture")]
    [InlineData("optionalSubjects: [\"Vizualizare stiintifica\"]",
        "optionalSubjects: [\"Vizualizare stiintifica\"]\n    requiredSubjects: [\"Vizualizare Stiintifica\"]")]
    public void Bad_overrides_are_refused(string from, string to) =>
        Assert.ThrowsAny<Exception>(() => ImportOverrides.Parse(Overrides.Replace(from, to)));

    [Fact]
    public void An_unknown_overrides_key_is_refused_not_ignored() =>
        Assert.ThrowsAny<YamlDotNet.Core.YamlException>(() => ImportOverrides.Parse(Overrides + "\nsheetz: {}\n"));

    [Fact]
    public void A_workbook_is_its_source_by_its_sheets_never_its_file_name()
    {
        var overrides = ImportOverrides.Parse(Overrides);

        Assert.Equal("orar", overrides.SourceOf(["An 1", "An 3", "Legenda"], out _));
        Assert.Null(overrides.SourceOf(["An 1", "Omega 4"], out var several));
        Assert.Contains("several sources", several);
        Assert.Null(overrides.SourceOf(["Something"], out var none));
        Assert.Contains("none of its sheets", none);
    }

    [Fact]
    public void Campus_Virtual_courses_go_on_their_subjects_by_name_and_the_printout_counts_those_without()
    {
        var parsed = Parse(Overrides + "\n" + CampusVirtualSection);

        Assert.Equal(4101, parsed.Subjects[new SubjectKey("ALFA", 1, "Geometria fractalilor")].CampusVirtualCourseId);
        Assert.Equal(4102, parsed.Subjects[new SubjectKey("ALFA", 1, "Pedagogia jocului")].CampusVirtualCourseId);
        Assert.Null(parsed.Subjects[new SubjectKey("ALFA", 1, "Logica senzorilor virtuali")].CampusVirtualCourseId);
        Assert.Contains("overrides: campusVirtual.courses.ALFA.1.\"Nimic de acest fel\" matches no subject", parsed.Warnings);

        var output = new StringWriter();
        TimetableImporter.PrintDiff(TimetableImporter.Snapshot.Empty, new ImportFile("orar", "orar.xlsx", "-", parsed), output);
        var text = output.ToString();
        Assert.Contains($"-- Campus Virtual: 2 of {parsed.Subjects.Count} subjects have a course, {parsed.Subjects.Count - 2} have none", text);
        Assert.Contains("   ? ALFA y1 Logica senzorilor virtuali\n", text.ReplaceLineEndings("\n"));
        Assert.Contains("   + ALFA y1 Geometria fractalilor [Fixed] cv:4101\n", text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Without_Campus_Virtual_courses_nothing_is_linked_or_counted()
    {
        var parsed = Parse();

        Assert.False(parsed.CampusVirtualListed);
        Assert.All(parsed.Subjects.Values, s => Assert.Null(s.CampusVirtualCourseId));
    }

    [Theory]
    [InlineData("{ courses: { ZETA: { 1: { \"Geometria fractalilor\": { id: 1 } } } } }", "names programme ZETA, which programmes doesn't list")]
    [InlineData("{ codes: { ZETA: [ZETA] } }", "names programme ZETA")]
    [InlineData("{ codes: { ALFA: [\"AL-FA\"] } }", "campusVirtual.codes \"AL-FA\" isn't a programme code")]
    [InlineData("{ search: [\"S1-X-FAC-\"] }", "campusVirtual.search \"S1-X-FAC-\" isn't a shortname prefix")]
    [InlineData("{ search: [\"S1 X\"] }", "campusVirtual.search \"S1 X\" isn't a shortname prefix")]
    [InlineData("{ courses: { ALFA: { 9: { \"Geometria fractalilor\": { id: 1 } } } } }", "campusVirtual.courses.ALFA: year 9 isn't 1-6")]
    [InlineData("{ courses: { ALFA: { 1: { \"Geometria fractalilor\": { id: 0 } } } } }", "needs a course id above 0")]
    [InlineData("{ courses: { ALFA: { 1: { \"Geometria fractalilor\": { shortname: \"S1-X\" } } } } }", "needs a course id above 0")]
    [InlineData("{ courses: { ALFA: { 1: { \"Geometria fractalilor\": { id: 1 }, \"GEOMETRIA FRACTALILOR\": { id: 2 } } } } }", "is listed twice")]
    public void Bad_campus_virtual_sections_are_refused(string section, string message)
    {
        var error = Assert.Throws<FormatException>(() => ImportOverrides.Parse(Overrides + "\ncampusVirtual: " + section));
        Assert.Contains(message, error.Message);
    }

    [Theory]
    [InlineData("{ courses: { ALFA: { 1: { \"Geometria fractalilor\": { id: 1, url: \"x\" } } } } }")]
    [InlineData("{ links: {} }")]
    public void Unknown_campus_virtual_keys_are_refused(string section) =>
        Assert.Throws<YamlDotNet.Core.YamlException>(() => ImportOverrides.Parse(Overrides + "\ncampusVirtual: " + section));

    /// <summary>
    /// <see cref="SheetParser.Version"/> decides whether an unchanged workbook is imported again. This pins it
    /// to what the parser reads from the test workbook: a change in what it reads changes the hash and fails
    /// here until Version is bumped (and the hash updated), so a parser change can't keep the skip check.
    /// </summary>
    [Fact]
    public void The_parser_version_moves_with_what_the_parser_reads()
    {
        using var workbook = Workbook();
        var output = new StringWriter();
        TimetableImporter.PrintDiff(TimetableImporter.Snapshot.Empty, new ImportFile("orar", "orar.xlsx", "-", Parse(workbook: workbook)), output);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(output.ToString().ReplaceLineEndings("\n"))));

        const string ReadByVersion2 = "1d30a6b8057aa275477ec557e656f4910e0fb4f2c387815d5e2f494fa1767cd1";
        Assert.True(SheetParser.Version == 2 && hash == ReadByVersion2,
            $"The parser now reads the test workbook differently (sha256 {hash}). Bump SheetParser.Version and pin the new hash here.");
    }
}

public sealed class ImportTimetableCommandTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("campuspulse-import-test-").FullName;

    [Theory]
    [InlineData]
    [InlineData("--overrides")]
    [InlineData("--overrides", "o.yaml")]
    [InlineData("--overrides", "o.yaml", "--dry-run", "--parse-only", "a.xlsx")]
    [InlineData("--overrides", "o.yaml", "--frobnicate", "a.xlsx")]
    [InlineData("--overrides", "o.yaml", "a.xlsx", "--subjects-json")]
    public async Task Usage_errors_exit_64(params string[] args)
    {
        var error = new StringWriter();

        Assert.Equal(64, await ImportTimetableCommand.RunAsync(args, TextWriter.Null, error));
        Assert.Contains("usage:", error.ToString());
    }

    private async Task<(string Overrides, string Workbook)> Files(string workbookName = "orar.xlsx")
    {
        var overrides = Path.Combine(_dir, "overrides.yaml");
        var workbookPath = Path.Combine(_dir, workbookName);
        await File.WriteAllTextAsync(overrides, TimetableParserTests.Overrides);
        using var workbook = TimetableParserTests.Workbook();
        await File.WriteAllBytesAsync(workbookPath, workbook.Bytes());
        return (overrides, workbookPath);
    }

    [Fact]
    public async Task Parse_only_prints_the_workbook_and_its_warnings_without_a_database()
    {
        var (overrides, workbook) = await Files("Orar (1).xlsx");

        var output = new StringWriter();
        var exit = await ImportTimetableCommand.RunAsync(["--overrides", overrides, "--parse-only", workbook], output, TextWriter.Null);

        Assert.Equal(0, exit);
        var text = output.ToString();
        Assert.Contains("== Orar (1).xlsx (source orar, sha256 ", text);
        Assert.Contains("+ ALFA y1 X 1.1 ALFA y1 X 1.2 ALFA y1 X 2.1 ALFA y1 X 2.2 ALFA y1 X 3.1 ALFA y1 X 3.2 | Monday 08:00-10:00 | Geometria fractalilor Course | room K210 | weeks 1-10 | A. Testescu", text);
        Assert.Contains("! An 1!K61: one hour", text);
        Assert.Contains("! overrides: sheets.\"Orfan\" is in no source, so nothing reads it", text);
        Assert.Contains("Parse only: no database was read or written.", text);
    }

    [Fact]
    public async Task Subjects_json_lists_the_subjects_read_and_the_campus_virtual_section_for_the_script()
    {
        var (_, workbook) = await Files();
        var overrides = Path.Combine(_dir, "with-campus-virtual.yaml");
        await File.WriteAllTextAsync(overrides, TimetableParserTests.Overrides + "\n" + TimetableParserTests.CampusVirtualSection);
        var json = Path.Combine(_dir, "subjects.json");

        var exit = await ImportTimetableCommand.RunAsync(["--overrides", overrides, "--parse-only", "--subjects-json", json, workbook],
            TextWriter.Null, TextWriter.Null);

        Assert.Equal(0, exit);
        using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(json));
        var root = document.RootElement;
        Assert.Equal(ImportTimetableCommand.Sha256(await File.ReadAllBytesAsync(overrides)), root.GetProperty("overridesSha256").GetString());
        Assert.Equal(["S1-X-FAC"], root.GetProperty("search").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["ALFA", "ALFAX"], root.GetProperty("codes").GetProperty("ALFA").EnumerateArray().Select(e => e.GetString()));
        var course = root.GetProperty("courses").GetProperty("ALFA").GetProperty("1").GetProperty("GEOMETRIA fractalilor");
        Assert.Equal((4101, "S1-X-FAC-ALFA1-GF", false),
            (course.GetProperty("id").GetInt32(), course.GetProperty("shortname").GetString(), course.GetProperty("hand").GetBoolean()));
        Assert.Contains("MDATA", root.GetProperty("programmes").EnumerateArray().Select(e => e.GetString()));
        var subjects = root.GetProperty("subjects").EnumerateArray().ToList();
        var lsv = Assert.Single(subjects, s => s.GetProperty("name").GetString() == "Logica senzorilor virtuali");
        Assert.Equal(("orar", "ALFA", 1, "LSV", "Fixed"), (lsv.GetProperty("source").GetString(), lsv.GetProperty("programme").GetString(),
            lsv.GetProperty("year").GetInt32(), lsv.GetProperty("shortName").GetString(), lsv.GetProperty("choice").GetString()));
    }

    [Fact]
    public async Task Subjects_json_is_not_written_when_a_sheet_has_errors()
    {
        var overrides = Path.Combine(_dir, "mdata-only.yaml");
        await File.WriteAllTextAsync(overrides, TimetableParserTests.Overrides.Replace(
            "orar: { sheets: [\"An 1\", \"An 2\", \"An 3\", \"An 1 MDATA\"] }", "orar: { sheets: [\"An 1 MDATA\"] }"));
        var workbook = Path.Combine(_dir, "four-days.xlsx");
        using (var fourDays = new TimetableWorkbook())
        {
            fourDays.Sheet("An 1 MDATA", firstGridRow: 3, days: ["", "", "", ""]).Activity(0, 8, "C", "Fundamente de orbitare (c)", lastColumn: "D");
            await File.WriteAllBytesAsync(workbook, fourDays.Bytes());
        }

        var json = Path.Combine(_dir, "subjects.json");
        var output = new StringWriter();

        Assert.Equal(1, await ImportTimetableCommand.RunAsync(["--overrides", overrides, "--parse-only", "--subjects-json", json, workbook],
            output, TextWriter.Null));
        Assert.False(File.Exists(json));
        Assert.Contains($"Subjects not written to {json}: a sheet has errors.", output.ToString());
    }

    [Fact]
    public async Task A_workbook_of_no_known_source_or_two_of_one_source_fail_with_exit_1()
    {
        var (overrides, workbook) = await Files();
        var other = Path.Combine(_dir, "other.xlsx");
        using (var unknown = new TimetableWorkbook())
        {
            unknown.Sheet("Something else");
            await File.WriteAllBytesAsync(other, unknown.Bytes());
        }

        var copy = Path.Combine(_dir, "orar copy.xlsx");
        File.Copy(workbook, copy);
        var error = new StringWriter();

        Assert.Equal(1, await ImportTimetableCommand.RunAsync(["--overrides", overrides, "--parse-only", other], TextWriter.Null, error));
        Assert.Contains("none of its sheets is in any of the overrides' sources", error.ToString());
        Assert.Equal(1, await ImportTimetableCommand.RunAsync(["--overrides", overrides, "--parse-only", workbook, copy], TextWriter.Null, error));
        Assert.Contains("are both source orar", error.ToString());
    }

    [Fact]
    public async Task A_missing_file_fails_with_exit_1()
    {
        var (overrides, _) = await Files();
        var error = new StringWriter();

        Assert.Equal(1, await ImportTimetableCommand.RunAsync(["--overrides", overrides, "--parse-only", Path.Combine(_dir, "none.xlsx")],
            TextWriter.Null, error));
        Assert.Contains("import-timetable failed", error.ToString());
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
