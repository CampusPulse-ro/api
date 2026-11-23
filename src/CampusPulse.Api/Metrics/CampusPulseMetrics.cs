using System.Diagnostics.Metrics;

namespace CampusPulse.Api.Metrics;

/// <summary>
/// The API's own instruments, on the <see cref="MeterName"/> meter: how the timetable cache answers. ASP.NET Core
/// (request rate and duration, rate-limit refusals, Kestrel), Npgsql (the connection pool) and the runtime have their
/// own meters; <see cref="MetricsSetup"/> exports them all. Counts and durations only, never who.
/// </summary>
public sealed class CampusPulseMetrics
{
    public const string MeterName = "CampusPulse.Api";

    private static readonly KeyValuePair<string, object?> Hit = new("result", "hit");
    private static readonly KeyValuePair<string, object?> Miss = new("result", "miss");

    private readonly Counter<long> _cacheLookups;
    private readonly Histogram<double> _cacheLoads;

    public CampusPulseMetrics(IMeterFactory meters)
    {
        var meter = meters.Create(MeterName);
        _cacheLookups = meter.CreateCounter<long>("campuspulse.timetable_cache.lookups", "{lookup}",
            "Timetable reads answered from memory (result=hit) or by loading the timetable from the database (result=miss).");
        _cacheLoads = meter.CreateHistogram<double>("campuspulse.timetable_cache.load.duration", "s",
            "How long loading the timetable from the database took (once per import, per API instance).",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10] });
    }

    public void CacheHit() => _cacheLookups.Add(1, Hit);

    public void CacheMiss(TimeSpan loadTime)
    {
        _cacheLookups.Add(1, Miss);
        _cacheLoads.Record(loadTime.TotalSeconds);
    }
}
