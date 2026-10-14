using System.Security.Claims;
using CampusPulse.Domain.Users;
using CampusPulse.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CampusPulse.Api.Users;

/// <summary>Body of <c>GET /me</c>: the signed-in student.</summary>
public sealed record MeResponse(Guid Id, string Email, string DisplayName);

public static class MeEndpoint
{
    public static RouteHandlerBuilder MapMe(this IEndpointRouteBuilder app) =>
        app.MapGet("/me", GetMe)
            .RequireAuthorization()
            .WithName("GetMe")
            .WithTags("Users")
            .Produces<MeResponse>()
            .Produces(StatusCodes.Status401Unauthorized);

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

    private static string? FirstNonEmpty(ClaimsPrincipal principal, params string[] claimTypes) =>
        claimTypes.Select(type => principal.FindFirst(type)?.Value?.Trim()).FirstOrDefault(value => !string.IsNullOrEmpty(value));
}
