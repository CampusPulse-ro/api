using Microsoft.EntityFrameworkCore;

namespace CampusPulse.Infrastructure;

public static class AccountDeletion
{
    /// <summary>
    /// Deletes the student with this Entra object id and adds one to <paramref name="day"/>'s
    /// deletion count, in one transaction. Everything else the student owns goes with the row: every
    /// table that refers to <c>users</c> does so with ON DELETE CASCADE (a test fails otherwise).
    /// Returns false, counting nothing, when there was no such student.
    /// Concurrent calls for the same student count once: the second DELETE waits on the first's row
    /// lock, then finds no row. The count is an atomic upsert, so deletions of different students on
    /// the same day never lose an increment.
    /// </summary>
    public static async Task<bool> DeleteAccountAsync(this CampusPulseDbContext db, string entraObjectId,
        DateOnly day, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var deleted = await db.Users.Where(u => u.EntraObjectId == entraObjectId).ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0)
        {
            return false;
        }

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO account_deletion_counts (day, deletions) VALUES ({day}, {deleted})
            ON CONFLICT (day) DO UPDATE SET deletions = account_deletion_counts.deletions + excluded.deletions
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
