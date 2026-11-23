using System.Security.Cryptography;
using System.Text;

namespace CampusPulse.Domain.Users;

/// <summary>
/// One of an account's refresh tokens, kept only as its SHA-256 (<see cref="TokenHash"/>): the token itself is 256
/// random bits, base64url, and only the client has it. Every refresh replaces it with a new one of the same
/// <see cref="FamilyId"/> (one sign-in on one device), each valid for <see cref="Lifetime"/>. Presenting a token that
/// was already replaced means it was copied, and the whole family is revoked; except within <see cref="RetryWindow"/> of
/// its replacement while the replacement is unused (<see cref="MayRetry"/>): the answer to a refresh got lost, or a client
/// sent two at once, and the same replacement is handed out again (the API derives a token's replacement from it, so a
/// retry yields the same one). A family has one live token at any time. Deleted with the account.
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

    /// <summary>True when this token was replaced at most <see cref="RetryWindow"/> ago and hasn't expired, and its
    /// <paramref name="replacement"/> is still unused and unrevoked: the client may not have got it, so it may be handed
    /// out again.</summary>
    public bool MayRetry(RefreshToken? replacement, DateTimeOffset now) =>
        ReplacedByHash is not null && RevokedAt is { } replacedAt && now - replacedAt < RetryWindow && now < ExpiresAt
        && replacement is { RevokedAt: null } && replacement.TokenHash == ReplacedByHash;

    /// <summary>Replaces this token with <paramref name="nextToken"/>, the next of its family (the API derives it from
    /// this token, so a retry yields the same one), and returns its row.</summary>
    public RefreshToken Rotate(string nextToken, DateTimeOffset now)
    {
        var next = new RefreshToken(UserId, FamilyId, Method, HashOf(nextToken), now);
        RevokedAt = now;
        ReplacedByHash = next.TokenHash;
        return next;
    }

    /// <summary>The stored form of a token: its SHA-256, lowercase hex.</summary>
    public static string HashOf(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
