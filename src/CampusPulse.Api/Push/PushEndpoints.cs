using System.Security.Claims;
using CampusPulse.Api.Users;
using CampusPulse.Domain.Push;
using CampusPulse.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CampusPulse.Api.Push;

/// <summary>Body of <c>POST /me/devices</c>: the app's Expo push token and its platform.</summary>
public sealed record RegisterDeviceRequest(string Token, PushPlatform Platform);

/// <summary>Body of <c>DELETE /me/devices</c>. The token goes in the body, not the path: paths are
/// logged (here and by the reverse proxy), and a push token must never be.</summary>
public sealed record UnregisterDeviceRequest(string Token);

/// <summary>A registered device, as <c>POST /me/devices</c> answers it.</summary>
public sealed record DeviceResponse(Guid Id, PushPlatform Platform, DateTimeOffset CreatedAt, DateTimeOffset LastRegisteredAt)
{
    internal static DeviceResponse From(PushDevice device) =>
        new(device.Id, device.Platform, device.CreatedAt, device.LastRegisteredAt);
}

/// <summary>Body of <c>POST /me/notifications/test</c>: how many devices the push service accepted
/// the notification for.</summary>
public sealed record TestNotificationResponse(int Sent);

public static class PushEndpoints
{
    public const string TestTitle = "CampusPulse";
    public const string TestBody = "CampusPulse test notification";

    public static IEndpointRouteBuilder MapPush(this IEndpointRouteBuilder app)
    {
        app.MapPost("/me/devices", RegisterDevice)
            .RequireAuthorization()
            .WithName("RegisterPushDevice")
            .WithTags("Push")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        app.MapDelete("/me/devices", UnregisterDevice)
            .RequireAuthorization()
            .WithName("UnregisterPushDevice")
            .WithTags("Push")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);

