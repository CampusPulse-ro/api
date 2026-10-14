namespace CampusPulse.Domain.Users;

/// <summary>
/// A student who has signed in with their UPT Microsoft account at least once. Identified by the
/// account's Entra object id (<c>oid</c>), which never changes; email and name are refreshed from
/// the token on sign-in when it carries them.
/// </summary>
public sealed class User
{
    public const int EntraObjectIdMaxLength = 64;
    public const int EmailMaxLength = 320;
    public const int DisplayNameMaxLength = 256;

    /// <summary>How stale <see cref="LastSeenAt"/> may get before a request refreshes it, so a
    /// signed-in request doesn't mean a database write every time.</summary>
    public static readonly TimeSpan SeenGranularity = TimeSpan.FromMinutes(5);

    private User()
    {
    }

    public User(string entraObjectId, string? email, string? displayName, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(entraObjectId) || entraObjectId.Length > EntraObjectIdMaxLength)
        {
            throw new ArgumentException("An Entra object id of 1-64 characters is required.", nameof(entraObjectId));
        }

        Id = Guid.CreateVersion7(now);
        EntraObjectId = entraObjectId;
        Email = Clamp(email, EmailMaxLength);
        DisplayName = Clamp(displayName, DisplayNameMaxLength);
        CreatedAt = now;
        LastSeenAt = now;
    }

    public Guid Id { get; private set; }

    public string EntraObjectId { get; private set; } = "";

    public string Email { get; private set; } = "";

    public string DisplayName { get; private set; } = "";

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    /// <summary>Records a sign-in. An empty or missing email or name means the token didn't carry
    /// it, so the stored one is kept. Returns true when something changed and needs saving.</summary>
    public bool Seen(string? email, string? displayName, DateTimeOffset now)
    {
        var changed = false;
        email = Clamp(email, EmailMaxLength);
        displayName = Clamp(displayName, DisplayNameMaxLength);
        if (email.Length > 0 && email != Email)
        {
            Email = email;
            changed = true;
        }

        if (displayName.Length > 0 && displayName != DisplayName)
        {
            DisplayName = displayName;
            changed = true;
        }

        if (now - LastSeenAt >= SeenGranularity)
        {
            LastSeenAt = now;
            changed = true;
        }

        return changed;
    }

    private static string Clamp(string? value, int max)
    {
        var trimmed = (value ?? "").Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
