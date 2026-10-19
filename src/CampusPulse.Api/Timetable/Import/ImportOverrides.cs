using System.Globalization;
using System.Text.RegularExpressions;
using CampusPulse.Domain.Timetable;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CampusPulse.Api.Timetable.Import;

/// <summary>
/// What the import can't read from the faculty's files, in a private YAML file given by path (never
/// committed: it describes UPT-only files). Structure and an example: docs/timetable-import.md.
/// </summary>
public sealed class ImportOverrides
{
    public SemesterOverrides Semester { get; set; } = new();

    public CalendarOverrides Calendar { get; set; } = new();

    /// <summary>Programme code → its full name.</summary>
    public Dictionary<string, string> Programmes { get; set; } = [];

    /// <summary>
    /// The workbooks, by an id of ours → the sheets each one holds. A workbook is recognised by its sheets,
    /// never by its file name (which changes from one download to the next), and everything it
    /// contributes is kept under that id: importing "Timetable (1).xlsx" replaces what "Timetable.xlsx" brought.
    /// </summary>
    public Dictionary<string, SourceOverrides> Sources { get; set; } = [];

    /// <summary>Per sheet, by its name in the workbook. A sheet that isn't listed is skipped (a warning).</summary>
    public Dictionary<string, SheetOverrides> Sheets { get; set; } = [];

    /// <summary>Per cell, by <c>Sheet!A1</c> (the cell's top-left address): corrections the rules can't make.</summary>
    public Dictionary<string, CellOverride> Cells { get; set; } = [];

    /// <summary>Room code → what we know about it.</summary>
    public Dictionary<string, RoomOverride> Rooms { get; set; } = [];

    /// <summary>The subjects' Campus Virtual (Moodle) courses, written by scripts/campus-virtual-courses.py.</summary>
    public CampusVirtualOverrides CampusVirtual { get; set; } = new();

    public static ImportOverrides Parse(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new CalendarText.Converter())
            .Build();
        var overrides = deserializer.Deserialize<ImportOverrides?>(yaml) ?? new ImportOverrides();
        overrides.Validate();
        return overrides;
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Semester.AcademicYear) || Semester.Number is not (1 or 2))
        {
            throw new FormatException("overrides: semester.academicYear (e.g. 2026-2027) and semester.number (1 or 2) are required.");
        }

        _ = Calendar.WeekStarts();
        Calendar.ValidateNames();
        if (Sources.Count == 0)
        {
            throw new FormatException("overrides: sources (workbook id → its sheets) is required.");
        }

        var owner = new Dictionary<string, string>();
        foreach (var (id, source) in Sources)
        {
            if (source.Sheets.Count == 0)
            {
                throw new FormatException($"overrides: sources.{id} lists no sheets.");
            }

            foreach (var sheet in source.Sheets)
            {
                if (!Sheets.TryGetValue(sheet, out var config) || config.Skip)
                {
                    throw new FormatException($"overrides: sources.{id} lists \"{sheet}\", which sheets doesn't describe (or skips).");
                }

                if (!owner.TryAdd(sheet, id))
                {
                    throw new FormatException($"overrides: \"{sheet}\" is in both sources.{owner[sheet]} and sources.{id}.");
                }
            }
        }

        foreach (var (name, sheet) in Sheets)
        {
            if (!sheet.Skip && sheet.Year is < 1 or > 6)
            {
                throw new FormatException($"overrides: sheets.\"{name}\".year must be 1-6.");
            }

            foreach (var role in sheet.ColumnRoles.Values)
            {
                _ = ColumnRoleParse(role, name);
            }

            if (sheet.TopRowWeeks is not ("odd" or "even"))
            {
                throw new FormatException($"overrides: sheets.\"{name}\".topRowWeeks must be odd or even.");
            }

            if (sheet.RequiredSubjects.FirstOrDefault(r => sheet.OptionalSubjects.Any(o => CellText.NameKey(o) == CellText.NameKey(r))) is { } both)
            {
                throw new FormatException($"overrides: sheets.\"{name}\": \"{both}\" is in both optionalSubjects and requiredSubjects.");
            }
        }

        foreach (var (key, cell) in Cells)
        {
            if (cell.Kind is { } kind && KindsParse(kind) is null)
            {
                throw new FormatException($"overrides: cells.\"{key}\".kind \"{kind}\" isn't course, lab, seminar, project or a sum like lab+project.");
            }
        }

        CampusVirtual.Validate(Programmes);
    }

    /// <summary>What's odd about the overrides themselves: sheets that no source lists.</summary>
    public IEnumerable<string> Warnings() =>
        Sheets.Where(s => !s.Value.Skip && !Sources.Values.Any(source => source.Sheets.Contains(s.Key)))
            .Select(s => $"overrides: sheets.\"{s.Key}\" is in no source, so nothing reads it")
            .Concat(Calendar.NameWarnings());

    /// <summary>The source whose sheets the workbook has; null (with the problem) when it has none of any
    /// source's or sheets of several.</summary>
    public string? SourceOf(IReadOnlyCollection<string> sheetNames, out string? problem)
    {
        var matches = Sources.Where(s => s.Value.Sheets.Any(sheetNames.Contains)).Select(s => s.Key).ToList();
        problem = matches.Count switch
        {
            0 => "none of its sheets is in any of the overrides' sources",
            1 => null,
            _ => $"it has sheets of several sources ({string.Join(", ", matches)})",
        };
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>"course", "lab+project": the kinds a cell override gives a cell without a mark.</summary>
    internal static ActivityKinds? KindsParse(string kind)
    {
        var kinds = ActivityKinds.None;
        foreach (var part in kind.ToLowerInvariant().Split('+', StringSplitOptions.TrimEntries))
        {
            kinds |= part switch
            {
                "course" => ActivityKinds.Course,
                "lab" => ActivityKinds.Lab,
                "seminar" => ActivityKinds.Seminar,
                "project" => ActivityKinds.Project,
                "facultative" => ActivityKinds.Facultative,
                _ => (ActivityKinds)(-1),
            };
        }

        return kinds is ActivityKinds.None || (int)kinds < 0 ? null : kinds;
    }

    internal static ColumnRole ColumnRoleParse(string role, string sheet) => role.ToLowerInvariant() switch
    {
        "optional" => ColumnRole.Optional,
        "facultative" => ColumnRole.Facultative,
        "pickslot" => ColumnRole.PickSlot,
        "notes" => ColumnRole.Notes,
        "grid" => ColumnRole.Grid,
        _ => throw new FormatException($"overrides: sheets.\"{sheet}\".columnRoles: \"{role}\" is not optional, facultative, pickSlot, notes or grid."),
    };
}

