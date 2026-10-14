namespace CampusPulse.Domain.Timetable;

/// <summary>What an activity is, from the sheets' marks: (c), (l), (s), (p), (c.f.), and combinations
/// such as (l+p).</summary>
[Flags]
public enum ActivityKinds
{
    None = 0,
    Course = 1,
    Lab = 2,
    Seminar = 4,
    Project = 8,
    Facultative = 16,
}

/// <summary>One kind, as the API lists them.</summary>
public enum ActivityKind
{
    Course,
    Lab,
    Seminar,
    Project,
    Facultative,
}

public static class ActivityKindsExtensions
{
    public static IReadOnlyList<ActivityKind> List(this ActivityKinds kinds) =>
        Enum.GetValues<ActivityKind>().Where(k => kinds.HasFlag((ActivityKinds)(1 << (int)k))).ToList();
}

/// <summary>
/// A weekly activity on the grid: a subject, for some study groups (a course merged across a whole
/// series links every subgroup in it, a lab one), on a weekday from <see cref="StartsAt"/> to
/// <see cref="EndsAt"/>, in the teaching weeks of <see cref="Weeks"/>. Replaced on every re-import of
/// its file: nothing a student owns points at a slot.
/// </summary>
public sealed class TimetableSlot
{
    public const int TeacherMaxLength = 200;
    public const int NoteMaxLength = 1000;
    public const int SourceCellMaxLength = 64;
    public const int RawTextMaxLength = 2000;

    private readonly List<TimetableSlotGroup> _groups = [];

    private TimetableSlot()
    {
    }

    public TimetableSlot(Guid semesterId, Guid subjectId, ActivityKinds kinds, DayOfWeek weekday, TimeOnly startsAt,
        TimeOnly endsAt, WeekSet weeks, string source, string sourceCell, string rawText, DateTimeOffset now)
    {
        if (endsAt <= startsAt)
        {
            throw new ArgumentException("An activity ends after it starts.", nameof(endsAt));
        }

        Id = Guid.CreateVersion7(now);
        SemesterId = semesterId;
        SubjectId = subjectId;
        Kinds = kinds;
        Weekday = weekday;
        StartsAt = startsAt;
        EndsAt = endsAt;
        Weeks = weeks;
        Source = TextLimits.Clamp(source, TextLimits.SourceMaxLength);
        SourceCell = TextLimits.Clamp(sourceCell, SourceCellMaxLength);
        RawText = TextLimits.Clamp(rawText, RawTextMaxLength);
    }

    public Guid Id { get; private set; }

    public Guid SemesterId { get; private set; }

    public Guid SubjectId { get; private set; }

    public ActivityKinds Kinds { get; private set; }

    public DayOfWeek Weekday { get; private set; }

    public TimeOnly StartsAt { get; private set; }

    public TimeOnly EndsAt { get; private set; }

    public WeekSet Weeks { get; private set; }

    public Guid? RoomId { get; private set; }

    public string? Teacher { get; private set; }

    public string? Note { get; private set; }

    /// <summary>The workbook the slot came from, by its id in the overrides file.</summary>
    public string Source { get; private set; } = "";

    /// <summary>Where the slot came from, e.g. <c>An 1!C5:H6</c>: the import report and the overrides
    /// file refer to cells this way.</summary>
    public string SourceCell { get; private set; } = "";

    /// <summary>The cell's text as the sheet has it, for a student who wants the details.</summary>
    public string RawText { get; private set; } = "";

    public IReadOnlyList<TimetableSlotGroup> Groups => _groups;

    public void Describe(Guid? roomId, string? teacher, string? note)
    {
        RoomId = roomId;
        Teacher = teacher is null ? null : TextLimits.Clamp(teacher, TeacherMaxLength);
        Note = note is null ? null : TextLimits.Clamp(note, NoteMaxLength);
    }

    public void AddGroup(Guid studyGroupId)
    {
        if (_groups.All(g => g.StudyGroupId != studyGroupId))
        {
            _groups.Add(new TimetableSlotGroup(Id, studyGroupId));
        }
    }
}

/// <summary>Links a slot to one of the study groups that has it.</summary>
public sealed class TimetableSlotGroup
{
    private TimetableSlotGroup()
    {
    }

    internal TimetableSlotGroup(Guid slotId, Guid studyGroupId)
    {
        SlotId = slotId;
        StudyGroupId = studyGroupId;
    }

    public Guid SlotId { get; private set; }

    public Guid StudyGroupId { get; private set; }
}

/// <summary>
/// One of the times a subject is offered at, for a student to pick (an optional lab's slots, a
/// master lab's sessions). Kept across re-imports while its day, times, room and weeks stay the same,
/// so a student's pick survives; an offer that changes is a new one and the pick is cleared.
/// </summary>
public sealed class OfferedSlot
{
    private OfferedSlot()
    {
    }

