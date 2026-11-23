namespace CampusPulse.Domain.Deadlines;

/// <summary>
/// A deadline the student entered by hand (Campus Virtual's stay on their device). At most <see cref="MaxPerUser"/>
/// per student. Deleted with the account.
/// </summary>
public sealed class Deadline
{
    public const int TitleMaxLength = 200;
    public const int CourseMaxLength = 200;
    public const int NotesMaxLength = 2000;
    public const int MaxPerUser = 500;

    /// <summary>The due times accepted: from <see cref="EarliestDue"/>, before <see cref="LatestDue"/>. Anything outside
    /// is a mistake (year 1 is what a missing date would become), not a deadline.</summary>
    public static readonly DateTimeOffset EarliestDue = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset LatestDue = new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private Deadline()
    {
    }

    public Deadline(Guid userId, string title, string? course, DateTimeOffset dueAt, string? notes, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7(now);
        UserId = userId;
        CreatedAt = Instants.Stored(now);
        Update(title, course, dueAt, notes, done: false, now);
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string Title { get; private set; } = "";

    public string? Course { get; private set; }

    public DateTimeOffset DueAt { get; private set; }

    public string? Notes { get; private set; }

    public bool Done { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Why these values can't be a deadline, or null when they can: a title of 1-200 characters, a course of at
    /// most 200 and notes of at most 2000 (trimmed; empty course or notes mean none), due between
    /// <see cref="EarliestDue"/> and <see cref="LatestDue"/>.</summary>
    public static string? Problem(string? title, string? course, string? notes, DateTimeOffset dueAt) =>
        (title?.Trim().Length ?? 0) is 0 or > TitleMaxLength ? $"title must be 1-{TitleMaxLength} characters."
        : (course?.Trim().Length ?? 0) > CourseMaxLength ? $"course must be at most {CourseMaxLength} characters."
        : (notes?.Trim().Length ?? 0) > NotesMaxLength ? $"notes must be at most {NotesMaxLength} characters."
        : dueAt < EarliestDue || dueAt >= LatestDue ? $"dueAt must be from {EarliestDue.Year} up to (not including) {LatestDue.Year}."
        : null;

    public void Update(string title, string? course, DateTimeOffset dueAt, string? notes, bool done, DateTimeOffset now)
    {
        if (Problem(title, course, notes, dueAt) is { } problem)
        {
            throw new ArgumentException(problem);
        }

        Title = title.Trim();
        Course = string.IsNullOrWhiteSpace(course) ? null : course.Trim();
        DueAt = Instants.Stored(dueAt.ToUniversalTime());
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        Done = done;
        UpdatedAt = Instants.Stored(now);
    }
}
