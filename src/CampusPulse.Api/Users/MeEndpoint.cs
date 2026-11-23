using System.Security.Claims;
using CampusPulse.Api.Accounts;
using CampusPulse.Api.Auth;
using CampusPulse.Api.Http;
using CampusPulse.Api.RateLimiting;
using CampusPulse.Domain.Users;
using CampusPulse.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CampusPulse.Api.Users;

/// <summary>A sign-in method linked to the account (today only <c>upt-microsoft</c>), with the email the provider
/// gives.</summary>
public sealed record IdentityResponse(string Provider, string? Email, DateTimeOffset LinkedAt);

/// <summary>Body of <c>GET /me</c> and <c>POST /me</c>, and <c>user</c> in every sign-in: the account.
/// <see cref="Email"/> is its verified email (null for an account made with UPT whose address another account has);
/// <see cref="UptLinked"/>: a UPT Microsoft account is linked.</summary>
public sealed record MeResponse(Guid Id, string? Email, string DisplayName, IReadOnlyList<IdentityResponse> Identities, bool UptLinked)
{
    internal static async Task<MeResponse> OfAsync(CampusPulseDbContext db, User user, CancellationToken cancellationToken)
    {
        var identities = await db.UserIdentities.AsNoTracking().Where(i => i.UserId == user.Id).OrderBy(i => i.LinkedAt)
            .Select(i => new IdentityResponse(i.Provider, i.Email, i.LinkedAt)).ToListAsync(cancellationToken);
        return new MeResponse(user.Id, user.Email, user.DisplayName, identities,
            identities.Any(i => i.Provider == UserIdentity.UptMicrosoft));
    }
}

/// <summary>
/// The signed-in student's own account, with either token (ours, or a UPT Entra token from an installed app version).
/// Reading it (<c>GET</c>) never creates it. With a UPT Entra token, <c>POST</c> creates it, as installed apps call it
/// right after an interactive sign-in; never on app start, or a second device that still holds tokens would silently
/// re-create an account the student deleted elsewhere. Our sign-ins create accounts in <c>/auth/*</c>.
/// </summary>
public static partial class MeEndpoint
{
    public const string LogCategory = "CampusPulse.Api.Users";

    public static IEndpointRouteBuilder MapMe(this IEndpointRouteBuilder app)
    {
        app.MapGet("/me", GetMe)
            .RequireAuthorization()
            .WithName("GetMe")
            .WithTags("Users")
            .WithHttpCaching(HttpCacheScope.PerStudent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        app.MapPost("/me", CreateMe)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitSetup.AccountPolicy)
            .WithName("CreateMe")
            .WithTags("Users")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        app.MapDelete("/me", DeleteMe)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitSetup.AccountPolicy)
            .WithName("DeleteMe")
            .WithTags("Users")
            .Produces(StatusCodes.Status401Unauthorized);

