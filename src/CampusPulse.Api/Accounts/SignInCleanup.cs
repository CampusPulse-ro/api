using CampusPulse.Domain.Users;
using CampusPulse.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CampusPulse.Api.Accounts;

/// <summary>
/// Deletes what sign-in no longer needs, once an hour: refresh tokens past their expiry (a revoked one is kept until
/// then, so showing it again is still recognised as reuse), and an address's code row a day after its last code (the
/// per-address daily limit counts the codes of that day). Logs counts only.
/// </summary>
public sealed partial class SignInCleanup(IServiceScopeFactory scopes, TimeProvider time, ILogger<SignInCleanup> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                LogFailed(logger, e.GetType().Name, e);
            }
        }
    }

    /// <summary>One pass; returns the refresh tokens and code rows deleted.</summary>
    public async Task<(int RefreshTokens, int Codes)> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CampusPulseDbContext>();
        var now = time.GetUtcNow();
        var tokens = await db.RefreshTokens.Where(t => t.ExpiresAt < now).ExecuteDeleteAsync(cancellationToken);
        var dayAgo = now - EmailSignInCode.DailyWindow;
        var codes = await db.EmailSignInCodes.Where(c => c.CreatedAt < dayAgo && c.ExpiresAt < now).ExecuteDeleteAsync(cancellationToken);
        if (tokens + codes > 0)
        {
            LogCleaned(logger, tokens, codes);
        }

        return (tokens, codes);
    }

    [LoggerMessage(EventId = 110, Level = LogLevel.Information, Message = "Sign-in cleanup: {RefreshTokens} expired refresh tokens, {Codes} old code rows deleted")]
    private static partial void LogCleaned(ILogger logger, int refreshTokens, int codes);

    [LoggerMessage(EventId = 111, Level = LogLevel.Warning, Message = "Sign-in cleanup failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason, Exception exception);
}
