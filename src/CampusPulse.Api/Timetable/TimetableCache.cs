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
/// loaded again. A read that looks for something the snapshot doesn't have (a group or room id a client got from a
/// newer snapshot, the group a student just picked) asks at once (<see cref="GetAsync(Func{TimetableSnapshot, bool}, CancellationToken)"/>),
/// so new data is never missed, only changed data can be up to that many seconds old.
/// <para>One check or load runs at a time: requests that arrive meanwhile wait for its result instead of each
/// querying the database (no stampede after an import). Per-student data is never cached.</para>
/// </summary>
public sealed partial class TimetableCache(IServiceScopeFactory scopes, TimeProvider time, IOptions<TimetableCacheOptions> options,
    CampusPulseMetrics metrics, ILogger<TimetableCache> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _checkEvery = TimeSpan.FromSeconds(Math.Max(0, options.Value.VersionCheckSeconds));
    private Entry? _current;
    private long _checks;

    /// <summary>The current snapshot: from memory while its version was checked less than
    /// <see cref="TimetableCacheOptions.VersionCheckSeconds"/> ago, else after checking (and reloading, if an import
    /// ran).</summary>
    public async ValueTask<TimetableSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var current = Volatile.Read(ref _current);
        if (current is not null && IsFresh(current))
        {
            metrics.CacheHit();
            return current.Snapshot;
        }

        return await RefreshAsync(force: false, cancellationToken);
    }

    /// <summary>The current snapshot, checked against the database first when it doesn't satisfy
    /// <paramref name="covers"/> (it lacks an id the caller has from the database or a client).</summary>
    public async ValueTask<TimetableSnapshot> GetAsync(Func<TimetableSnapshot, bool> covers, CancellationToken cancellationToken)
    {
        var snapshot = await GetAsync(cancellationToken);
        return covers(snapshot) ? snapshot : await RefreshAsync(force: true, cancellationToken);
    }

    public void Dispose() => _gate.Dispose();

    private bool IsFresh(Entry entry) => time.GetUtcNow() - entry.CheckedAt < _checkEvery;

    private async Task<TimetableSnapshot> RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        var checksBefore = Interlocked.Read(ref _checks);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Another request checked while this one waited: its answer is as new as this one's would be.
            var current = _current;
            if (current is not null && (force ? Interlocked.Read(ref _checks) != checksBefore : IsFresh(current)))
            {
                metrics.CacheHit();
                return current.Snapshot;
            }

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
        finally
        {
            _gate.Release();
        }
    }

    private void Publish(TimetableSnapshot snapshot)
    {
        Volatile.Write(ref _current, new Entry(snapshot, time.GetUtcNow()));
        Interlocked.Increment(ref _checks);
    }

    private sealed record Entry(TimetableSnapshot Snapshot, DateTimeOffset CheckedAt);

    [LoggerMessage(EventId = 80, Level = LogLevel.Information,
        Message = "Timetable cache loaded import {Version}: {StudyGroups} study groups, {Slots} slots in {ElapsedMs} ms")]
    private static partial void LogLoaded(ILogger logger, string version, int studyGroups, int slots, double elapsedMs);
}
