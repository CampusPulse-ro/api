namespace CampusPulse.Api.RateLimiting;

/// <summary>
/// Counts the requests the rate limits refused and logs the counts once a minute (and at shutdown), only when there
/// were any: one Warning line, never a student, an IP address or a path, so a flood can't flood the log either.
/// Per-route and per-limit counts are in the metrics (<c>aspnetcore.rate_limiting.requests</c>).
/// </summary>
public sealed partial class RateLimitLog(TimeProvider time, ILogger<RateLimitLog> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private long _signedIn;
    private long _anonymous;

    public void Refused(bool signedIn)
    {
        if (signedIn)
        {
            Interlocked.Increment(ref _signedIn);
        }
        else
        {
            Interlocked.Increment(ref _anonymous);
        }
    }

    /// <summary>Logs and resets the counts (the timer does this every <see cref="Interval"/>).</summary>
    public void Flush()
    {
        var signedIn = Interlocked.Exchange(ref _signedIn, 0);
        var anonymous = Interlocked.Exchange(ref _anonymous, 0);
        if (signedIn + anonymous > 0)
        {
            LogRefused(logger, signedIn + anonymous, signedIn, anonymous);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                Flush();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            Flush();
        }
    }

    [LoggerMessage(EventId = 70, Level = LogLevel.Warning,
        Message = "Rate limits refused {Refused} requests in the last minute ({SignedIn} signed in, {Anonymous} anonymous)")]
    private static partial void LogRefused(ILogger logger, long refused, long signedIn, long anonymous);
}
