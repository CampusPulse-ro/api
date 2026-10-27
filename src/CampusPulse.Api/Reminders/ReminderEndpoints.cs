using System.Security.Claims;
using CampusPulse.Api.Users;
using CampusPulse.Domain.Reminders;
using CampusPulse.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CampusPulse.Api.Reminders;

/// <summary>Body of <c>GET /me/reminders</c> and <c>PUT /me/reminders</c>: how many minutes before each class the
/// student is reminded, or null when reminders are off.</summary>
public sealed record ReminderSettingsResponse(int? MinutesBefore);

/// <summary>Body of <c>PUT /me/reminders</c>: 5, 10, 15, 30 or 60 minutes before each class, or null to turn
/// reminders off. The field is required, null included.</summary>
public sealed record UpdateReminderSettingsRequest(int? MinutesBefore);

/// <summary>The student's class reminders setting (CampusPulse-ro/api#53); <see cref="ClassReminderSender"/> sends
/// them.</summary>
public static class ReminderEndpoints
{
    public static IEndpointRouteBuilder MapReminders(this IEndpointRouteBuilder app)
    {
        app.MapGet("/me/reminders", GetReminders)
            .RequireAuthorization()
            .WithName("GetReminderSettings")
            .WithTags("Reminders")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        app.MapPut("/me/reminders", UpdateReminders)
            .RequireAuthorization()
            .WithName("UpdateReminderSettings")
            .WithTags("Reminders")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }

    /// <summary>The signed-in student's lead in minutes, or null when reminders are off (the default). 404 without
    /// an account, like <c>GET /me</c>.</summary>
    internal static async Task<Results<Ok<ReminderSettingsResponse>, ProblemHttpResult>> GetReminders(ClaimsPrincipal principal,
        CampusPulseDbContext db, CancellationToken cancellationToken)
    {
        if (await UserIdAsync(db, principal, cancellationToken) is not { } userId)
        {
            return MeEndpoint.NoAccount();
        }

        var minutes = await db.ReminderSettings.Where(r => r.UserId == userId)
            .Select(r => (int?)r.MinutesBefore)
            .SingleOrDefaultAsync(cancellationToken);
        return TypedResults.Ok(new ReminderSettingsResponse(minutes));
    }

    /// <summary>
    /// Sets the signed-in student's lead (5, 10, 15, 30 or 60 minutes), or turns reminders off (null), and answers
    /// the setting as stored. 400 for any other value, 404 without an account. An upsert in one statement, so two
    /// devices saving at once can't collide; an account deleted in between is a 404.
    /// </summary>
    internal static async Task<Results<Ok<ReminderSettingsResponse>, ProblemHttpResult>> UpdateReminders(
        UpdateReminderSettingsRequest request, ClaimsPrincipal principal, CampusPulseDbContext db, TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (request.MinutesBefore is { } minutes && !ReminderSettings.IsAllowed(minutes))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not a reminder time",
                detail: $"minutesBefore must be one of {string.Join(", ", ReminderSettings.AllowedMinutes)}, or null for no reminders.");
        }

        if (await UserIdAsync(db, principal, cancellationToken) is not { } userId)
        {
            return MeEndpoint.NoAccount();
        }

        if (request.MinutesBefore is not { } lead)
        {
            await db.ReminderSettings.Where(r => r.UserId == userId).ExecuteDeleteAsync(cancellationToken);
            return TypedResults.Ok(new ReminderSettingsResponse(null));
        }

        try
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO reminder_settings (user_id, minutes_before, updated_at) VALUES ({userId}, {lead}, {time.GetUtcNow()})
                ON CONFLICT (user_id) DO UPDATE SET minutes_before = excluded.minutes_before, updated_at = excluded.updated_at
                """, cancellationToken);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return MeEndpoint.NoAccount(); // deleted (DELETE /me from another device) since the read above
        }

        return TypedResults.Ok(new ReminderSettingsResponse(lead));
    }

    private static async Task<Guid?> UserIdAsync(CampusPulseDbContext db, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var oid = MeEndpoint.Identity.From(principal).Oid;
        return await db.Users.Where(u => u.EntraObjectId == oid).Select(u => (Guid?)u.Id).SingleOrDefaultAsync(cancellationToken);
    }
}
