using System.Security.Cryptography;
using System.Text;

namespace CampusPulse.Domain.Users;

/// <summary>How a code check came out.</summary>
public enum CodeCheck
{
    Accepted,

    /// <summary>Wrong, expired or never sent: the attempt was counted (when there is a live code).</summary>
    Invalid,

    /// <summary>The code took its last attempt: ask for a new one.</summary>
    Burned,
}

/// <summary>
/// The one-time sign-in code of an email address: one live code per address (a new request replaces it), kept only as
/// a salted SHA-256, valid for <see cref="Lifetime"/> and <see cref="MaxAttempts"/> wrong tries. The row also remembers
/// when codes were sent in the last day, for the per-address limits (<see cref="SendRefusal"/>). Not under
/// <c>users</c>: an address may have no account yet. Account deletion deletes its address's row.
/// </summary>
public sealed class EmailSignInCode
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public const int MaxAttempts = 5;
    public const int CodeLength = 6;

    /// <summary>Codes an address may get: <see cref="ShortLimit"/> per <see cref="ShortWindow"/>,
    /// <see cref="DailyLimit"/> per <see cref="DailyWindow"/>.</summary>
    public static readonly TimeSpan ShortWindow = TimeSpan.FromMinutes(15);
    public const int ShortLimit = 3;
    public static readonly TimeSpan DailyWindow = TimeSpan.FromDays(1);
    public const int DailyLimit = 10;

    private EmailSignInCode()
    {
    }

    public EmailSignInCode(string email) => Email = Emails.Normalize(email);

    public string Email { get; private set; } = "";

    /// <summary>SHA-256 of <see cref="Salt"/> + the code; null once used or burned.</summary>
    public byte[]? CodeHash { get; private set; }

    public byte[]? Salt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>When the current code was sent.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When codes were sent to this address in the last <see cref="DailyWindow"/>.</summary>
    public List<DateTimeOffset> Sends { get; private set; } = [];

    /// <summary>A new random code of <see cref="CodeLength"/> digits.</summary>
    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>How long until the address may get another code, or null when it may now.</summary>
    public TimeSpan? SendRefusal(DateTimeOffset now)
    {
        var recent = Sends.Where(s => now - s < DailyWindow).Order().ToList();
        var shortWindow = recent.Where(s => now - s < ShortWindow).ToList();
        if (shortWindow.Count >= ShortLimit)
        {
            return shortWindow[^ShortLimit] + ShortWindow - now;
        }

        return recent.Count >= DailyLimit ? recent[^DailyLimit] + DailyWindow - now : null;
    }

    /// <summary>The code an <see cref="Issue"/> replaced, to put back if the new one can't be sent.</summary>
    public sealed record Replaced(byte[]? CodeHash, byte[]? Salt, DateTimeOffset ExpiresAt, int Attempts, DateTimeOffset CreatedAt);

    /// <summary>Replaces any live code with <paramref name="code"/>, valid from <paramref name="now"/>, and records the
    /// send. Returns the code it replaced.</summary>
    public Replaced Issue(string code, DateTimeOffset now)
    {
        var replaced = new Replaced(CodeHash, Salt, ExpiresAt, Attempts, CreatedAt);
        now = Instants.Stored(now);
        Salt = RandomNumberGenerator.GetBytes(16);
        CodeHash = Hash(Salt, code);
        ExpiresAt = now + Lifetime;
        Attempts = 0;
        CreatedAt = now;
        Sends = [.. Sends.Where(s => now - s < DailyWindow), now];
        return replaced;
    }

    /// <summary>Takes back the code issued at <paramref name="sentAt"/> and its send, since it couldn't be delivered,
    /// and puts back the code it <paramref name="replaced"/>: a failed resend leaves the earlier email's code
    /// working.</summary>
    public void Withdraw(DateTimeOffset sentAt, Replaced replaced)
    {
        sentAt = Instants.Stored(sentAt);
        (CodeHash, Salt, ExpiresAt, Attempts, CreatedAt) =
            (replaced.CodeHash, replaced.Salt, replaced.ExpiresAt, replaced.Attempts, replaced.CreatedAt);
        Sends = [.. Sends.Where(s => s != sentAt)];
    }

    /// <summary>True when the live code is the one issued at <paramref name="sentAt"/>.</summary>
    public bool IssuedAt(DateTimeOffset sentAt) => CodeHash is not null && CreatedAt == Instants.Stored(sentAt);

    /// <summary>Checks <paramref name="code"/>: accepted once (the code is used up), counted when wrong, burned at the
    /// last attempt.</summary>
    public CodeCheck Check(string code, DateTimeOffset now)
    {
        if (CodeHash is null || Salt is null)
        {
            return Attempts >= MaxAttempts ? CodeCheck.Burned : CodeCheck.Invalid;
        }

        if (now >= ExpiresAt)
        {
            return CodeCheck.Invalid;
        }

        if (CryptographicOperations.FixedTimeEquals(CodeHash, Hash(Salt, code)))
        {
            CodeHash = null;
            Salt = null;
            return CodeCheck.Accepted;
        }

        Attempts++;
        if (Attempts < MaxAttempts)
        {
            return CodeCheck.Invalid;
        }

        CodeHash = null;
        Salt = null;
        return CodeCheck.Burned;
    }

    private static byte[] Hash(byte[] salt, string code) => SHA256.HashData([.. salt, .. Encoding.UTF8.GetBytes(code)]);
}
