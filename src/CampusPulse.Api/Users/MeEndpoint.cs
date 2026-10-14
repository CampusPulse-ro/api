using System.Security.Claims;
using CampusPulse.Domain.Users;
using CampusPulse.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CampusPulse.Api.Users;

/// <summary>Body of <c>GET /me</c> and <c>POST /me</c>: the signed-in student.</summary>
public sealed record MeResponse(Guid Id, string Email, string DisplayName)
{
    internal static MeResponse From(User user) => new(user.Id, user.Email, user.DisplayName);
}

/// <summary>
/// The signed-in student's own account. Reading it (<c>GET</c>) never creates it: only <c>POST</c>,
/// which clients call right after an interactive sign-in. Otherwise a second device that still holds
/// tokens would silently re-create an account the student deleted elsewhere.
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
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        app.MapPost("/me", CreateMe)
            .RequireAuthorization()
            .WithName("CreateMe")
            .WithTags("Users")
            .Produces(StatusCodes.Status401Unauthorized);

        app.MapDelete("/me", DeleteMe)
            .RequireAuthorization()
            .WithName("DeleteMe")
            .WithTags("Users")
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }

    /// <summary>
    /// The signed-in student, or 404 when they have no account: never created, or deleted (maybe
    /// from another device, maybe by a DELETE overlapping this request). A client reads the 404 as
    /// "this account no longer exists" and signs out. Refreshes email and name when the token has
    /// them and they changed (a token requested without the <c>profile</c> scope may lack
    /// <c>name</c> and <c>preferred_username</c>), and the last-seen time at most every few minutes.
    /// </summary>
    internal static async Task<Results<Ok<MeResponse>, ProblemHttpResult>> GetMe(ClaimsPrincipal principal,
        CampusPulseDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var identity = Identity.From(principal);

        var user = await db.Users.SingleOrDefaultAsync(u => u.EntraObjectId == identity.Oid, cancellationToken);
        if (user is null || !await TrySeen(db, user, identity, time.GetUtcNow(), cancellationToken))
        {
            return NoAccount();
        }

        return TypedResults.Ok(MeResponse.From(user));
    }

    /// <summary>
    /// Creates the signed-in student's account, or returns the one they have: 201 when created, 200
    /// when it existed, with GET's body either way. Clients call it once after an interactive
    /// sign-in, never on app start or a silent token refresh. Two first calls at once can't make two
    /// rows: the unique index refuses the second insert, which then returns the row the first one
    /// made. If that row is already gone (deleted in between), the insert is tried once more.
    /// </summary>
    internal static async Task<Results<Created<MeResponse>, Ok<MeResponse>>> CreateMe(ClaimsPrincipal principal,
        CampusPulseDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var identity = Identity.From(principal);
        var now = time.GetUtcNow();

        for (var attempt = 1; ; attempt++)
        {
            var existing = await db.Users.SingleOrDefaultAsync(u => u.EntraObjectId == identity.Oid, cancellationToken);
            if (existing is not null && await TrySeen(db, existing, identity, now, cancellationToken))
            {
                return TypedResults.Ok(MeResponse.From(existing));
            }

            // No row, or it was deleted between the read and the update: create the account.
            var user = new User(identity.Oid, identity.Email, identity.Name, now);
            db.Users.Add(user);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return TypedResults.Created("/me", MeResponse.From(user));
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear();
                var winner = await db.Users.SingleOrDefaultAsync(u => u.EntraObjectId == identity.Oid, cancellationToken);
                if (winner is not null)
                {
                    return TypedResults.Ok(MeResponse.From(winner));
                }

                if (attempt == 2)
                {
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// Deletes the signed-in student's account and everything stored about them, keeping only a
    /// count per UTC day (<see cref="AccountDeletion"/>). 204 also when there is nothing to delete, so
    /// a retry after a lost response is safe. The deleting client signs out right after: its token
    /// stays valid until it expires.
    /// </summary>
    internal static async Task<NoContent> DeleteMe(ClaimsPrincipal principal, CampusPulseDbContext db,
        TimeProvider time, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var identity = Identity.From(principal);
        var day = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

        if (await db.DeleteAccountAsync(identity.Oid, day, cancellationToken))
        {
            LogAccountDeleted(loggers.CreateLogger(LogCategory));
        }

        return TypedResults.NoContent();
    }

    /// <summary>Records a sign-in on a row this context loaded. False when the row was deleted after
    /// it was read (a DELETE /me overlapping this request): the update then matches no row, and the
    /// context is cleared so nothing of it is saved later.</summary>
    private static async Task<bool> TrySeen(CampusPulseDbContext db, User user, Identity identity,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!user.Seen(identity.Email, identity.Name, now))
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

    private static ProblemHttpResult NoAccount() => TypedResults.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "No account",
        detail: "This Microsoft account has no CampusPulse account: it was deleted or never created. Sign out; signing in again creates a new one.");

    // No identifier of any kind: the student is gone.
    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "Account deleted")]
    private static partial void LogAccountDeleted(ILogger logger);

    /// <summary>What the API takes from the validated token.</summary>
    private sealed record Identity(string Oid, string? Email, string? Name)
    {
        public static Identity From(ClaimsPrincipal principal) => new(
            principal.FindFirst("oid")?.Value
                ?? throw new InvalidOperationException("An authenticated principal without oid (the token check should have refused it)."),
            FirstNonEmpty(principal, "email", "preferred_username"),
            FirstNonEmpty(principal, "name"));

        private static string? FirstNonEmpty(ClaimsPrincipal principal, params string[] claimTypes) =>
            claimTypes.Select(type => principal.FindFirst(type)?.Value?.Trim()).FirstOrDefault(value => !string.IsNullOrEmpty(value));
    }
}
