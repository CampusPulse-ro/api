namespace CampusPulse.Domain.Users;

/// <summary>
/// A sign-in method linked to an account, other than its email: today the student's UPT Microsoft account
/// (<see cref="UptMicrosoft"/>, <see cref="Subject"/> = its Entra <c>oid</c>); Google or Apple later. One per provider
/// per account, and a provider's account belongs to one CampusPulse account. Deleted with the account.
/// </summary>
public sealed class UserIdentity
{
    public const string UptMicrosoft = "upt-microsoft";
    public const int ProviderMaxLength = 32;
    public const int SubjectMaxLength = 128;

    private UserIdentity()
    {
    }

    public UserIdentity(Guid userId, string provider, string subject, string? email, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > SubjectMaxLength)
        {
            throw new ArgumentException($"A subject of 1-{SubjectMaxLength} characters is required.", nameof(subject));
        }

        UserId = userId;
        Provider = provider;
        Subject = subject;
        Email = email is null ? null : Emails.Normalize(email);
        LinkedAt = now;
    }

    public Guid UserId { get; private set; }

    public string Provider { get; private set; } = "";

    /// <summary>The provider's stable id for the account (Entra: <c>oid</c>).</summary>
    public string Subject { get; private set; } = "";

    /// <summary>The email the provider gives for it (normalized), when it gives one.</summary>
    public string? Email { get; private set; }

    public DateTimeOffset LinkedAt { get; private set; }

    /// <summary>The provider's email as its latest token has it; false when nothing changed.</summary>
    public bool Refresh(string? email)
    {
        var normalized = string.IsNullOrWhiteSpace(email) ? null : Emails.Normalize(email);
        if (normalized is null || normalized == Email)
        {
            return false;
        }

        Email = normalized;
        return true;
    }
}