/// <summary>
/// Campus Virtual (cv.upt.ro, UPT's Moodle): which course each subject has there, so a class can link to it.
/// scripts/campus-virtual-courses.py writes this section from the catalog (docs/timetable-import.md);
/// <see cref="Search"/> and <see cref="Codes"/> are its input, which the import doesn't use.
/// </summary>
public sealed partial class CampusVirtualOverrides
{
    private readonly Dictionary<SubjectKey, CampusVirtualCourse> _bySubject = [];

    /// <summary>The course shortname prefixes the script searches the catalog for, e.g. "S1-L-XY".</summary>
    public List<string> Search { get; set; } = [];

    /// <summary>Programme code → the codes Moodle shortnames use for it (by default the code without its
    /// punctuation): a programme whose courses partly sit under a common one lists both.</summary>
    public Dictionary<string, List<string>> Codes { get; set; } = [];

    /// <summary>Programme code → year → subject name (as the import reads it) → its course.</summary>
    public Dictionary<string, Dictionary<int, Dictionary<string, CampusVirtualCourse>>> Courses { get; set; } = [];

    /// <summary>The subject's course id, or null when it has none here (or a hand entry says none).</summary>
    public int? CourseOf(SubjectKey subject) => _bySubject.GetValueOrDefault(subject)?.Id;

    /// <summary>The entries, as the subjects they name.</summary>
    public IEnumerable<SubjectKey> Subjects => _bySubject.Keys;

