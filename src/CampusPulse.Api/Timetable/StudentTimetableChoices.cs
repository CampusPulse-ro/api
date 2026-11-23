using CampusPulse.Domain.Timetable;
using CampusPulse.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CampusPulse.Api.Timetable;

/// <summary>What a student's own timetable is made of: their study group, if they picked one, and the subjects
/// they chose (with the offered slot picked, if any). Per student, so always read from the database, never
/// cached: a change is seen by the next request.</summary>
public sealed record StudentTimetableChoices(Guid UserId, Guid? StudyGroupId, IReadOnlyList<StudentSubject> Subjects)
{
    /// <summary>The choices of each of <paramref name="userIds"/>, in that order (a student without settings has
    /// no group and no subjects). Two queries, however many students.</summary>
    public static async Task<IReadOnlyList<StudentTimetableChoices>> LoadAsync(CampusPulseDbContext db, IReadOnlyList<Guid> userIds,
        CancellationToken cancellationToken)
    {
        var ids = userIds.ToList();
        var groups = await db.StudentSettings.AsNoTracking().Where(s => ids.Contains(s.UserId))
            .ToDictionaryAsync(s => s.UserId, s => s.StudyGroupId, cancellationToken);
        var subjects = (await db.StudentSubjects.AsNoTracking().Where(s => ids.Contains(s.UserId)).ToListAsync(cancellationToken))
            .ToLookup(s => s.UserId);
        return [.. ids.Select(id => new StudentTimetableChoices(id, groups.GetValueOrDefault(id), [.. subjects[id]]))];
    }

    /// <summary>The signed-in student's choices, or null when they have no account: their <c>users</c> row, group and
    /// subjects in one query (one round trip to the database).</summary>
    public static async Task<StudentTimetableChoices?> ForOidAsync(CampusPulseDbContext db, string oid, CancellationToken cancellationToken)
    {
        var row = await db.Users.AsNoTracking()
            .Where(u => u.EntraObjectId == oid)
            .Select(u => new
            {
                u.Id,
                StudyGroupId = db.StudentSettings.Where(s => s.UserId == u.Id).Select(s => s.StudyGroupId).FirstOrDefault(),
                Subjects = db.StudentSubjects.Where(s => s.UserId == u.Id).ToList(),
            })
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : new StudentTimetableChoices(row.Id, row.StudyGroupId, row.Subjects);
    }
}
