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
/// <para>A read that looks for something the snapshot doesn't have (a group or room id a client got from a newer
/// snapshot, the group a student just picked) makes the cache ask sooner
/// (<see cref="GetAsync(Func{TimetableSnapshot, bool}, CancellationToken)"/>), at most once per
/// <see cref="MissCheckInterval"/>, so new data is found within a second while made-up ids can't make every request
/// query the database. Changed data can be up to the check interval old.</para>
/// <para>One check or load runs at a time: requests that arrive meanwhile wait and reuse its result (no stampede after
/// an import). If the database can't be asked, the snapshot in memory is served for another interval (with a
/// warning) instead of every waiting request trying in turn; with no snapshot yet, a failed load fails the requests of
/// the next <see cref="MissCheckInterval"/> at once. Per-student data is never cached.</para>
/// </summary>
public sealed partial class TimetableCache(IServiceScopeFactory scopes, TimeProvider time, IOptions<TimetableCacheOptions> options,
    CampusPulseMetrics metrics, ILogger<TimetableCache> logger) : IDisposable
{
    /// <summary>How often, at most, a lookup that misses makes the cache ask the database.</summary>
    public static readonly TimeSpan MissCheckInterval = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _checkEvery = TimeSpan.FromSeconds(Math.Max(0, options.Value.VersionCheckSeconds));
    private Entry? _current;
    private DateTimeOffset? _failedAt;

    /// <summary>The current snapshot: from memory while its version was checked less than
    /// <see cref="TimetableCacheOptions.VersionCheckSeconds"/> ago, else after checking (and reloading, if an import
    /// ran).</summary>
    public async ValueTask<TimetableSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var current = Volatile.Read(ref _current);
        if (current is not null && Age(current) < _checkEvery)
        {
            metrics.CacheHit();
            return current.Snapshot;
        }

        return await RefreshAsync(covers: null, cancellationToken);
    }

    /// <summary>The current snapshot, checked against the database first (at most once per
    /// <see cref="MissCheckInterval"/>) when it doesn't satisfy <paramref name="covers"/>: it lacks an id the caller has
    /// from the database or a client.</summary>
    public async ValueTask<TimetableSnapshot> GetAsync(Func<TimetableSnapshot, bool> covers, CancellationToken cancellationToken)
    {
        var snapshot = await GetAsync(cancellationToken);
        return covers(snapshot) ? snapshot : await RefreshAsync(covers, cancellationToken);
    }

    public void Dispose() => _gate.Dispose();

    private TimeSpan Age(Entry entry) => time.GetUtcNow() - entry.CheckedAt;

    private async Task<TimetableSnapshot> RefreshAsync(Func<TimetableSnapshot, bool>? covers, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // What the request before this one found serves this one too: it is fresh, or it has what this lookup looks
            // for, or (a lookup that misses) the database was asked less than a second ago.
            var current = _current;
            if (current is not null && (covers is null ? Age(current) < _checkEvery : covers(current.Snapshot) || Age(current) < MissCheckInterval))
            {
                metrics.CacheHit();
                return current.Snapshot;
            }

            if (current is null && _failedAt is { } failedAt && time.GetUtcNow() - failedAt < MissCheckInterval)
            {
                throw new InvalidOperationException("The timetable couldn't be loaded from the database a moment ago; not trying again yet.");
            }

            try
            {
                var snapshot = await CheckAsync(current, cancellationToken);
                _failedAt = null;
                return snapshot;
            }
            catch (Exception e) when (IsDatabaseFailure(e) && !cancellationToken.IsCancellationRequested)
            {
                if (current is null)
                {
                    _failedAt = time.GetUtcNow();
                    throw;
                }

                // Served from memory for another interval: an import may be late, but timetable reads keep working.
                Publish(current.Snapshot);
                metrics.CacheHit();
                LogServingStale(logger, e.GetType().Name, _checkEvery.TotalSeconds);
                return current.Snapshot;
            }
        }
        finally
        {
            _gate.Release();
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
