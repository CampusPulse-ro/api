using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CampusPulse.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Connections one process may hold, unless <c>Database:MaxPoolSize</c> says otherwise. Npgsql's own
    /// default is 100, and stage and prod share one Postgres whose <c>max_connections</c> is 100: at 20 each, a load
    /// spike in both still leaves room for migrations, backups and psql.</summary>
    public const int DefaultMaxPoolSize = 20;

    /// <param name="requireConnectionString">
    /// <c>false</c> only where no configuration exists by design: the build writing the OpenAPI
    /// document. The registration is the same either way; only the fail-fast check differs.
    /// </param>
    public static IServiceCollection AddCampusPulsePersistence(
        this IServiceCollection services, IConfiguration configuration, bool requireConnectionString = true)
    {
        var connectionString = configuration.GetConnectionString("Postgres");

        // Fail at startup, not on the first query: a missing or empty value would otherwise
        // only surface inside Npgsql as "The ConnectionString property has not been initialized."
        // Registering the context opens no connection, so the API still starts with the
        // database down; only requests that need it fail.
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (requireConnectionString)
            {
                throw new InvalidOperationException(
                    "ConnectionStrings__Postgres is not set. See README.md.");
            }

            connectionString = null;
        }
        else
        {
            connectionString = WithPoolSize(connectionString, configuration);
        }

        services.AddDbContext<CampusPulseDbContext>(options => Configure(options, connectionString));

        return services;
    }

    /// <summary>
    /// The connection string with its pool sized: <c>Database:MaxPoolSize</c> / <c>Database:MinPoolSize</c> when set
    /// (they win over the connection string), else a size the connection string gives, else
    /// <see cref="DefaultMaxPoolSize"/> and 0. A request that finds every connection busy waits for one (up to the
    /// connection string's <c>Timeout</c>, 15 s by default) instead of opening more. Invalid values fail the start.
    /// </summary>
    public static string WithPoolSize(string connectionString, IConfiguration configuration)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        // Which keys the string itself has (Npgsql's builder answers ContainsKey for every keyword it knows).
        var keys = new DbConnectionStringBuilder { ConnectionString = connectionString }.Keys.Cast<string>()
            .Select(key => key.Replace(" ", "", StringComparison.Ordinal)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var max = PoolSetting(configuration, "MaxPoolSize", minimum: 1)
            ?? (keys.Overlaps(["MaximumPoolSize", "MaxPoolSize"]) ? builder.MaxPoolSize : DefaultMaxPoolSize);
        var min = PoolSetting(configuration, "MinPoolSize", minimum: 0)
            ?? (keys.Overlaps(["MinimumPoolSize", "MinPoolSize"]) ? builder.MinPoolSize : 0);
        if (min > max)
        {
            throw new InvalidOperationException($"The pool's minimum ({min}) is larger than its maximum ({max}): check Database__MinPoolSize, "
                + "Database__MaxPoolSize and the connection string.");
        }

        builder.MaxPoolSize = max;
        builder.MinPoolSize = min;
        return builder.ConnectionString;
    }

    private static int? PoolSetting(IConfiguration configuration, string name, int minimum)
    {
        var text = configuration[$"Database:{name}"];
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= minimum
            ? value
            : throw new InvalidOperationException($"Database__{name} must be a whole number of at least {minimum}.");
    }

    /// <summary>
    /// The one place the context's options are set, shared by the app, the design-time
    /// factory (migrations) and the tests, so the model EF compares is always the same.
    /// A null connection string configures Npgsql without one: building the model works,
    /// and anything that tries to connect fails with a clear error instead of reaching some
    /// other database.
    /// </summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string? connectionString) =>
        (connectionString is null ? options.UseNpgsql() : options.UseNpgsql(connectionString))
            // Postgres convention: snake_case names, so hand-written SQL needs no quoting.
            .UseSnakeCaseNamingConvention();
}
