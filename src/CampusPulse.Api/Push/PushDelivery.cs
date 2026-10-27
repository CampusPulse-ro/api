using CampusPulse.Domain.Push;
using CampusPulse.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CampusPulse.Api.Push;

/// <summary>How a send went: accepted by the push service, and refused (by error code).</summary>
public sealed record PushDeliveryResult(int Accepted, IReadOnlyDictionary<string, int> Refused)
{
    /// <summary>Nothing was accepted because the service itself failed, not because of the devices.</summary>
    public bool ServiceFailed => Accepted == 0 && Refused.Count > 0 && Refused.Keys.All(PushDelivery.IsServiceFailure);

    public static PushDeliveryResult Of(IEnumerable<PushTicket> tickets)
    {
        var accepted = 0;
        var refused = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var ticket in tickets)
        {
            if (ticket.Ok)
            {
                accepted++;
            }
            else
            {
                var code = ticket.Error ?? PushErrors.Unknown;
                refused[code] = refused.GetValueOrDefault(code) + 1;
            }
        }

        return new PushDeliveryResult(accepted, refused);
    }
}

/// <summary>
/// Sends notifications to students' devices, and acts on the tickets: an accepted one is kept in
/// <c>push_receipts_pending</c> for <see cref="PushReceiptChecker"/>, and a <c>DeviceNotRegistered</c> one deletes
/// its device at once, unless the device registered again after the send started (a reinstall can bring the same
/// token back). Logs counts and error codes only.
/// </summary>
public sealed partial class PushDelivery(CampusPulseDbContext db, IPushSender sender, TimeProvider time,
    ILogger<PushDelivery> logger)
{
    /// <summary>The push service couldn't be used (unreachable, too slow, or an answer that can't be read), as
    /// opposed to refusing a message: sending again later may work.</summary>
    public static bool IsServiceFailure(string? errorCode) =>
        errorCode is PushErrors.RequestFailed or PushErrors.TimedOut or PushErrors.MalformedResponse;

    /// <summary>The push service certainly didn't take the message (it was unreachable, or refused the whole
    /// request), so sending it again can't make a duplicate. A timeout or an unreadable answer may hide an accepted
    /// message.</summary>
    public static bool CertainlyNotSent(string? errorCode) => errorCode is PushErrors.RequestFailed;

    /// <summary>The same notification to each of <paramref name="devices"/>.</summary>
    public async Task<PushDeliveryResult> SendAsync(IReadOnlyList<PushDevice> devices, string title, string body,
        CancellationToken cancellationToken) =>
        PushDeliveryResult.Of(await SendEachAsync([.. devices.Select(d => (d, new PushMessage(d.Token, title, body)))], cancellationToken));

    /// <summary>Each message to its device (the message's <c>To</c> is that device's token), in batches as the push
    /// service takes them. Returns one ticket per message, in the same order.</summary>
    public async Task<IReadOnlyList<PushTicket>> SendEachAsync(IReadOnlyList<(PushDevice Device, PushMessage Message)> messages,
        CancellationToken cancellationToken)
    {
        if (messages.Count == 0)
        {
            return [];
        }

        // A ticket or receipt speaks for the token as it was when the message left: the moment a later
        // registration is compared with.
        var sentAt = time.GetUtcNow();
        var tickets = await sender.SendAsync([.. messages.Select(m => m.Message)], cancellationToken);
        var accepted = new List<(Guid DeviceId, string TicketId)>();
        var unregistered = new List<Guid>();
        foreach (var ((device, _), ticket) in messages.Zip(tickets))
        {
            if (ticket.Ok)
            {
                accepted.Add((device.Id, ticket.Id!));
            }
            else if (ticket.Error == PushErrors.DeviceNotRegistered && !unregistered.Contains(device.Id))
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

        var result = PushDeliveryResult.Of(tickets);
        LogSent(logger, messages.Count, result.Accepted, removed);
        foreach (var (code, count) in result.Refused)
        {
            LogRefused(logger, code, count);
        }

        return tickets;
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
        Message = "Push sent: {Messages} messages, {Accepted} accepted, {Unregistered} unregistered devices removed")]
    private static partial void LogSent(ILogger logger, int messages, int accepted, int unregistered);

    [LoggerMessage(EventId = 41, Level = LogLevel.Warning, Message = "Push refused for {Count} messages: {ErrorCode}")]
    private static partial void LogRefused(ILogger logger, string errorCode, int count);

    [LoggerMessage(EventId = 42, Level = LogLevel.Warning,
        Message = "{Count} push receipts not kept: their devices were deleted during the send")]
    private static partial void LogReceiptsDropped(ILogger logger, int count);
}
