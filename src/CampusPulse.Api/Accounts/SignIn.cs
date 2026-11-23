using CampusPulse.Api.Auth;
using CampusPulse.Api.Users;
using CampusPulse.Domain.Users;
using CampusPulse.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CampusPulse.Api.Accounts;

/// <summary>Body of every sign-in and of <c>POST /auth/refresh</c>: our access token (15 min) and refresh token (60 days,
/// replaced on every refresh), and the account (<c>GET /me</c>'s body). <see cref="IsNew"/>: this sign-in created it.</summary>
public sealed record TokenResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    bool IsNew,
    MeResponse User);

/// <summary>
/// Finding, linking and creating accounts when someone signs in, and the tokens a sign-in gets. Email addresses are
/// compared normalized (trimmed, lowercase); both an address confirmed with a code and a UPT account's address count as
/// verified, so an address joins the two.
/// </summary>
public static class SignIn
{
    public const string EmailMethod = "email";

    /// <summary>
    /// The account of a UPT (Entra) sign-in, and whether it was created now. In order: the account its
    /// <c>upt-microsoft</c> identity points at; an account the previous release made for it (no identity yet: one is
    /// added); an account whose verified email is the UPT account's, without a UPT account of its own (linked
    /// automatically: both addresses are verified); else a new account, with the UPT email unless another account
    /// has it. Only a UPT address counts (<see cref="UptClaims.VerifiedEmail"/>). Two first sign-ins at once make one
    /// account: the unique keys refuse the second, which then finds the first's.
    /// </summary>
    public static async Task<(User User, bool Created)> UptAsync(CampusPulseDbContext db, UptClaims upt, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var identity = await db.UserIdentities
                    .SingleOrDefaultAsync(i => i.Provider == UserIdentity.UptMicrosoft && i.Subject == upt.Oid, cancellationToken);
                if (identity is not null)
                {
                    var linked = await db.Users.SingleAsync(u => u.Id == identity.UserId, cancellationToken);
                    if (identity.Refresh(upt.Email) | linked.Seen(upt.Name, now))
                    {
                        await db.SaveChangesAsync(cancellationToken);
                    }

                    return (linked, false);
                }

                var verified = upt.VerifiedEmail;
                var existing = await db.Users.FirstOrDefaultAsync(u => u.EntraObjectId == upt.Oid, cancellationToken);
                if (existing is null && verified is { } email)
                {
                    existing = await db.Users.FirstOrDefaultAsync(u => u.Email == email
                        && !db.UserIdentities.Any(i => i.UserId == u.Id && i.Provider == UserIdentity.UptMicrosoft), cancellationToken);
                }

                var created = existing is null;
                var emailFree = verified is not null && !await db.Users.AnyAsync(u => u.Email == verified, cancellationToken);
                User user;
                if (existing is null)
                {
                    user = User.FromUpt(upt.Oid, emailFree ? verified : null, upt.Name, now);
                    db.Users.Add(user);
                }
                else
                {
                    user = existing;
                    user.LinkUpt(upt.Oid);
                    user.Seen(upt.Name, now);
                    if (emailFree)
                    {
                        user.VerifyEmail(verified!);
                    }
                }

                db.UserIdentities.Add(new UserIdentity(user.Id, UserIdentity.UptMicrosoft, upt.Oid, upt.Email, now));
                await db.SaveChangesAsync(cancellationToken);
                return (user, created);
            }
            catch (DbUpdateException e) when (attempt < 3 && IsUniqueViolation(e))
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    /// The account of an email sign-in (the code was right), and whether it was created now: the account with that
    /// verified email, else one whose linked UPT account has it (a UPT address only; it becomes the account's email if it
    /// has none), else a new account.
    /// </summary>
    public static async Task<(User User, bool Created)> EmailAsync(CampusPulseDbContext db, string email, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);
                if (user is null && Emails.IsUpt(email))
                {
                    user = await db.Users.Where(u => db.UserIdentities.Any(i => i.UserId == u.Id && i.Email == email))
                        .OrderBy(u => u.CreatedAt).FirstOrDefaultAsync(cancellationToken);
                }

                if (user is not null)
                {
                    user.VerifyEmail(email);
                    user.Seen(null, now);
                    await db.SaveChangesAsync(cancellationToken);
                    return (user, false);
                }

