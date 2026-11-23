using System.Data.Common;
using System.Diagnostics;
using CampusPulse.Api.Metrics;
using CampusPulse.Infrastructure;
using Microsoft.Extensions.Options;

namespace CampusPulse.Api.Timetable;

/// <summary>The <c>TimetableCache</c> configuration section.</summary>
public sealed class TimetableCacheOptions
{
    public const string Section = "TimetableCache";

    /// <summary>How often, at most, the cache asks the database whether a new import ran (one small query). An import
    /// is served by every instance within this many seconds; 0 asks on every read (the tests do).</summary>
    public int VersionCheckSeconds { get; set; } = 10;
}

/// <summary>
/// Keeps the shared timetable (<see cref="TimetableSnapshot"/>) in memory, so the timetable endpoints and the class
/// reminders don't read it from the database on every request. The timetable changes only when
/// <c>import-timetable</c> runs, a separate process that can't tell this one; so the cache labels its snapshot with
/// the newest import run's id and asks the database for that id at most every
/// <see cref="TimetableCacheOptions.VersionCheckSeconds"/>: a different one means an import ran, and the snapshot is
/// loaded again.
/// <list type="bullet">
/// <item>One check runs at a time (single flight). Once a snapshot is in memory, only the request that starts a check
/// waits for it (one small query); every other request is answered from memory meanwhile, so a slow database never
/// stalls timetable reads. With nothing loaded yet, requests wait for the one load.</item>
/// <item>A read that looks for something the snapshot doesn't have makes the cache check sooner
/// (<see cref="GetAsync(Func{TimetableSnapshot, bool}, bool, CancellationToken)"/>): at once for ids that come from
/// the database (the group a student has or just picked), at most once per <see cref="MissCheckInterval"/> for ids a
/// client sent, so made-up ids can't make every request query the database. New data is found within a second;
/// changed data can be up to the check interval old.</item>
/// <item>The check runs on the cache's own token with a timeout (<see cref="CheckTimeout"/>), not a request's, so a
/// client that goes away doesn't cancel it for the others. If the database can't be asked, the snapshot in memory is
/// served for another interval (with a warning); with nothing loaded yet, a failed load fails the requests of the next
/// <see cref="MissCheckInterval"/> at once.</item>
/// </list>
/// Per-student data is never cached.
/// </summary>
public sealed partial class TimetableCache(IServiceScopeFactory scopes, TimeProvider time, IOptions<TimetableCacheOptions> options,
    IHostApplicationLifetime lifetime, CampusPulseMetrics metrics, ILogger<TimetableCache> logger)
{
    /// <summary>How often, at most, a lookup of an id a client sent that the snapshot lacks makes the cache ask.</summary>
    public static readonly TimeSpan MissCheckInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long one check (with the load, when an import ran) may take before it is given up.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    private readonly object _lock = new();
    private readonly TimeSpan _checkEvery = TimeSpan.FromSeconds(Math.Max(0, options.Value.VersionCheckSeconds));
    private Entry? _current;
    private Task<TimetableSnapshot>? _check;
    private DateTimeOffset? _failedAt;

    /// <summary>The current snapshot. From memory while its version was checked less than
    /// <see cref="TimetableCacheOptions.VersionCheckSeconds"/> ago; after that, the request that starts the check waits
    /// for it and the others get the snapshot in memory.</summary>
    public async ValueTask<TimetableSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var current = Volatile.Read(ref _current);
        if (current is not null && Age(current) < _checkEvery)
        {
            metrics.CacheHit();
            return current.Snapshot;
        }

        var check = StartCheck(out var started);
        if (current is not null && !started)
        {
            metrics.CacheHit();
            return current.Snapshot;
        }

        return await check.WaitAsync(cancellationToken);
    }

    /// <summary>The current snapshot, checked against the database first when it doesn't satisfy
    /// <paramref name="covers"/> (it lacks an id the caller has). <paramref name="idsFromDatabase"/>: the ids come from
    /// the database or were just checked there, so they exist and a miss always checks; otherwise (a client's ids) at
    /// most once per <see cref="MissCheckInterval"/>.</summary>
    public async ValueTask<TimetableSnapshot> GetAsync(Func<TimetableSnapshot, bool> covers, bool idsFromDatabase,
        CancellationToken cancellationToken)
    {
        var snapshot = await GetAsync(cancellationToken);
        if (covers(snapshot) || (!idsFromDatabase && Age(Volatile.Read(ref _current)!) < MissCheckInterval))
        {
            return snapshot;
        }

        // A check already running may have asked before the id existed: if it doesn't have it, one more of our own.
        var checkedSnapshot = await StartCheck(out var started).WaitAsync(cancellationToken);
        return covers(checkedSnapshot) || started ? checkedSnapshot : await StartCheck(out _).WaitAsync(cancellationToken);
    }

    private TimeSpan Age(Entry entry) => time.GetUtcNow() - entry.CheckedAt;

    /// <summary>The check in flight, or a new one (<paramref name="started"/>).</summary>
    private Task<TimetableSnapshot> StartCheck(out bool started)
    {
        lock (_lock)
        {
            started = _check is null || _check.IsCompleted;
            if (started)
            {
                _check = Task.Run(RunCheckAsync);
                // Observed here, so a check nobody waits for any more never surfaces as an unobserved exception.
                _check.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            return _check!;
        }
    }

    private async Task<TimetableSnapshot> RunCheckAsync()
    {
        var current = Volatile.Read(ref _current);
        if (current is null && _failedAt is { } failedAt && time.GetUtcNow() - failedAt < MissCheckInterval)
        {
            throw new InvalidOperationException("The timetable couldn't be loaded from the database a moment ago; not trying again yet.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        timeout.CancelAfter(CheckTimeout);
        try
        {
            var snapshot = await CheckAsync(current, timeout.Token);
            _failedAt = null;
            return snapshot;
        }
        catch (Exception e) when (!lifetime.ApplicationStopping.IsCancellationRequested
            && (IsDatabaseFailure(e) || e is OperationCanceledException))
        {
            if (current is null)
            {
                _failedAt = time.GetUtcNow();
                throw;
            }

            // Served from memory for another interval: an import may be late, but timetable reads keep working.
            Publish(current.Snapshot);
            LogServingStale(logger, e.GetType().Name, _checkEvery.TotalSeconds);
            return current.Snapshot;
        }
    }

    /// <summary>Asks the database for the newest import run; loads the timetable again when it isn't the snapshot's.</summary>
    private async Task<TimetableSnapshot> CheckAsync(Entry? current, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CampusPulseDbContext>();
        var version = await TimetableSnapshot.VersionAsync(db, cancellationToken);
        if (current is not null && current.Snapshot.Version == version)
        {
            Publish(current.Snapshot);
            metrics.CacheHit();
            return current.Snapshot;
        }

        var started = Stopwatch.GetTimestamp();
        var snapshot = await TimetableSnapshot.LoadAsync(db, cancellationToken);
        var elapsed = Stopwatch.GetElapsedTime(started);
        Publish(snapshot);
        metrics.CacheMiss(elapsed);
        LogLoaded(logger, snapshot.Version?.ToString() ?? "none", snapshot.StudyGroups, snapshot.Slots, Math.Round(elapsed.TotalMilliseconds, 1));
        return snapshot;
    }

    /// <summary>The database couldn't be reached or didn't answer (EF wraps a transient Npgsql error in an
    /// InvalidOperationException).</summary>
    private static bool IsDatabaseFailure(Exception e) =>
        e is DbException or TimeoutException || e.InnerException is DbException or TimeoutException;

    private void Publish(TimetableSnapshot snapshot) => Volatile.Write(ref _current, new Entry(snapshot, time.GetUtcNow()));

    private sealed record Entry(TimetableSnapshot Snapshot, DateTimeOffset CheckedAt);

    [LoggerMessage(EventId = 80, Level = LogLevel.Information,
        Message = "Timetable cache loaded import {Version}: {StudyGroups} study groups, {Slots} slots in {ElapsedMs} ms")]
    private static partial void LogLoaded(ILogger logger, string version, int studyGroups, int slots, double elapsedMs);

    [LoggerMessage(EventId = 81, Level = LogLevel.Warning,
        Message = "Timetable cache: the database couldn't be asked for a new import ({Reason}); serving the timetable in memory for another {Seconds} s")]
    private static partial void LogServingStale(ILogger logger, string reason, double seconds);
}
