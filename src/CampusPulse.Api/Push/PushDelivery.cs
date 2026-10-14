using CampusPulse.Domain.Push;
using CampusPulse.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CampusPulse.Api.Push;

/// <summary>How a send went: accepted by the push service, and refused (by error code).</summary>
public sealed record PushDeliveryResult(int Accepted, IReadOnlyDictionary<string, int> Refused)
{
    /// <summary>Nothing was accepted because the service itself failed, not because of the devices.</summary>
    public bool ServiceFailed => Accepted == 0 && Refused.Count > 0
        && Refused.Keys.All(code => code is PushErrors.RequestFailed or PushErrors.MalformedResponse);
}

/// <summary>
/// Sends one notification to each of a student's devices, and acts on the tickets: an accepted one
/// is kept in <c>push_receipts_pending</c> for <see cref="PushReceiptChecker"/>, and a
/// <c>DeviceNotRegistered</c> one deletes its device at once, unless the device registered again
/// after the send started (a reinstall can bring the same token back). Logs counts and error codes
/// only.
/// </summary>
public sealed partial class PushDelivery(CampusPulseDbContext db, IPushSender sender, TimeProvider time,
    ILogger<PushDelivery> logger)
{
    public async Task<PushDeliveryResult> SendAsync(IReadOnlyList<PushDevice> devices, string title, string body,
        CancellationToken cancellationToken)
    {
        if (devices.Count == 0)
        {
            return new PushDeliveryResult(0, new Dictionary<string, int>());
        }

        // A ticket or receipt speaks for the token as it was when the message left: the moment a later
        // registration is compared with.
        var sentAt = time.GetUtcNow();
        var tickets = await sender.SendAsync(devices.Select(d => new PushMessage(d.Token, title, body)).ToList(), cancellationToken);
        var accepted = new List<(Guid DeviceId, string TicketId)>();
        var refused = new Dictionary<string, int>(StringComparer.Ordinal);
        var unregistered = new List<Guid>();
        foreach (var (device, ticket) in devices.Zip(tickets))
        {
            if (ticket.Ok)
            {
                accepted.Add((device.Id, ticket.Id!));
                continue;
            }

            var code = ticket.Error ?? PushErrors.Unknown;
            refused[code] = refused.GetValueOrDefault(code) + 1;
            if (code == PushErrors.DeviceNotRegistered)
            {
                unregistered.Add(device.Id);
            }
        }

        await KeepReceiptsAsync(accepted, sentAt, cancellationToken);

        var removed = unregistered.Count == 0
            ? 0
            : await db.PushDevices
                .Where(d => unregistered.Contains(d.Id) && d.LastRegisteredAt < sentAt)
                .ExecuteDeleteAsync(cancellationToken);

        LogSent(logger, devices.Count, accepted.Count, removed);
        foreach (var (code, count) in refused)
        {
            LogRefused(logger, code, count);
        }

        return new PushDeliveryResult(accepted.Count, refused);
    }

    /// <summary>Stores the accepted tickets. A device deleted while its message was on the way
    /// (DELETE /me, sign-out) has nothing to hang a receipt on: the others are stored without it.</summary>
    private async Task KeepReceiptsAsync(List<(Guid DeviceId, string TicketId)> accepted, DateTimeOffset sentAt,
        CancellationToken cancellationToken)
    {
        if (accepted.Count == 0)
        {
            return;
        }

        db.ChangeTracker.Clear();
        db.PushReceiptsPending.AddRange(accepted.Select(a => new PushReceiptPending(a.TicketId, a.DeviceId, sentAt)));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            db.ChangeTracker.Clear();
        }

        var ids = accepted.Select(a => a.DeviceId).ToList();
        var stillThere = (await db.PushDevices.Where(d => ids.Contains(d.Id)).Select(d => d.Id).ToListAsync(cancellationToken)).ToHashSet();
        LogReceiptsDropped(logger, accepted.Count(a => !stillThere.Contains(a.DeviceId)));
        db.PushReceiptsPending.AddRange(accepted.Where(a => stillThere.Contains(a.DeviceId))
            .Select(a => new PushReceiptPending(a.TicketId, a.DeviceId, sentAt)));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            // Deleted again in between: give up on these receipts rather than fail the send.
            db.ChangeTracker.Clear();
            LogReceiptsDropped(logger, stillThere.Count);
        }
    }

    [LoggerMessage(EventId = 40, Level = LogLevel.Information,
        Message = "Push sent to {Devices} devices: {Accepted} accepted, {Unregistered} unregistered devices removed")]
    private static partial void LogSent(ILogger logger, int devices, int accepted, int unregistered);

    [LoggerMessage(EventId = 41, Level = LogLevel.Warning, Message = "Push refused for {Count} devices: {ErrorCode}")]
    private static partial void LogRefused(ILogger logger, string errorCode, int count);

    [LoggerMessage(EventId = 42, Level = LogLevel.Warning,
        Message = "{Count} push receipts not kept: their devices were deleted during the send")]
    private static partial void LogReceiptsDropped(ILogger logger, int count);
}
