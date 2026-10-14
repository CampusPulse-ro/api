using CampusPulse.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CampusPulse.Api.Push;

/// <summary>What one pass of <see cref="PushReceiptChecker"/> did.</summary>
public sealed record PushReceiptCheck(int Requested, int Answered, int DevicesRemoved, int Expired)
{
    public static readonly PushReceiptCheck Nothing = new(0, 0, 0, 0);
}

/// <summary>
/// Every 15 minutes, reads the receipts of the tickets that are at least 15 minutes old (Expo's
/// advice: receipts take a while). A <c>DeviceNotRegistered</c> receipt deletes its device; every
/// answered row is dropped, and so is any row older than 24 hours (Expo keeps receipts about a
/// day). Always registered; with no pending rows a pass is one query and nothing else.
/// </summary>
public sealed partial class PushReceiptChecker(IServiceScopeFactory scopes, TimeProvider time,
    ILogger<PushReceiptChecker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MinimumAge = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaximumAge = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckOnceAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                // The rows stay; the next pass tries again. Never stop the host over it.
                LogCheckFailed(logger, e.GetType().Name, e);
            }
        }
    }

    public async Task<PushReceiptCheck> CheckOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CampusPulseDbContext>();
        if (!await db.PushReceiptsPending.AnyAsync(cancellationToken))
        {
            return PushReceiptCheck.Nothing;
        }

        var now = time.GetUtcNow();
        var expiredBefore = now - MaximumAge;
        var dueBefore = now - MinimumAge;
        var expired = await db.PushReceiptsPending.Where(r => r.CreatedAt < expiredBefore).ExecuteDeleteAsync(cancellationToken);
        var due = await db.PushReceiptsPending.Where(r => r.CreatedAt <= dueBefore)
            .Select(r => new { r.TicketId, r.DeviceId })
            .ToListAsync(cancellationToken);
        if (due.Count == 0)
        {
            LogChecked(logger, 0, 0, 0, expired);
            return new PushReceiptCheck(0, 0, 0, expired);
        }

        var sender = scope.ServiceProvider.GetRequiredService<IPushSender>();
        var receipts = await sender.GetReceiptsAsync(due.Select(r => r.TicketId).ToList(), cancellationToken);

        var answered = due.Where(r => receipts.ContainsKey(r.TicketId)).ToList();
        var unregistered = answered
            .Where(r => receipts[r.TicketId].Error == PushErrors.DeviceNotRegistered)
            .Select(r => r.DeviceId)
            .Distinct()
            .ToList();
        foreach (var group in answered.Select(r => receipts[r.TicketId].Error).OfType<string>().GroupBy(code => code))
        {
            LogReceiptErrors(logger, group.Key, group.Count());
        }

        var answeredIds = answered.Select(r => r.TicketId).ToList();
        await db.PushReceiptsPending.Where(r => answeredIds.Contains(r.TicketId)).ExecuteDeleteAsync(cancellationToken);
        // Their remaining pending rows go with them (ON DELETE CASCADE).
        var removed = unregistered.Count == 0
            ? 0
            : await db.PushDevices.Where(d => unregistered.Contains(d.Id)).ExecuteDeleteAsync(cancellationToken);

        LogChecked(logger, due.Count, answered.Count, removed, expired);
        return new PushReceiptCheck(due.Count, answered.Count, removed, expired);
    }

    [LoggerMessage(EventId = 50, Level = LogLevel.Information,
        Message = "Push receipts: {Requested} requested, {Answered} answered, {Removed} unregistered devices removed, {Expired} expired unread")]
    private static partial void LogChecked(ILogger logger, int requested, int answered, int removed, int expired);

    [LoggerMessage(EventId = 51, Level = LogLevel.Warning, Message = "Push receipts with errors: {Count} {ErrorCode}")]
    private static partial void LogReceiptErrors(ILogger logger, string errorCode, int count);

    [LoggerMessage(EventId = 52, Level = LogLevel.Warning, Message = "Push receipt check failed: {Reason}")]
    private static partial void LogCheckFailed(ILogger logger, string reason, Exception exception);
}
