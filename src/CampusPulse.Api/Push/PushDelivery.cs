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
/// <c>DeviceNotRegistered</c> one deletes its device at once. Logs counts and error codes only.
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

        var tickets = await sender.SendAsync(devices.Select(d => new PushMessage(d.Token, title, body)).ToList(), cancellationToken);
        var now = time.GetUtcNow();
        var accepted = 0;
        var refused = new Dictionary<string, int>(StringComparer.Ordinal);
        var unregistered = new List<Guid>();
        foreach (var (device, ticket) in devices.Zip(tickets))
        {
            if (ticket.Ok)
            {
                accepted++;
                db.PushReceiptsPending.Add(new PushReceiptPending(ticket.Id!, device.Id, now));
                continue;
            }

            var code = ticket.Error ?? PushErrors.Unknown;
            refused[code] = refused.GetValueOrDefault(code) + 1;
            if (code == PushErrors.DeviceNotRegistered)
            {
                unregistered.Add(device.Id);
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            // A device was deleted while the message was on its way (DELETE /me, or sign-out): its
            // receipt has nothing to belong to. The other receipts are lost too; only the devices
            // Expo reports as gone, on a later send, would be missed.
            db.ChangeTracker.Clear();
            LogReceiptsDropped(logger, accepted);
        }

        if (unregistered.Count > 0)
        {
            await db.PushDevices.Where(d => unregistered.Contains(d.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        LogSent(logger, devices.Count, accepted, unregistered.Count);
        foreach (var (code, count) in refused)
        {
            LogRefused(logger, code, count);
        }

        return new PushDeliveryResult(accepted, refused);
    }

    [LoggerMessage(EventId = 40, Level = LogLevel.Information,
        Message = "Push sent to {Devices} devices: {Accepted} accepted, {Unregistered} unregistered devices removed")]
    private static partial void LogSent(ILogger logger, int devices, int accepted, int unregistered);

    [LoggerMessage(EventId = 41, Level = LogLevel.Warning, Message = "Push refused for {Count} devices: {ErrorCode}")]
    private static partial void LogRefused(ILogger logger, string errorCode, int count);

    [LoggerMessage(EventId = 42, Level = LogLevel.Warning,
        Message = "{Count} push receipts not kept: a device was deleted during the send")]
    private static partial void LogReceiptsDropped(ILogger logger, int count);
}