                user = User.FromEmail(email, now);
                db.Users.Add(user);
                await db.SaveChangesAsync(cancellationToken);
                return (user, true);
            }
            catch (DbUpdateException e) when (attempt < 3 && IsUniqueViolation(e))
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>A new session for <paramref name="user"/>: a refresh token family started by <paramref name="method"/>,
    /// and its first access token.</summary>
    public static async Task<TokenResponse> StartSessionAsync(CampusPulseDbContext db, CampusPulseTokens tokens, User user,
        string method, bool isNew, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (row, refreshToken) = RefreshToken.Start(user.Id, method, now);
        db.RefreshTokens.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        var (accessToken, accessExpiresAt) = tokens.Issue(user.Id, method);
        return new TokenResponse(accessToken, accessExpiresAt, refreshToken, row.ExpiresAt, isNew,
            await MeResponse.OfAsync(db, user, cancellationToken));
    }

    /// <summary>How a refresh came out.</summary>
    public enum RefreshResult
    {
        Refreshed,

        /// <summary>Unknown, expired or revoked.</summary>
        Invalid,

        /// <summary>Already replaced: it was copied, and its whole family is now revoked.</summary>
        Reused,
    }

    /// <summary>Replaces <paramref name="presented"/> with the next token of its family. A token that was already
    /// replaced revokes the family (whoever holds the newest one is signed out too), unless it was replaced less than
    /// <see cref="RefreshToken.RetryWindow"/> ago and its replacement is unused (<see cref="RefreshToken.MayRetry"/>): the
    /// client never got that answer, or sent two refreshes at once, and gets new tokens again. The account's row and then
    /// the token's are locked, in the order <c>DELETE /me</c> locks them (so the two can't deadlock), and two refreshes of
    /// one token run one after the other. A retry gets the replacement the first refresh made (it is derived from the
    /// presented token, <see cref="CampusPulseTokens.NextRefreshToken"/>), so a family never has two live tokens and every
    /// answer the client may keep holds the live one.</summary>
    public static async Task<(RefreshResult Result, TokenResponse? Response)> RefreshAsync(CampusPulseDbContext db, CampusPulseTokens tokens,
        string presented, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var hash = RefreshToken.HashOf(presented);
        var userId = await db.RefreshTokens.Where(t => t.TokenHash == hash).Select(t => (Guid?)t.UserId).SingleOrDefaultAsync(cancellationToken);
        if (userId is null)
        {
            return (RefreshResult.Invalid, null);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = (await db.Users.FromSql($"SELECT * FROM users WHERE id = {userId} FOR UPDATE").ToListAsync(cancellationToken)).SingleOrDefault();
        var row = user is null
            ? null
            : (await db.RefreshTokens.FromSql($"SELECT * FROM refresh_tokens WHERE token_hash = {hash} FOR UPDATE")
                .ToListAsync(cancellationToken)).SingleOrDefault();
        if (user is null || row is null)
        {
            return (RefreshResult.Invalid, null);
        }

        var refreshToken = tokens.NextRefreshToken(presented);
        RefreshToken next;
        if (row.ReplacedByHash is not null)
        {
            // A retry gets the same replacement again, as long as it is the one derived from this token (a token rotated
            // before replacements were derived, or under another signing key, isn't) and still unused.
            var replacement = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == row.ReplacedByHash, cancellationToken);
            if (!row.MayRetry(replacement, now) || replacement!.TokenHash != RefreshToken.HashOf(refreshToken))
            {
                await RevokeFamilyAsync(db, row.FamilyId, now, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return (RefreshResult.Reused, null);
            }

            next = replacement;
        }
        else if (!row.Usable(now))
        {
            return (RefreshResult.Invalid, null);
        }
        else
        {
            next = row.Rotate(refreshToken, now);
            db.RefreshTokens.Add(next);
        }

        user.Seen(null, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var (accessToken, accessExpiresAt) = tokens.Issue(user.Id, row.Method);
        return (RefreshResult.Refreshed, new TokenResponse(accessToken, accessExpiresAt, refreshToken, next.ExpiresAt, IsNew: false,
            await MeResponse.OfAsync(db, user, cancellationToken)));
    }

    /// <summary>Signs a session out: revokes the family of <paramref name="presented"/>, if it is one of ours.</summary>
    public static async Task SignOutAsync(CampusPulseDbContext db, string presented, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var hash = RefreshToken.HashOf(presented);
        var family = await db.RefreshTokens.Where(t => t.TokenHash == hash).Select(t => (Guid?)t.FamilyId).SingleOrDefaultAsync(cancellationToken);
        if (family is { } familyId)
        {
            await RevokeFamilyAsync(db, familyId, now, cancellationToken);
        }
    }

    private static Task<int> RevokeFamilyAsync(CampusPulseDbContext db, Guid familyId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.RevokedAt, now), cancellationToken);

    internal static bool IsUniqueViolation(DbUpdateException e) => UniqueViolation(e) is not null;

    /// <summary>The unique constraint or index <paramref name="e"/> broke, or null when it isn't a unique
    /// violation.</summary>
    internal static string? UniqueViolation(DbUpdateException e) =>
        e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } p ? p.ConstraintName ?? "" : null;
}
