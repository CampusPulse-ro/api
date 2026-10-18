namespace CampusPulse.Domain.Timetable;

/// <summary>A study programme, by a code of ours chosen in the import's overrides file (the faculty's
/// sheets only have headers like "Seria 1 (X)").</summary>
public sealed class Programme
{
    public const int CodeMaxLength = 32;
    public const int NameMaxLength = 200;

    private Programme()
    {
    }

    public Programme(string code, string name, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > CodeMaxLength)
        {
            throw new ArgumentException($"A code of 1-{CodeMaxLength} characters is required.", nameof(code));
        }

        Id = Guid.CreateVersion7(now);
        Code = code;
        Rename(name);
    }

    public Guid Id { get; private set; }

    public string Code { get; private set; } = "";

    public string Name { get; private set; } = "";

    public void Rename(string name) => Name = TextLimits.Clamp(name, NameMaxLength) is { Length: > 0 } n ? n : Code;
}

/// <summary>
/// The unit a student belongs to: a subgroup (semigrupă) like 2.1 of year 3 of a programme, in one
/// semester. A year can also have series (C, D). A sheet without group columns (the master
/// programmes) has one group, number 1 with <see cref="Subgroup"/> 0. Rows are kept across re-imports
/// (matched on all of these), so the group a student picked survives an updated timetable file; one an
/// update no longer has is retired, never deleted, so the student still sees it as "no longer offered".
/// </summary>
public sealed class StudyGroup
{
    public const int SeriesMaxLength = 8;

    private StudyGroup()
    {
    }

    public StudyGroup(Guid semesterId, Guid programmeId, int year, string series, int group, int subgroup,
        string source, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7(now);
        SemesterId = semesterId;
        ProgrammeId = programmeId;
        Year = year;
        Series = series;
        Group = group;
        Subgroup = subgroup;
        Source = TextLimits.Clamp(source, TextLimits.SourceMaxLength);
    }

    public Guid Id { get; private set; }

    public Guid SemesterId { get; private set; }

    public Guid ProgrammeId { get; private set; }

    public int Year { get; private set; }

    /// <summary>"" when the year has no series.</summary>
    public string Series { get; private set; } = "";

    public int Group { get; private set; }

    /// <summary>0 when the group isn't split into subgroups.</summary>
    public int Subgroup { get; private set; }

    /// <summary>The workbook the group came from, by its id in the overrides file (not its file name,
    /// which changes from one download to the next).</summary>
    public string Source { get; private set; } = "";

    /// <summary>When an import of its workbook no longer had it; null while it's offered.</summary>
    public DateTimeOffset? RetiredAt { get; private set; }

    /// <summary>"2.1", or "1" without subgroups.</summary>
    public string Label => Subgroup == 0 ? $"{Group}" : $"{Group}.{Subgroup}";

    public void Retire(DateTimeOffset now) => RetiredAt ??= now;

    /// <summary>Offered again (a later import has it); its source is the workbook that has it now.</summary>
    public void Offer(string source)
    {
        RetiredAt = null;
        Source = TextLimits.Clamp(source, TextLimits.SourceMaxLength);
    }
}

/// <summary>A room, by the code the timetables use (<c>R418A</c>, <c>707CP</c>).</summary>
public sealed class Room
{
    public const int CodeMaxLength = 32;

    private Room()
    {
    }

    public Room(string code, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > CodeMaxLength)
        {
            throw new ArgumentException($"A room code of 1-{CodeMaxLength} characters is required.", nameof(code));
        }

        Id = Guid.CreateVersion7(now);
        Code = code;
    }

    public Guid Id { get; private set; }

    public string Code { get; private set; } = "";

    public string? Building { get; private set; }

    public string? Floor { get; private set; }

    public int? Capacity { get; private set; }

    /// <summary>Sets what the overrides file says about the room; null leaves a value as it is.</summary>
    public void Describe(string? building, string? floor, int? capacity)
    {
        Building = building is null ? Building : TextLimits.Clamp(building, 100);
        Floor = floor is null ? Floor : TextLimits.Clamp(floor, 20);
        Capacity = capacity ?? Capacity;
    }
}

/// <summary>How a student comes to have a subject.</summary>
public enum SubjectChoice
{
    /// <summary>Everyone in the groups it's scheduled for has it, at the times on the grid.</summary>
    Fixed,

    /// <summary>An optional or facultative subject: a student opts in, then picks one of its offered
    /// slots.</summary>
    Optional,

    /// <summary>Everyone has it, but at a time they pick from the offered slots (most master labs).</summary>
    PickSlot,
}

/// <summary>
/// A subject of one programme and year in a semester, under its full name (an abbreviation used in
/// the sheets is <see cref="ShortName"/>). Kept across re-imports by name, so a student's choice of an
/// optional subject survives an updated file; one an update no longer has is retired, never deleted.
/// </summary>
public sealed class Subject
{
    public const int NameMaxLength = 200;
    public const int ShortNameMaxLength = 32;

    private Subject()
    {
    }

    public Subject(Guid semesterId, Guid programmeId, int year, string name, string source, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > NameMaxLength)
        {
            throw new ArgumentException($"A name of 1-{NameMaxLength} characters is required.", nameof(name));
        }

        Id = Guid.CreateVersion7(now);
        SemesterId = semesterId;
        ProgrammeId = programmeId;
        Year = year;
        Name = name;
        Source = TextLimits.Clamp(source, TextLimits.SourceMaxLength);
    }

    public Guid Id { get; private set; }

    public Guid SemesterId { get; private set; }

    public Guid ProgrammeId { get; private set; }

    public int Year { get; private set; }

    public string Name { get; private set; } = "";

    public string? ShortName { get; private set; }

    public SubjectChoice Choice { get; private set; }

    /// <summary>The workbook the subject came from, by its id in the overrides file.</summary>
    public string Source { get; private set; } = "";

    /// <summary>When an import of its workbook no longer had it; null while it's offered.</summary>
    public DateTimeOffset? RetiredAt { get; private set; }

    /// <summary>Its course on Campus Virtual (UPT's Moodle), by the course id; null when it has none we know of.</summary>
    public int? CampusVirtualCourseId { get; private set; }

    /// <summary>What the latest import says about it; offered again if it was retired.</summary>
    public void Update(string? shortName, SubjectChoice choice, string source, int? campusVirtualCourseId)
    {
        if (campusVirtualCourseId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(campusVirtualCourseId), "A Campus Virtual course id is positive.");
        }

        ShortName = shortName is null ? null : TextLimits.Clamp(shortName, ShortNameMaxLength);
        Choice = choice;
        CampusVirtualCourseId = campusVirtualCourseId;
        RetiredAt = null;
        Source = TextLimits.Clamp(source, TextLimits.SourceMaxLength);
    }

    public void Retire(DateTimeOffset now) => RetiredAt ??= now;
}

internal static class TextLimits
{
    public const int SourceMaxLength = 200;

    public static string Clamp(string? value, int max)
    {
        var trimmed = (value ?? "").Trim();
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }
}
