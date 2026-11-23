using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CampusPulse.Api.Auth;

/// <summary>
/// The API's own access tokens: JWTs it signs with <c>Auth:SigningKey</c> (HS256, at least 32 bytes, base64), issuer
/// <see cref="Issuer"/>, audience <see cref="Audience"/>, <c>sub</c> = the account id, <c>amr</c> = how the student
/// signed in, valid <see cref="AccessTokenLifetime"/>. Stateless to check: the refresh token (stored, revocable) is what
/// keeps a session. Without a key (and outside Development) no token is issued or accepted, and sign-in answers 503.
/// </summary>
public sealed class CampusPulseTokens(SymmetricSecurityKey? key, TimeProvider time)
{
    public const string Scheme = "CampusPulse";
    public const string Issuer = "campuspulse";
    public const string Audience = "campuspulse-api";
    public const int MinimumKeyBytes = 32;
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);

    public bool IsConfigured => key is not null;

    /// <summary>A new access token for <paramref name="userId"/>, signed in by <paramref name="method"/>.</summary>
    public (string Token, DateTimeOffset ExpiresAt) Issue(Guid userId, string method)
    {
        if (key is null)
        {
            throw new InvalidOperationException("Auth__SigningKey is not set: no access token can be issued.");
        }

        var now = time.GetUtcNow();
        var expires = now + AccessTokenLifetime;
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Claims = new Dictionary<string, object> { ["sub"] = userId.ToString(), ["amr"] = new[] { method } },
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }

    /// <summary>How the <see cref="Scheme"/> bearer checks a token: this issuer, audience and key, HS256 only, not
    /// expired (a minute of skew).</summary>
    public static TokenValidationParameters ValidationParameters(SecurityKey key) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = Issuer,
        ValidateAudience = true,
        ValidAudience = Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = key,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ValidateLifetime = true,
        RequireExpirationTime = true,
        ClockSkew = TimeSpan.FromMinutes(1),
        NameClaimType = "sub",
    };

    /// <summary>The account id of a principal signed in with one of our tokens; null for a UPT Entra token.</summary>
    public static Guid? UserIdOf(ClaimsPrincipal principal) =>
        principal.FindFirst("iss")?.Value == Issuer && Guid.TryParse(principal.FindFirst("sub")?.Value, out var id) ? id : null;

    /// <summary>The key from <c>Auth:SigningKey</c>. Unset: in Development a random one for this process (tokens die
    /// with it; <paramref name="ephemeral"/>), else null. Set but not base64 of at least 32 bytes: the start fails.</summary>
    public static SymmetricSecurityKey? KeyFrom(IConfiguration configuration, IHostEnvironment environment, out bool ephemeral)
    {
        ephemeral = false;
        var text = configuration["Auth:SigningKey"];
        if (string.IsNullOrWhiteSpace(text))
        {
            if (!environment.IsDevelopment())
            {
                return null;
            }

            ephemeral = true;
            return new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(MinimumKeyBytes));
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(text.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Auth__SigningKey must be base64.");
        }

        return bytes.Length >= MinimumKeyBytes
            ? new SymmetricSecurityKey(bytes)
            : throw new InvalidOperationException($"Auth__SigningKey must be at least {MinimumKeyBytes} bytes (base64 of `openssl rand 32`).");
    }
}
