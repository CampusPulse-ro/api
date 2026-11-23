namespace CampusPulse.Domain;

/// <summary>Instants as Postgres stores them: <c>timestamptz</c> keeps microseconds, .NET ticks are 100 ns. Values an
/// entity stores are cut to microseconds, so what an answer shows, and what a later comparison expects, is exactly what
/// the database will hold.</summary>
public static class Instants
{
    public static DateTimeOffset Stored(DateTimeOffset instant) => new(instant.Ticks - (instant.Ticks % 10), instant.Offset);
}