    internal void Validate(IReadOnlyDictionary<string, string> programmes)
    {
        foreach (var programme in Codes.Keys.Concat(Courses.Keys).Where(p => !programmes.ContainsKey(p)))
        {
            throw new FormatException($"overrides: campusVirtual names programme {programme}, which programmes doesn't list.");
        }

        // The script reads a course's programme and year from the shortname part after the prefix: a stray dash or
        // space would shift it.
        if (Search.FirstOrDefault(p => !ShortnamePart().IsMatch(p)) is { } prefix)
        {
            throw new FormatException($"overrides: campusVirtual.search \"{prefix}\" isn't a shortname prefix like S1-X-FAC.");
        }

        if (Codes.SelectMany(c => c.Value).FirstOrDefault(c => !ShortnameCode().IsMatch(c)) is { } code)
        {
            throw new FormatException($"overrides: campusVirtual.codes \"{code}\" isn't a programme code of letters and digits.");
        }

        foreach (var (programme, years) in Courses)
        {
            foreach (var (year, subjects) in years)
            {
                if (year is < 1 or > 6)
                {
                    throw new FormatException($"overrides: campusVirtual.courses.{programme}: year {year} isn't 1-6.");
                }

                foreach (var (name, course) in subjects)
                {
                    var where = $"campusVirtual.courses.{programme}.{year}.\"{name}\"";
                    if (string.IsNullOrWhiteSpace(name) || course is null || (course.Id is null && !course.Hand) || course.Id is <= 0)
                    {
                        throw new FormatException($"overrides: {where} needs a course id above 0 (only a hand: true entry may have none).");
                    }

                    if (!_bySubject.TryAdd(new SubjectKey(programme, year, name), course))
                    {
                        throw new FormatException($"overrides: {where} is listed twice (names are compared ignoring case and diacritics).");
                    }
                }
            }
        }
    }

    [GeneratedRegex("^[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*$")]
    private static partial Regex ShortnamePart();

    [GeneratedRegex("^[A-Za-z0-9]+$")]
    private static partial Regex ShortnameCode();
}

/// <summary>A subject's Campus Virtual course.</summary>
public sealed class CampusVirtualCourse
{
    /// <summary>The Moodle course id: its page is /course/view.php?id=…</summary>
    public int? Id { get; set; }

    /// <summary>The course's Moodle shortname, for whoever reads the file; the import doesn't use it.</summary>
    public string? Shortname { get; set; }

    /// <summary>Set by hand rather than by the script, which keeps it as it is. A hand entry without an id says
    /// the subject has no course.</summary>
    public bool Hand { get; set; }
}

public sealed class SourceOverrides
{
    public List<string> Sheets { get; set; } = [];
}

public sealed class SemesterOverrides
{
    public string AcademicYear { get; set; } = "";

    public int Number { get; set; }
}

/// <summary>
/// The teaching weeks: week 1 starts on <see cref="FirstWeekStartsOn"/> (a Monday); each following
/// Monday starts the next week, except Mondays whose week (Monday to Friday) is inside a break.
/// </summary>
public sealed class CalendarOverrides
{
    public string FirstWeekStartsOn { get; set; } = "";

    public int Weeks { get; set; } = 14;

    public List<DateRange> Breaks { get; set; } = [];

    public List<NonTeachingDayOverride> NonTeachingDays { get; set; } = [];

    public IReadOnlyList<DateOnly> WeekStarts()
    {
        if (!DateOnly.TryParseExact(FirstWeekStartsOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var monday)
            || monday.DayOfWeek != DayOfWeek.Monday)
        {
            throw new FormatException("overrides: calendar.firstWeekStartsOn must be a Monday, as yyyy-MM-dd.");
        }

        if (Weeks is < 1 or > WeekSet.MaxWeeks)
        {
            throw new FormatException($"overrides: calendar.weeks must be 1-{WeekSet.MaxWeeks}.");
        }

        var breaks = Breaks.Select(b => (From: b.FromDate(), To: b.ToDate())).ToList();
        var starts = new List<DateOnly>();
        for (var start = monday; starts.Count < Weeks; start = start.AddDays(7))
        {
            var friday = start.AddDays(4);
            if (!breaks.Any(b => start >= b.From && friday <= b.To))
            {
                starts.Add(start);
            }
        }

        return starts;
    }

    /// <summary>What a break's days are called when the break has no name.</summary>
    public static readonly CalendarName UnnamedBreak = new("Vacanță", "Break");

    /// <summary>The non-teaching days: the listed ones, and the days of a break that only covers part of
    /// a teaching week (one starting on a Wednesday leaves that week's Monday and Tuesday), under the break's name.</summary>
    public IEnumerable<(DateOnly Date, CalendarName Reason)> Days()
    {
        var days = NonTeachingDays.Select(d => (Date: DateRange.Parse(d.Date, "calendar.nonTeachingDays[].date"), Reason: d.Reason!.Name()))
            .ToList();
        var weeks = WeekStarts();
        foreach (var range in Breaks)
        {
            var (from, to) = (range.FromDate(), range.ToDate());
            foreach (var monday in weeks)
            {
                for (var day = monday; day <= monday.AddDays(6); day = day.AddDays(1))
                {
                    if (day >= from && day <= to && days.All(d => d.Date != day))
                    {
                        days.Add((day, range.Name?.Name() ?? UnnamedBreak));
                    }
                }
            }
        }

        return days;
    }