    public OfferedSlot(Guid subjectId, ActivityKinds kinds, DayOfWeek weekday, TimeOnly startsAt, TimeOnly endsAt, Guid? roomId,
        WeekSet weeks, string sourceCell, string rawText, DateTimeOffset now)
    {
        if (endsAt <= startsAt)
        {
            throw new ArgumentException("An activity ends after it starts.", nameof(endsAt));
        }

        Id = Guid.CreateVersion7(now);
        SubjectId = subjectId;
        Kinds = kinds;
        Weekday = weekday;
        StartsAt = startsAt;
        EndsAt = endsAt;
        RoomId = roomId;
        Weeks = weeks;
        SourceCell = TextLimits.Clamp(sourceCell, TimetableSlot.SourceCellMaxLength);
        RawText = TextLimits.Clamp(rawText, TimetableSlot.RawTextMaxLength);
    }

    public Guid Id { get; private set; }

    public Guid SubjectId { get; private set; }

    public ActivityKinds Kinds { get; private set; }

    public DayOfWeek Weekday { get; private set; }

    public TimeOnly StartsAt { get; private set; }

    public TimeOnly EndsAt { get; private set; }

    public Guid? RoomId { get; private set; }

    public WeekSet Weeks { get; private set; }

    public string SourceCell { get; private set; } = "";

    public string RawText { get; private set; } = "";

    /// <summary>True when this offer is the same one: a re-import keeps it (and the students' picks).</summary>
    public bool SameAs(DayOfWeek weekday, TimeOnly startsAt, TimeOnly endsAt, Guid? roomId, WeekSet weeks) =>
        Weekday == weekday && StartsAt == startsAt && EndsAt == endsAt && RoomId == roomId && Weeks == weeks;

    /// <summary>A re-import found the same offer: keeps it, with what the file says about it now.</summary>
    public void Refresh(ActivityKinds kinds, string sourceCell, string rawText)
    {
        Kinds = kinds;
        SourceCell = TextLimits.Clamp(sourceCell, TimetableSlot.SourceCellMaxLength);
        RawText = TextLimits.Clamp(rawText, TimetableSlot.RawTextMaxLength);
    }
}

/// <summary>Text from a timetable sheet that isn't a slot or an offer (sport, tutoring hours,
/// per-subgroup lab rotas written out in words), shown with the year's timetable.</summary>
public sealed class TimetableNote
{
    public const int TextMaxLength = 2000;

    private TimetableNote()
    {
    }

    public TimetableNote(Guid semesterId, Guid programmeId, int year, string text, string source, string sourceCell,
        DateTimeOffset now)
    {
        Id = Guid.CreateVersion7(now);
        SemesterId = semesterId;
        ProgrammeId = programmeId;
        Year = year;
        Text = TextLimits.Clamp(text, TextMaxLength);
        Source = TextLimits.Clamp(source, TextLimits.SourceMaxLength);
        SourceCell = TextLimits.Clamp(sourceCell, TimetableSlot.SourceCellMaxLength);
    }

    public Guid Id { get; private set; }

    public Guid SemesterId { get; private set; }

    public Guid ProgrammeId { get; private set; }

    public int Year { get; private set; }

    public string Text { get; private set; } = "";

    public string Source { get; private set; } = "";

    public string SourceCell { get; private set; } = "";
}

/// <summary>One import of one workbook: which (its source id in the overrides, the file name it had, the
/// sheets it covered), what exactly (the file's and the overrides' SHA-256, the parser version), when, and
/// what it reported. The same source imported again with the same three is skipped.</summary>
public sealed class TimetableImportRun
{
    private TimetableImportRun()
    {
    }

    public TimetableImportRun(Guid semesterId, string source, string fileName, IReadOnlyList<string> sheets, string fileSha256,
        string overridesSha256, int parserVersion, string summary, IReadOnlyList<string> warnings, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7(now);
        SemesterId = semesterId;
        Source = TextLimits.Clamp(source, TextLimits.SourceMaxLength);
        FileName = TextLimits.Clamp(fileName, TextLimits.SourceMaxLength);
        Sheets = [.. sheets];
        FileSha256 = fileSha256;
        OverridesSha256 = overridesSha256;
        ParserVersion = parserVersion;
        Summary = summary;
        Warnings = [.. warnings];
        ImportedAt = now;
    }

    public Guid Id { get; private set; }

    public Guid SemesterId { get; private set; }

    public string Source { get; private set; } = "";

    public string FileName { get; private set; } = "";

    public List<string> Sheets { get; private set; } = [];

    public string FileSha256 { get; private set; } = "";

    public string OverridesSha256 { get; private set; } = "";

    public int ParserVersion { get; private set; }

    public string Summary { get; private set; } = "";

    public List<string> Warnings { get; private set; } = [];

    public DateTimeOffset ImportedAt { get; private set; }
}
