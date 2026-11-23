namespace CampusPulse.Domain.Users;

/// <summary>
/// A CampusPulse account. <see cref="Id"/> is what everything of the student's refers to. The account signs in with
/// its verified <see cref="Email"/> (a one-time code) or with a linked identity (<see cref="UserIdentity"/>: their UPT
/// Microsoft account today). <see cref="EntraObjectId"/> is the UPT account's id as the previous release stored it:
/// kept in step with the UPT identity for one release, so a rollback still finds the student (expand first, contract
/// later); new code reads the identities.
/// </summary>
public sealed class User
{
    public const int EntraObjectIdMaxLength = 64;
    /// <summary>The column's size, as the previous release made it; a new address is at most
    /// <see cref="Emails.MaxLength"/>.</summary>
    public const int EmailMaxLength = 320;
    public const int DisplayNameMaxLength = 256;

    /// <summary>How stale <see cref="LastSeenAt"/> may get before a request refreshes it, so a
    /// signed-in request doesn't mean a database write every time.</summary>
    public static readonly TimeSpan SeenGranularity = TimeSpan.FromMinutes(5);

    private User()
    {
    }

    private User(string? email, string? displayName, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7(now);
        Email = email is null ? null : Emails.Normalize(email);
        DisplayName = Clamp(displayName, DisplayNameMaxLength);
        CreatedAt = now;
        LastSeenAt = now;
    }

    /// <summary>An account made by signing in with an email and a code: the email is verified, and until the student
    /// links UPT their name is the email's local part.</summary>
    public static User FromEmail(string verifiedEmail, DateTimeOffset now)
    {
        var email = Emails.Normalize(verifiedEmail);
        return new User(email, email[..email.IndexOf('@', StringComparison.Ordinal)], now);
    }

    /// <summary>An account made by signing in with UPT. <paramref name="verifiedEmail"/> is the UPT account's email,
    /// or null when another account already has it.</summary>
    public static User FromUpt(string entraObjectId, string? verifiedEmail, string? displayName, DateTimeOffset now)
    {
        var user = new User(verifiedEmail, displayName, now);
        user.LinkUpt(entraObjectId);
        return user;
    }

    public Guid Id { get; private set; }

    /// <summary>The UPT account's Entra object id while UPT is linked (see the class remarks).</summary>
    public string? EntraObjectId { get; private set; }

    /// <summary>The account's verified email, trimmed and lowercase (<see cref="Emails.Normalize"/>); unique. Null for
    /// an account made with UPT whose email another account already had.</summary>
    public string? Email { get; private set; }

    public string DisplayName { get; private set; } = "";

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    /// <summary>Records a request. <paramref name="displayName"/> is the name a UPT token carries (empty or null: the
    /// stored one is kept). Returns true when something changed and needs saving.</summary>
    public bool Seen(string? displayName, DateTimeOffset now)
    {
        var changed = false;
        displayName = Clamp(displayName, DisplayNameMaxLength);
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

    /// <summary>Gives an account without one a verified email (a code was confirmed, or a linked UPT account has
    /// it). An account's email never changes once set.</summary>
    public void VerifyEmail(string email) => Email ??= Emails.Normalize(email);

    public void LinkUpt(string entraObjectId)
    {
        if (string.IsNullOrWhiteSpace(entraObjectId) || entraObjectId.Length > EntraObjectIdMaxLength)
        {
            throw new ArgumentException("An Entra object id of 1-64 characters is required.", nameof(entraObjectId));
        }

        EntraObjectId = entraObjectId;
    }

    public void UnlinkUpt() => EntraObjectId = null;

    private static string Clamp(string? value, int max)
    {
        var trimmed = (value ?? "").Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

/// <summary>Email addresses as accounts store and compare them.</summary>
public static class Emails
{
    /// <summary>Trimmed and lowercase: an account's email is unique whatever case it was typed in.</summary>
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();

    /// <summary>The longest address accepted (RFC 5321's path limit).</summary>
    public const int MaxLength = 254;

    /// <summary>A plain address (no display name), at most <see cref="MaxLength"/> characters, with one <c>@</c> and a
    /// dot in its domain.</summary>
    public static bool IsValid(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var normalized = Normalize(email);
        var at = normalized.IndexOf('@', StringComparison.Ordinal);
        return normalized.Length <= MaxLength
            && at > 0 && at == normalized.LastIndexOf('@') && normalized.IndexOf('.', at) > at + 1 && !normalized.EndsWith('.')
            && !normalized.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '<' or '>' or ',' or ';' or '"')
            && System.Net.Mail.MailAddress.TryCreate(normalized, out var parsed) && parsed.Address == normalized;
    }
}