    /// <summary>The breaks, with their names (null when unnamed).</summary>
    public IEnumerable<(DateOnly From, DateOnly To, CalendarName? Name)> BreakDates() =>
        Breaks.Select(b => (b.FromDate(), b.ToDate(), b.Name?.Name()));

    internal void ValidateNames()
    {
        foreach (var day in NonTeachingDays)
        {
            var where = $"calendar.nonTeachingDays {day.Date}: reason";
            if (day.Reason is null || string.IsNullOrWhiteSpace(day.Reason.Ro))
            {
                throw new FormatException($"overrides: {where} needs a Romanian name (\"...\" or {{ ro: ..., en: ... }}).");
            }

            day.Reason.Check(where);
        }

        foreach (var range in Breaks)
        {
            if (range.ToDate() < range.FromDate())
            {
                throw new FormatException($"overrides: calendar.breaks {range.From}: it ends ({range.To}) before it starts.");
            }

            range.Name?.Check($"calendar.breaks {range.From}: name");
        }
    }

    /// <summary>Names given in Romanian only: the English UI shows those in Romanian.</summary>
    internal IEnumerable<string> NameWarnings() =>
        NonTeachingDays.Where(d => d.Reason?.En is null).Select(d => $"calendar.nonTeachingDays {d.Date}: reason")
            .Concat(Breaks.Where(b => b.Name is { En: null }).Select(b => $"calendar.breaks {b.From}: name"))
            .Select(where => $"overrides: {where} has no English name, so the English UI shows the Romanian one; "
                + "write it as { ro: ..., en: ... }");
}

/// <summary>
/// A calendar name in the overrides: <c>"Ziua Națională a României"</c> (Romanian only: warned) or
/// <c>{ ro: "Ziua Națională a României", en: "Romania's National Day" }</c>.
/// </summary>
public sealed class CalendarText
{
    public string Ro { get; set; } = "";

    public string? En { get; set; }

    public CalendarName Name() => new(Ro, En);

    internal void Check(string where)
    {
        if (string.IsNullOrWhiteSpace(Ro))
        {
            throw new FormatException($"overrides: {where} needs a Romanian name (ro).");
        }

        if (Ro.Length > CalendarName.MaxLength || En?.Length > CalendarName.MaxLength)
        {
            throw new FormatException($"overrides: {where} is longer than {CalendarName.MaxLength} characters.");
        }
    }

    /// <summary>Reads either form; any key but ro and en is refused, like every unknown key.</summary>
    internal sealed class Converter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(CalendarText);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            if (parser.TryConsume<Scalar>(out var scalar))
            {
                return scalar.Value.Length == 0 || (scalar.IsPlainImplicit && scalar.Value is "~" or "null")
                    ? null
                    : new CalendarText { Ro = scalar.Value };
            }

            var start = parser.Consume<MappingStart>();
            var text = new CalendarText();
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                var key = parser.Consume<Scalar>();
                var value = parser.Consume<Scalar>();
                switch (key.Value)
                {
                    case "ro":
                        text.Ro = value.Value;
                        break;
                    case "en":
                        text.En = value.Value.Length == 0 ? null : value.Value;
                        break;
                    default:
                        throw new YamlException(key.Start, key.End, $"\"{key.Value}\" isn't ro or en (a calendar name at {start.Start})");
                }
            }

            return text;
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
            throw new NotSupportedException("The overrides are only read.");
    }
}

public sealed class DateRange
{
    public string From { get; set; } = "";

    public string To { get; set; } = "";

    /// <summary>A break's name ("Vacanța de iarnă" / "Winter break"); optional.</summary>
    public CalendarText? Name { get; set; }

    public DateOnly FromDate() => Parse(From, "calendar.breaks[].from");

    public DateOnly ToDate() => Parse(To, "calendar.breaks[].to");

    internal static DateOnly Parse(string value, string where) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new FormatException($"overrides: {where} must be a date as yyyy-MM-dd, not \"{value}\".");
}

