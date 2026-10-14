namespace CampusPulse.Domain.Users;

/// <summary>
/// How many accounts were deleted on one UTC day: the only trace a deleted account leaves, so it
/// holds no user identifier of any kind. Rows are written only by the account deletion itself, in
/// the same transaction as the delete.
/// </summary>
public sealed class AccountDeletionCount
{
    private AccountDeletionCount()
    {
    }

    public DateOnly Day { get; private set; }

    public int Deletions { get; private set; }
}
