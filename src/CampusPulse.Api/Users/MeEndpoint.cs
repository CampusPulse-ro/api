using System.Security.Claims;
using CampusPulse.Domain.Users;
using CampusPulse.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CampusPulse.Api.Users;

/// <summary>Body of <c>GET /me</c>: the signed-in student.</summary>
public sealed record MeResponse(Guid Id, string Email, string DisplayName);

public static partial class MeEndpoint
{
    public const string LogCategory = "CampusPulse.Api.Users";

    public static IEndpointRouteBuilder MapMe(this IEndpointRouteBuilder app)
    {
        app.MapGet("/me", GetMe)
            .RequireAuthorization()
            .WithName("GetMe")
            .WithTags("Users")
            .Produces<MeResponse>()
            .Produces(StatusCodes.Status401Unauthorized);

        app.MapDelete("/me", DeleteMe)
            .RequireAuthorization()
            .WithName("DeleteMe")
            .WithTags("Users")
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }

    /// <summary>
    /// The signed-in student, created on their first call (keyed by the token's <c>oid</c>). Later
    /// calls refresh email and name when the token has them and they changed (a token requested
    /// without the <c>profile</c> scope may lack <c>name</c> and <c>preferred_username</c>), and the
    /// last-seen time at most every few minutes. Two first calls at once can't make two rows: the unique index refuses the second
    /// insert, which then reads the row the first one made.
    /// </summary>
    internal static async Task<MeResponse> GetMe(ClaimsPrincipal principal, CampusPulseDbContext db,
        TimeProvider time, CancellationToken cancellationToken)
    {
        var oid = principal.FindFirst("oid")?.Value
            ?? throw new InvalidOperationException("An authenticated principal without oid (the token check should have refused it).");
        var email = FirstNonEmpty(principal, "email", "preferred_username");
        var name = FirstNonEmpty(principal, "name");
        var now = time.GetUtcNow();

        var user = await db.Users.SingleOrDefaultAsync(u => u.EntraObjectId == oid, cancellationToken);
        if (user is null)
        {
            user = new User(oid, email, name, now);
            db.Users.Add(user);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear();
                user = await db.Users.SingleAsync(u => u.EntraObjectId == oid, cancellationToken);
            }
        }
        else if (user.Seen(email, name, now))
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return new MeResponse(user.Id, user.Email, user.DisplayName);
    }

    /// <summary>
    /// Deletes the signed-in student's account and everything stored about them, keeping only a
    /// count per UTC day (<see cref="AccountDeletion"/>). 204 also when there is nothing to delete, so
    /// a retry after a lost response is safe. The token stays valid until it expires and a later
    /// <c>GET /me</c> with it would create a new, empty student, so clients sign out right after.
    /// </summary>
    internal static async Task<NoContent> DeleteMe(ClaimsPrincipal principal, CampusPulseDbContext db,
        TimeProvider time, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var oid = principal.FindFirst("oid")?.Value
            ?? throw new InvalidOperationException("An authenticated principal without oid (the token check should have refused it).");
        var day = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

        if (await db.DeleteAccountAsync(oid, day, cancellationToken))
        {
            LogAccountDeleted(loggers.CreateLogger(LogCategory));
        }

        return TypedResults.NoContent();
    }

    // No identifier of any kind: the student is gone, and the line keeps only the request id.
    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "Account deleted")]
    private static partial void LogAccountDeleted(ILogger logger);

    private static string? FirstNonEmpty(ClaimsPrincipal principal, params string[] claimTypes) =>
        claimTypes.Select(type => principal.FindFirst(type)?.Value?.Trim()).FirstOrDefault(value => !string.IsNullOrEmpty(value));
}