public sealed class NonTeachingDayOverride
{
    public string Date { get; set; } = "";

    /// <summary>Why there are no classes: a name in Romanian, or in Romanian and English.</summary>
    public CalendarText? Reason { get; set; }
}

public enum ColumnRole
{
    /// <summary>A column of the timetable grid (a sheet without group columns uses this for its own).</summary>
    Grid,

    /// <summary>Optional subjects, each with the slots it's offered at.</summary>
    Optional,

    /// <summary>Facultative subjects (and extra lab groups), with their slots: optional too.</summary>
    Facultative,

    /// <summary>Labs everyone has, at one of several offered times (master sheets).</summary>
    PickSlot,

    /// <summary>Text shown with the timetable.</summary>
    Notes,
}

public sealed class SheetOverrides
{
    public bool Skip { get; set; }

    /// <summary>The year of study the sheet is for.</summary>
    public int Year { get; set; }

    /// <summary>The programme of every group column whose header label isn't in <see cref="Columns"/>,
    /// and of a sheet without group columns.</summary>
    public string? Programme { get; set; }

    public string? Series { get; set; }

    /// <summary>A group-header label as the sheet has it (e.g. "Seria 1 (X)", "SISTEME ROBOTICE") →
    /// the programme and series of the group columns under it.</summary>
    public Dictionary<string, ColumnLabelOverride> Columns { get; set; } = [];

    /// <summary>A column letter → what it holds: optional, facultative, pickSlot, notes or grid. Columns
    /// outside the groups that aren't listed hold notes.</summary>
    public Dictionary<string, string> ColumnRoles { get; set; } = [];

    /// <summary>An optional lab's audience tag ([R], [E]) → the programme it means.</summary>
    public Dictionary<string, string> Tags { get; set; } = [];

    /// <summary>An abbreviation as used in this sheet (LSV) → the subject's full name.</summary>
    public Dictionary<string, string> Aliases { get; set; } = [];

    /// <summary>
    /// A one-hour activity whose two-hour block has a partner in its other row (another one-hour cell in
    /// the same column, or in the same room) alternates with it: two hours every other week. The top row
    /// is <c>odd</c> weeks and the bottom row the other parity, unless this says <c>even</c>. A cell can be
    /// set differently in cells (weeks, end).
    /// </summary>
    public string TopRowWeeks { get; set; } = "odd";

    /// <summary>False reads every one-hour cell of the sheet as written (one hour, every week).</summary>
    public bool Alternate { get; set; } = true;

    /// <summary>Subjects (full names) whose classes on the grid are offers any student of the year can
    /// pick, whatever column they sit in: optional courses grouped differently from the timetable groups.</summary>
    public List<string> OptionalSubjects { get; set; } = [];

    /// <summary>Subjects (full names) every student of the year has, though an optional or facultative
    /// column offers them (a required course whose labs are listed with the optional ones): those offers
    /// are times to pick from, as in a pickSlot column.</summary>
    public List<string> RequiredSubjects { get; set; } = [];
}

public sealed class ColumnLabelOverride
{
    public string Programme { get; set; } = "";

    public string? Series { get; set; }
}

/// <summary>A correction for one cell. Anything left out keeps what the rules read.</summary>
public sealed class CellOverride
{
    /// <summary>The cell's text must start with this (ignoring case and spacing), or the override is
    /// ignored with a warning: a guard against a cell address that moved in a newer file.</summary>
    public string? Text { get; set; }

    public bool Skip { get; set; }

    /// <summary>Makes a cell without a mark an activity: course, lab, seminar, project, or a sum like
    /// lab+project.</summary>
    public string? Kind { get; set; }

    public string? Subject { get; set; }

    /// <summary>"HH:mm".</summary>
    public string? Start { get; set; }

    /// <summary>"HH:mm".</summary>
    public string? End { get; set; }

    /// <summary>Like the sheets write them: "1-7", "odd", "even", "3, 5, 7, 9", "s. 1-7 s. impara".</summary>
    public string? Weeks { get; set; }

    public string? Room { get; set; }

    public string? Teacher { get; set; }

    public string? Note { get; set; }

    /// <summary>Turns the cell into a note instead of a slot.</summary>
    public bool AsNote { get; set; }
}

public sealed class RoomOverride
{
    public string? Building { get; set; }

    public string? Floor { get; set; }

    public int? Capacity { get; set; }
}
