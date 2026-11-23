using System.Security.Cryptography;
using System.Text;

namespace CampusPulse.Domain.Users;

/// <summary>
/// One of an account's refresh tokens, kept only as its SHA-256 (<see cref="TokenHash"/>): the token itself is 256
/// random bits, base64url, and only the client has it. Every refresh replaces it with a new one of the same
/// <see cref="FamilyId"/> (one sign-in on one device), each valid for <see cref="Lifetime"/>. Presenting a token that
/// was already replaced means it was copied, and the whole family is revoked; except within <see cref="RetryWindow"/> of
/// its replacement while the replacement is unused (<see cref="MayRetry"/>, <see cref="Retry"/>): the answer to a refresh
/// got lost, or a client sent two at once. A family has one live token at any time. Deleted with the account.
/// </summary>
public sealed class RefreshToken
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(60);

    /// <summary>How long after its replacement a token may be refreshed again (see the class remarks).</summary>
    public static readonly TimeSpan RetryWindow = TimeSpan.FromMinutes(1);
    public const int MethodMaxLength = 32;

    private RefreshToken()
    {
    }

    private RefreshToken(Guid userId, Guid familyId, string method, string tokenHash, DateTimeOffset now)
    {
        UserId = userId;
        FamilyId = familyId;
        Method = method;
        TokenHash = tokenHash;
        CreatedAt = now;
        ExpiresAt = now + Lifetime;
    }

    /// <summary>The first token of a sign-in by <paramref name="method"/> (<c>email</c>, <c>upt-microsoft</c>), and the
    /// token to hand out.</summary>
    public static (RefreshToken Row, string Token) Start(Guid userId, string method, DateTimeOffset now)
    {
        var token = NewToken();
        return (new RefreshToken(userId, Guid.CreateVersion7(now), method, HashOf(token), now), token);
    }

    public string TokenHash { get; private set; } = "";

    public Guid UserId { get; private set; }

    public Guid FamilyId { get; private set; }

    /// <summary>How the family's sign-in was made; every access token it refreshes says so (<c>amr</c>).</summary>
    public string Method { get; private set; } = "";

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>The token that replaced this one; set means this one must never be shown again.</summary>
    public string? ReplacedByHash { get; private set; }

    public bool Usable(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    /// <summary>True when this token was replaced at most <see cref="RetryWindow"/> ago and hasn't expired: it may be
    /// refreshed once more if its <paramref name="replacement"/> is still unused and unrevoked, meaning the client never
    /// got it.</summary>
    public bool MayRetry(RefreshToken? replacement, DateTimeOffset now) =>
        ReplacedByHash is not null && RevokedAt is { } replacedAt && now - replacedAt <= RetryWindow && now < ExpiresAt
        && replacement is { RevokedAt: null } && replacement.TokenHash == ReplacedByHash;

    /// <summary>Replaces this token, refreshed again within its retry window (<see cref="MayRetry"/>), with a new next
    /// token of its family. <paramref name="replacement"/>, the unused one the lost answer held, is revoked and chained
    /// to the new one: the family keeps a single live token, and showing the superseded one later is a reuse like
    /// showing this one. This token's window still counts from its first replacement.</summary>
    public (RefreshToken Row, string Token) Retry(RefreshToken replacement, DateTimeOffset now)
    {
        if (!MayRetry(replacement, now))
        {
            throw new InvalidOperationException("Only a token in its retry window, with its unused replacement, can be retried.");
        }

        var (next, token) = Rotate(now);
        replacement.RevokedAt = now;
        replacement.ReplacedByHash = next.TokenHash;
        return (next, token);
    }

    /// <summary>Replaces this token with the next of its family, and returns it with the token to hand out.</summary>
    public (RefreshToken Row, string Token) Rotate(DateTimeOffset now)
    {
        var token = NewToken();
        var next = new RefreshToken(UserId, FamilyId, Method, HashOf(token), now);
        RevokedAt ??= now;
        ReplacedByHash = next.TokenHash;
        return (next, token);
    }

    /// <summary>The stored form of a token: its SHA-256, lowercase hex.</summary>
    public static string HashOf(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
