namespace CampusPulse.Api.Tests;

/// <summary>
/// A test that needs a real Postgres. It gets the server from <c>CAMPUSPULSE_TEST_DB</c>
/// (an Npgsql connection string without a database name; CI sets it for its Postgres
/// service). Without it the test is reported as SKIPPED with this reason, never as passed.
/// On CI (<c>CI=true</c>) it is never skipped: a missing variable fails the test instead.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public const string Variable = "CAMPUSPULSE_TEST_DB";

    public PostgresFactAttribute()
    {
        if (ServerOrNull() is null && !OnCi)
        {
            Skip = $"Needs Postgres: set {Variable} to a connection string for a server you can create "
                + "databases on (e.g. Host=localhost;Port=5434;Username=campuspulse;Password=campuspulse_dev).";
        }
    }

    public static bool OnCi => Environment.GetEnvironmentVariable("CI") == "true";

    public static string? ServerOrNull() =>
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } value ? value : null;
}