        app.MapPost("/me/notifications/test", SendTestNotification)
            .RequireAuthorization()
            .WithName("SendTestNotification")
            .WithTags("Push")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }

    /// <summary>
    /// Registers the app's push token for the signed-in student: 201 when it's new, 200 when it was
    /// known (its last-registered time is refreshed, and if another student had it, it moves to this
    /// one). 400 unless it is an Expo push token, 404 without an account (like <c>GET /me</c>). A
    /// student keeps at most <see cref="PushDevice.MaxPerUser"/> devices: registering one more drops
    /// the least recently registered.
    /// </summary>
    internal static async Task<Results<Created<DeviceResponse>, Ok<DeviceResponse>, ProblemHttpResult>> RegisterDevice(
        RegisterDeviceRequest request, ClaimsPrincipal principal, CampusPulseDbContext db, TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (!PushDevice.IsExpoToken(request.Token))
        {
            // Never echo the value: it may be a real token in the wrong shape.
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not an Expo push token",
                detail: "token must look like ExponentPushToken[…] (from getExpoPushTokenAsync).");
        }

        var identity = MeEndpoint.Identity.From(principal);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RegisterOnceAsync(request, identity, db, time.GetUtcNow(), cancellationToken);
            }
            catch (DbUpdateException e) when (attempt == 1 && (e is DbUpdateConcurrencyException
                || e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }))
            {
                // Someone else changed this token's row between the read and the write: another
                // student registered it at the same moment (one phone, two sign-ins; the insert hits
                // the unique index), or it was moved or deleted (the update matches no row). The
                // second try reads it again.
                db.ChangeTracker.Clear();
            }
        }
    }

    private static async Task<Results<Created<DeviceResponse>, Ok<DeviceResponse>, ProblemHttpResult>> RegisterOnceAsync(
        RegisterDeviceRequest request, MeEndpoint.Identity identity, CampusPulseDbContext db, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Locks the student's row: their registrations run one at a time (so the cap holds), and a
        // DELETE /me either waits for this one or has already removed the row (then: 404).
        var user = (await db.Users
            .FromSql($"SELECT * FROM users WHERE entra_object_id = {identity.Oid} FOR UPDATE")
            .ToListAsync(cancellationToken)).SingleOrDefault();
        if (user is null)
        {
            return MeEndpoint.NoAccount();
        }

        var device = await db.PushDevices.SingleOrDefaultAsync(d => d.Token == request.Token, cancellationToken);
        var created = device is null;
        if (device is null)
        {
            device = new PushDevice(user.Id, request.Token, request.Platform, now);
            db.PushDevices.Add(device);
        }
        else
        {
            device.Register(user.Id, request.Platform, now);
        }

        await db.SaveChangesAsync(cancellationToken);

        // The cap: keep the most recently registered, drop the rest (their pending receipts cascade).
        await db.Database.ExecuteSqlAsync($"""
            DELETE FROM push_devices
            WHERE user_id = {user.Id} AND id NOT IN (
                SELECT id FROM push_devices WHERE user_id = {user.Id}
                ORDER BY last_registered_at DESC, id DESC
                LIMIT {PushDevice.MaxPerUser})
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var body = DeviceResponse.From(device);
        return created ? TypedResults.Created((string?)null, body) : TypedResults.Ok(body);
    }

    /// <summary>
    /// Forgets one of the signed-in student's devices (the app calls it on sign-out). 204 for any
    /// token: one that isn't theirs, or isn't registered at all, changes nothing, so the answer can't
    /// be used to find out whose a token is. Only a body that isn't <c>{ token }</c> is a 400.
    /// </summary>
    internal static async Task<NoContent> UnregisterDevice([FromBody] UnregisterDeviceRequest request,
        ClaimsPrincipal principal, CampusPulseDbContext db, CancellationToken cancellationToken)
    {
        var identity = MeEndpoint.Identity.From(principal);
        if (PushDevice.IsExpoToken(request.Token))
        {
            await db.PushDevices
                .Where(d => d.Token == request.Token && db.Users.Any(u => u.Id == d.UserId && u.EntraObjectId == identity.Oid))
                .ExecuteDeleteAsync(cancellationToken);
        }

        return TypedResults.NoContent();
    }

    /// <summary>
    /// Sends "CampusPulse test notification" to the signed-in student's devices and answers how many
    /// the push service accepted it for. Once per minute per student (429 with Retry-After
    /// otherwise; see <see cref="TestNotificationLimit"/>), counting only sends that reached the push
    /// service: with no devices, or a 502, the next tap may send right away. 404 without an account,
    /// 502 when the push service couldn't be used at all.
    /// </summary>
    internal static async Task<Results<Ok<TestNotificationResponse>, ProblemHttpResult>> SendTestNotification(
        ClaimsPrincipal principal, CampusPulseDbContext db, PushDelivery delivery, TestNotificationLimit limit,
        HttpContext context, CancellationToken cancellationToken)
    {
        var identity = MeEndpoint.Identity.From(principal);
        var user = await db.Users.SingleOrDefaultAsync(u => u.EntraObjectId == identity.Oid, cancellationToken);
        if (user is null)
        {
            return MeEndpoint.NoAccount();
        }

        if (!limit.TryStart(user.Id, out var retryAfter))
        {
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return TypedResults.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many test notifications",
                detail: "One test notification per minute.");
        }

        var devices = await db.PushDevices.Where(d => d.UserId == user.Id).ToListAsync(cancellationToken);
        PushDeliveryResult result;
        try
        {
            result = await delivery.SendAsync(devices, TestTitle, TestBody, cancellationToken);
        }
        catch
        {
            limit.Release(user.Id);
            throw;
        }

        if (devices.Count == 0 || result.ServiceFailed)
        {
            limit.Release(user.Id);
        }

        if (result.ServiceFailed)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Push service unavailable",
                detail: "The push service couldn't be reached. Try again later.");
        }

        return TypedResults.Ok(new TestNotificationResponse(result.Accepted));
    }
}
