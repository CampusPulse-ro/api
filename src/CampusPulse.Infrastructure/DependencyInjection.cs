using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CampusPulse.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddCampusPulsePersistence(
        this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres");

        // Fail at startup, not on the first query: a missing or empty value would otherwise
        // only surface inside Npgsql as "The ConnectionString property has not been initialized."
        // Registering the context opens no connection, so the API still starts with the
        // database down; only requests that need it fail.
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings__Postgres is not set. See README.md.");
        }

        services.AddDbContext<CampusPulseDbContext>(options => Configure(options, connectionString));

        return services;
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