        app.MapPost("/me/identities/upt-microsoft", LinkUpt)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitSetup.AccountPolicy)
            .WithName("LinkUpt")
            .WithTags("Users")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        app.MapDelete("/me/identities/upt-microsoft", UnlinkUpt)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitSetup.AccountPolicy)
            .WithName("UnlinkUpt")
            .WithTags("Users")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }

    /// <summary>
    /// The signed-in student, or 404 when they have no account: never created, or deleted (maybe from another device,
    /// maybe by a DELETE overlapping this request). A client reads the 404 as "this account no longer exists" and signs
    /// out. Refreshes the last-seen time at most every few minutes, and with a UPT Entra token the name it carries.
    /// </summary>
    internal static async Task<Results<Ok<MeResponse>, ProblemHttpResult>> GetMe(ClaimsPrincipal principal,
        CampusPulseDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var user = await Caller.Users(db, principal).FirstOrDefaultAsync(cancellationToken);
        var name = Caller.IsUpt(principal) ? UptClaims.From(principal).Name : null;
        if (user is null || !await TrySeen(db, user, name, time.GetUtcNow(), cancellationToken))
        {
            return NoAccount();
        }

        return TypedResults.Ok(await MeResponse.OfAsync(db, user, cancellationToken));
    }

    /// <summary>
    /// With a UPT Entra token (installed app versions): creates the student's account, or returns the one their UPT
    /// account has (201 when created, 200 when it existed), linking it to an account with the same verified email as
    /// <c>POST /auth/microsoft</c> does. Two first calls at once make one account. With our token the account exists
    /// already: 200, or 404 once it is deleted.
    /// </summary>
    internal static async Task<Results<Created<MeResponse>, Ok<MeResponse>, ProblemHttpResult>> CreateMe(ClaimsPrincipal principal,
        CampusPulseDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (!Caller.IsUpt(principal))
        {
            var user = await Caller.Users(db, principal).AsNoTracking().FirstOrDefaultAsync(cancellationToken);
            return user is null ? NoAccount() : TypedResults.Ok(await MeResponse.OfAsync(db, user, cancellationToken));
        }

        var (account, created) = await SignIn.UptAsync(db, UptClaims.From(principal), time.GetUtcNow(), cancellationToken);
        var body = await MeResponse.OfAsync(db, account, cancellationToken);
        return created ? TypedResults.Created("/me", body) : TypedResults.Ok(body);
    }

    /// <summary>
    /// Deletes the signed-in student's account and everything stored about them, keeping only a count per UTC day
    /// (<see cref="AccountDeletion"/>). 204 also when there is nothing to delete, so a retry after a lost response is
    /// safe. The deleting client signs out right after: its access token stays valid until it expires, but finds no
    /// account, and its refresh tokens are gone.
    /// </summary>
    internal static async Task<NoContent> DeleteMe(ClaimsPrincipal principal, CampusPulseDbContext db,
        TimeProvider time, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var day = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        if (await Caller.IdAsync(db, principal, cancellationToken) is { } userId
            && await db.DeleteAccountAsync(userId, day, cancellationToken))
        {
            LogAccountDeleted(loggers.CreateLogger(LogCategory));
        }

        return TypedResults.NoContent();
    }

    /// <summary>
    /// Links a UPT Microsoft account (its Entra access token in the body) to the signed-in account, and answers the
    /// account. 200 also when it was linked already. 409 <c>identity-in-use</c> when that UPT account belongs to another
    /// CampusPulse account (accounts aren't merged), 409 <c>already-linked</c> when this account has a different UPT
    /// account; 401 <c>microsoft-token-invalid</c>; 503 <c>microsoft-unavailable</c>. An account without a verified
    /// email gets the UPT account's, when it is a UPT address and no other account has it.
    /// </summary>
    internal static async Task<Results<Ok<MeResponse>, ProblemHttpResult>> LinkUpt(MicrosoftTokenRequest request,
        ClaimsPrincipal principal, CampusPulseDbContext db, EntraTokenValidator entra, TimeProvider time, ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        if (!await Caller.Users(db, principal).AnyAsync(cancellationToken))
        {
            return NoAccount();
        }

        var (check, token) = await entra.ValidateAsync(request.AccessToken, cancellationToken);
        if (check != EntraCheck.Valid)
        {
            return AuthEndpoints.MicrosoftProblem(check);
        }

        var upt = UptClaims.From(token!);
        var now = time.GetUtcNow();
        // A unique key refuses the link when a request at the same moment got there first: the state is read again and
        // the checks below answer (this same link, made by a double tap, is a 200). Another account taking the address
        // means linking again without it. Should the clash persist, the key it hit says which conflict it is.
        var withEmail = true;
        for (var attempt = 1; ; attempt++)
        {
            var user = await Caller.Users(db, principal).FirstOrDefaultAsync(cancellationToken);
            if (user is null)
            {
                return NoAccount();
            }

            var owner = await db.UserIdentities.Where(i => i.Provider == UserIdentity.UptMicrosoft && i.Subject == upt.Oid)
                .Select(i => (Guid?)i.UserId).SingleOrDefaultAsync(cancellationToken)
                ?? await db.Users.Where(u => u.EntraObjectId == upt.Oid).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(cancellationToken);
            if (owner == user.Id && await db.UserIdentities.AnyAsync(i => i.UserId == user.Id && i.Subject == upt.Oid, cancellationToken))
            {
                return TypedResults.Ok(await MeResponse.OfAsync(db, user, cancellationToken));
            }

            if (owner is not null && owner != user.Id)
            {
                return IdentityInUse();
            }

            if (await db.UserIdentities.AnyAsync(i => i.UserId == user.Id && i.Provider == UserIdentity.UptMicrosoft, cancellationToken))
            {
                return AlreadyLinked();
            }

            user.LinkUpt(upt.Oid);
            user.Seen(upt.Name, now);
            if (withEmail && user.Email is null && upt.VerifiedEmail is { } email && !await db.Users.AnyAsync(u => u.Email == email, cancellationToken))
            {
                user.VerifyEmail(email);
            }

            db.UserIdentities.Add(new UserIdentity(user.Id, UserIdentity.UptMicrosoft, upt.Oid, upt.Email, now));
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                LogUptLinked(loggers.CreateLogger(LogCategory));
                return TypedResults.Ok(await MeResponse.OfAsync(db, user, cancellationToken));
            }
            catch (DbUpdateException e) when (SignIn.UniqueViolation(e) is { } key)
            {
                db.ChangeTracker.Clear();
                withEmail &= key != EmailKey;
                if (attempt == 3)
                {
                    return key == OneUptPerAccountKey ? AlreadyLinked() : IdentityInUse();
                }
            }
        }
    }

    /// <summary>Unlinks the account's UPT Microsoft account: 204, also when none was linked. 409
    /// <c>last-sign-in-method</c> when the account has no verified email, since it couldn't sign in again.</summary>
    internal static async Task<Results<NoContent, ProblemHttpResult>> UnlinkUpt(ClaimsPrincipal principal, CampusPulseDbContext db,
        ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var user = await Caller.Users(db, principal).FirstOrDefaultAsync(cancellationToken);
        if (user is null)
        {
            return NoAccount();
        }

        var identity = await db.UserIdentities.SingleOrDefaultAsync(i => i.UserId == user.Id && i.Provider == UserIdentity.UptMicrosoft,
            cancellationToken);
        if (identity is null && user.EntraObjectId is null)
        {
            return TypedResults.NoContent();
        }

        if (user.Email is null)
        {
            return Problems.Of(StatusCodes.Status409Conflict, "last-sign-in-method", "UPT is the only way to sign in",
                "This account has no verified email, so it couldn't sign in without UPT. Sign in with an email code first.");
        }

        if (identity is not null)
        {
            db.UserIdentities.Remove(identity);
        }

        user.UnlinkUpt();
        await db.SaveChangesAsync(cancellationToken);
        LogUptUnlinked(loggers.CreateLogger(LogCategory));
        return TypedResults.NoContent();
    }

    /// <summary>Records a request on a row this context loaded. False when the row was deleted after it was read (a
    /// DELETE /me overlapping this request): the update then matches no row, and the context is cleared so nothing of
    /// it is saved later.</summary>
    private static async Task<bool> TrySeen(CampusPulseDbContext db, User user, string? name, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!user.Seen(name, now))
        {
            return true;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    /// <summary>The signed-in student's account, or null when they have none (answer <see cref="NoAccount"/>). Read
    /// only (not tracked): callers use it to know who is asking.</summary>
    internal static Task<User?> FindUserAsync(CampusPulseDbContext db, ClaimsPrincipal principal, CancellationToken cancellationToken) =>
        Caller.Users(db, principal).AsNoTracking().FirstOrDefaultAsync(cancellationToken);

    internal static ProblemHttpResult NoAccount() => TypedResults.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "No account",
        detail: "This sign-in has no CampusPulse account: it was deleted or never created. Sign out; signing in again creates a new one.");

    /// <summary>The unique index on the account email, and the one that allows one UPT account per account.</summary>
    private const string EmailKey = "ix_users_verified_email";
    private const string OneUptPerAccountKey = "ix_user_identities_user_id_provider";

    private static ProblemHttpResult AlreadyLinked() => Problems.Of(StatusCodes.Status409Conflict, "already-linked",
        "Another UPT account is linked", "This account already has a UPT account. Unlink it first.");

    private static ProblemHttpResult IdentityInUse() => Problems.Of(StatusCodes.Status409Conflict, "identity-in-use",
        "UPT account in use", "This UPT account belongs to another CampusPulse account. Sign in with it there, or unlink it there first.");

    // No identifier of any kind: the student is gone.
    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "Account deleted")]
    private static partial void LogAccountDeleted(ILogger logger);

    [LoggerMessage(EventId = 21, Level = LogLevel.Information, Message = "UPT account linked")]
    private static partial void LogUptLinked(ILogger logger);

    [LoggerMessage(EventId = 22, Level = LogLevel.Information, Message = "UPT account unlinked")]
    private static partial void LogUptUnlinked(ILogger logger);
}
