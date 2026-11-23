using Microsoft.EntityFrameworkCore;

namespace CampusPulse.Infrastructure;

public static class AccountDeletion
{
    /// <summary>
    /// Deletes the account <paramref name="userId"/> and adds one to <paramref name="day"/>'s deletion count, in one
    /// transaction. Everything else the student owns goes with the row: every table that refers to <c>users</c> does
    /// so with ON DELETE CASCADE (a test fails otherwise). The email sign-in codes are per address, not per account,
    /// so the codes of the account's addresses (its own and its identities') are deleted here.
    /// Returns false, counting nothing, when there was no such account.
    /// Concurrent calls for the same student count once: the second DELETE waits on the first's row
    /// lock, then finds no row. The count is an atomic upsert, so deletions of different students on
    /// the same day never lose an increment.
    /// </summary>
    public static async Task<bool> DeleteAccountAsync(this CampusPulseDbContext db, Guid userId, DateOnly day,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var emails = await db.Users.Where(u => u.Id == userId && u.Email != null).Select(u => u.Email!)
            .Concat(db.UserIdentities.Where(i => i.UserId == userId && i.Email != null).Select(i => i.Email!))
            .ToListAsync(cancellationToken);
        var deleted = await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0)
        {
            return false;
        }

        if (emails.Count > 0)
        {
            await db.EmailSignInCodes.Where(c => emails.Contains(c.Email)).ExecuteDeleteAsync(cancellationToken);
        }

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO account_deletion_counts (day, deletions) VALUES ({day}, {deleted})
            ON CONFLICT (day) DO UPDATE SET deletions = account_deletion_counts.deletions + excluded.deletions
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
