using System.Security.Claims;
using CampusPulse.Api.Auth;
using CampusPulse.Domain.Users;
using CampusPulse.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CampusPulse.Api.Users;

/// <summary>
/// Who is calling: the account behind the bearer token, whichever scheme it came by. Our own token names the account
/// (<c>sub</c>); a UPT Entra token (installed app versions) names a UPT account (<c>oid</c>), whose account is found
/// through its <c>upt-microsoft</c> identity, or through <c>users.entra_object_id</c> for a student the previous release
/// created after this one rolled back (it wrote no identity).
/// </summary>
public static class Caller
{
    /// <summary>The caller's account, as a query (no row when it has none: never created, or deleted).</summary>
    public static IQueryable<User> Users(CampusPulseDbContext db, ClaimsPrincipal principal)
    {
        if (CampusPulseTokens.UserIdOf(principal) is { } id)
        {
            return db.Users.Where(u => u.Id == id);
        }

        var oid = UptClaims.From(principal).Oid;
        return db.Users.Where(u => u.EntraObjectId == oid
            || db.UserIdentities.Any(i => i.Provider == UserIdentity.UptMicrosoft && i.Subject == oid && i.UserId == u.Id));
    }

    /// <summary>The caller's account id, or null when they have no account.</summary>
    public static Task<Guid?> IdAsync(CampusPulseDbContext db, ClaimsPrincipal principal, CancellationToken cancellationToken) =>
        Users(db, principal).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(cancellationToken);

    /// <summary>True when the caller came with a UPT Entra token rather than one of ours.</summary>
    public static bool IsUpt(ClaimsPrincipal principal) => CampusPulseTokens.UserIdOf(principal) is null;
}

/// <summary>What the API takes from a validated UPT Entra token: the account's <c>oid</c>, its email
/// (<c>email</c>, else the UPN in <c>preferred_username</c>; normalized) and the name.</summary>
public sealed record UptClaims(string Oid, string? Email, string? Name)
{
    /// <summary><see cref="Email"/> when it is one of UPT's own addresses (<see cref="Emails.IsUpt"/>), else null. Only
    /// this one links accounts or becomes an account's email: Microsoft doesn't vouch for a token's email claim, and a
    /// tenant can put any address there.</summary>
    public string? VerifiedEmail => Email is { } email && Emails.IsUpt(email) ? email : null;

    public static UptClaims From(ClaimsPrincipal principal)
    {
        var email = FirstNonEmpty(principal, "email", "preferred_username");
        return new UptClaims(
            principal.FindFirst("oid")?.Value
                ?? throw new InvalidOperationException("An Entra principal without oid (the token check should have refused it)."),
            Emails.IsValid(email) ? Emails.Normalize(email!) : null,
            FirstNonEmpty(principal, "name"));
    }

    private static string? FirstNonEmpty(ClaimsPrincipal principal, params string[] claimTypes) =>
        claimTypes.Select(type => principal.FindFirst(type)?.Value?.Trim()).FirstOrDefault(value => !string.IsNullOrEmpty(value));
}
