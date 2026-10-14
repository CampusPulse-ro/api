namespace CampusPulse.Domain.Timetable;

/// <summary>
/// A student's timetable settings: the study group they're in. Deleted with the account (cascade
/// from <c>users</c>); if the group disappears in a later import, <see cref="StudyGroupId"/> becomes
/// null and the app asks them to pick again.
/// </summary>
public sealed class StudentSettings
{
    private StudentSettings()
    {
    }

    public StudentSettings(Guid userId, DateTimeOffset now)
    {
        UserId = userId;
        UpdatedAt = now;
    }

    public Guid UserId { get; private set; }

    public Guid? StudyGroupId { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public void SetStudyGroup(Guid? studyGroupId, DateTimeOffset now)
    {
        StudyGroupId = studyGroupId;
        UpdatedAt = now;
    }
}

/// <summary>
/// A subject the student takes by choice (an <see cref="SubjectChoice.Optional"/> one), or one whose
/// time they pick (<see cref="SubjectChoice.PickSlot"/>), with the offered slot they picked, if any.
/// Deleted with the account; deleted with the subject; the pick becomes null when the offer goes.
/// </summary>
public sealed class StudentSubject
{
    private StudentSubject()
    {
    }

    public StudentSubject(Guid userId, Guid subjectId, DateTimeOffset now)
    {
        UserId = userId;
        SubjectId = subjectId;
        UpdatedAt = now;
    }

    public Guid UserId { get; private set; }

    public Guid SubjectId { get; private set; }

    public Guid? OfferedSlotId { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public void Pick(Guid? offeredSlotId, DateTimeOffset now)
    {
        OfferedSlotId = offeredSlotId;
        UpdatedAt = now;
    }
}
