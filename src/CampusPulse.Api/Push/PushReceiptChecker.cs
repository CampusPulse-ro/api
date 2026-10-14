using CampusPulse.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CampusPulse.Api.Push;

/// <summary>What one pass of <see cref="PushReceiptChecker"/> did.</summary>
public sealed record PushReceiptCheck(int Requested, int Answered, int DevicesRemoved, int Expired, int FailedBatches = 0)
{
    public static readonly PushReceiptCheck Nothing = new(0, 0, 0, 0);
}

/// <summary>
/// Every 15 minutes, reads the receipts of the tickets that are at least 15 minutes old (Expo's
/// advice: receipts take a while), in batches of 300 (Expo's limit). A <c>DeviceNotRegistered</c>
/// receipt deletes its device, unless the device registered again after that ticket's send (a
/// reinstall can bring the same token back). Every answered row is dropped, and so is any row
/// older than 24 hours (Expo keeps receipts about a day). Each batch is applied on its own, in one
/// transaction: a batch that can't be read keeps its rows for the next pass and doesn't undo the
/// others. Always registered; with no pending rows a pass is one query and nothing else.
/// </summary>
public sealed partial class PushReceiptChecker(IServiceScopeFactory scopes, TimeProvider time,
    ILogger<PushReceiptChecker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MinimumAge = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaximumAge = TimeSpan.FromHours(24);

    /// <summary>Receipts per request: Expo's limit. Tests set a smaller one to get several batches
    /// out of a few rows.</summary>
    public int BatchSize { get; set; } = 300;

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
            .OrderBy(r => r.CreatedAt)
            .Select(r => new Due(r.TicketId, r.DeviceId, r.CreatedAt))
            .ToListAsync(cancellationToken);

        var sender = scope.ServiceProvider.GetRequiredService<IPushSender>();
        int answered = 0, removed = 0, failed = 0;
        foreach (var batch in due.Chunk(BatchSize))
        {
            IReadOnlyDictionary<string, PushReceipt> receipts;
            try
            {
                receipts = await sender.GetReceiptsAsync(batch.Select(r => r.TicketId).ToList(), cancellationToken);
            }
            catch (PushServiceException e)
            {
                failed++;
                LogBatchFailed(logger, batch.Length, e.Message);
                continue;
            }

            var (batchAnswered, batchRemoved) = await ApplyAsync(db, batch, receipts, cancellationToken);
            answered += batchAnswered;
            removed += batchRemoved;
        }

        LogChecked(logger, due.Count, answered, removed, expired, failed);
        return new PushReceiptCheck(due.Count, answered, removed, expired, failed);
    }

    /// <summary>One batch's receipts, in one transaction: first the dead devices (their pending rows
    /// go with them by cascade), then the answered rows.</summary>
    private async Task<(int Answered, int Removed)> ApplyAsync(CampusPulseDbContext db, Due[] batch,
        IReadOnlyDictionary<string, PushReceipt> receipts, CancellationToken cancellationToken)
    {
        var answered = batch.Where(r => receipts.ContainsKey(r.TicketId)).ToList();
        foreach (var group in answered.Select(r => receipts[r.TicketId].Error).OfType<string>().GroupBy(code => code))
        {
            LogReceiptErrors(logger, group.Key, group.Count());
        }

        // Per device, the newest ticket that says it's gone: a registration after that one wins.
        var dead = answered
            .Where(r => receipts[r.TicketId].Error == PushErrors.DeviceNotRegistered)
            .GroupBy(r => r.DeviceId)
            .Select(g => (DeviceId: g.Key, SentAt: g.Max(r => r.CreatedAt)))
            .ToList();
        var answeredIds = answered.Select(r => r.TicketId).ToList();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var removed = 0;
        foreach (var (deviceId, sentAt) in dead)
        {
            removed += await db.PushDevices
                .Where(d => d.Id == deviceId && d.LastRegisteredAt < sentAt)
                .ExecuteDeleteAsync(cancellationToken);
        }

        await db.PushReceiptsPending.Where(r => answeredIds.Contains(r.TicketId)).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (answered.Count, removed);
    }

    private sealed record Due(string TicketId, Guid DeviceId, DateTimeOffset CreatedAt);

    [LoggerMessage(EventId = 50, Level = LogLevel.Information,
        Message = "Push receipts: {Requested} requested, {Answered} answered, {Removed} unregistered devices removed, {Expired} expired unread, {FailedBatches} batches failed")]
    private static partial void LogChecked(ILogger logger, int requested, int answered, int removed, int expired, int failedBatches);

    [LoggerMessage(EventId = 51, Level = LogLevel.Warning, Message = "Push receipts with errors: {Count} {ErrorCode}")]
    private static partial void LogReceiptErrors(ILogger logger, string errorCode, int count);

    [LoggerMessage(EventId = 52, Level = LogLevel.Warning, Message = "Push receipt check failed: {Reason}")]
    private static partial void LogCheckFailed(ILogger logger, string reason, Exception exception);

    [LoggerMessage(EventId = 53, Level = LogLevel.Warning,
        Message = "Push receipts: a batch of {Count} couldn't be read, kept for the next pass: {Reason}")]
    private static partial void LogBatchFailed(ILogger logger, int count, string reason);
}
