using CampusPulse.Domain.Timetable;

namespace CampusPulse.Api.Timetable.Import;

/// <summary>A study group as the sheets define it: programme, year, series, group and subgroup.</summary>
public sealed record GroupKey(string Programme, int Year, string Series, int Group, int Subgroup)
{
    public string Label => Subgroup == 0 ? $"{Group}" : $"{Group}.{Subgroup}";

    public override string ToString() => $"{Programme} y{Year}{(Series.Length > 0 ? " " + Series : "")} {Label}";
}

/// <summary>A subject: its full name within a programme's year. Two keys are equal when the names are
/// equal ignoring case and diacritics.</summary>
public sealed record SubjectKey(string Programme, int Year, string Name)
{
    public bool Equals(SubjectKey? other) =>
        other is not null && Programme == other.Programme && Year == other.Year && CellText.NameKey(Name) == CellText.NameKey(other.Name);

    public override int GetHashCode() => HashCode.Combine(Programme, Year, CellText.NameKey(Name));

    public override string ToString() => $"{Programme} y{Year} {Name}";
}

/// <param name="CampusVirtualCourseId">its Campus Virtual course, from the overrides' campusVirtual section.</param>
public sealed record ParsedSubject(SubjectKey Key, string? ShortName, SubjectChoice Choice, int? CampusVirtualCourseId = null);

public sealed record ParsedSlot(
    SubjectKey Subject,
    ActivityKinds Kinds,
    DayOfWeek Weekday,
    TimeOnly StartsAt,
    TimeOnly EndsAt,
    WeekSet Weeks,
    string? Room,
    string? Teacher,
    string? Note,
    IReadOnlyList<GroupKey> Groups,
    string SourceCell,
    string RawText);

public sealed record ParsedOffer(
    SubjectKey Subject,
    ActivityKinds Kinds,
    DayOfWeek Weekday,
    TimeOnly StartsAt,
    TimeOnly EndsAt,
    WeekSet Weeks,
    string? Room,
    string SourceCell,
    string RawText);

public sealed record ParsedNote(string Programme, int Year, string Text, string SourceCell);

/// <summary>Everything one workbook holds, ready to compare with the database and write.</summary>
public sealed class ParsedTimetable(string source, string fileName)
{
    /// <summary>The workbook's id in the overrides' sources: what it contributes is kept under it.</summary>
    public string Source { get; } = source;

    public string FileName { get; } = fileName;

    /// <summary>The sheets read.</summary>
    public List<string> Sheets { get; } = [];

    /// <summary>Sheets the source lists that the workbook doesn't have.</summary>
    public List<string> MissingSheets { get; } = [];

    /// <summary>What couldn't be read at all (a sheet whose days or groups aren't readable): an import
    /// with errors writes nothing.</summary>
    public List<string> Errors { get; } = [];

    public HashSet<GroupKey> Groups { get; } = [];

    public Dictionary<SubjectKey, ParsedSubject> Subjects { get; } = [];

    /// <summary>The overrides list Campus Virtual courses, so the import reports the subjects without one.</summary>
    public bool CampusVirtualListed { get; set; }

    public List<ParsedSlot> Slots { get; } = [];

    public List<ParsedOffer> Offers { get; } = [];

    public List<ParsedNote> Notes { get; } = [];

    /// <summary>Everything the import couldn't place for sure, by cell: the import prints them all,
    /// and each can be fixed in the overrides file.</summary>
    public List<string> Warnings { get; } = [];

    public IEnumerable<string> Programmes =>
        Groups.Select(g => g.Programme).Concat(Subjects.Keys.Select(s => s.Programme)).Distinct();

    /// <summary>Adds the subject, or makes an existing one's choice stricter: a subject that is on the
    /// grid and also offered at times to pick from is a pick-slot one; one offered as optional anywhere
    /// is optional.</summary>
    public void AddSubject(SubjectKey key, string? shortName, SubjectChoice choice)
    {
        if (!Subjects.TryGetValue(key, out var existing))
        {
            Subjects[key] = new ParsedSubject(key, shortName, choice);
            return;
        }

        var stricter = (existing.Choice, choice) switch
        {
            (SubjectChoice.Optional, _) or (_, SubjectChoice.Optional) => SubjectChoice.Optional,
            (SubjectChoice.PickSlot, _) or (_, SubjectChoice.PickSlot) => SubjectChoice.PickSlot,
            _ => SubjectChoice.Fixed,
        };
        Subjects[key] = existing with { ShortName = existing.ShortName ?? shortName, Choice = stricter };
    }
}
