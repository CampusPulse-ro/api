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

        services.AddDbContext<CampusPulseDbContext>(options => options
            .UseNpgsql(connectionString)
            // Postgres convention: snake_case names, so hand-written SQL needs no quoting.
            .UseSnakeCaseNamingConvention());

        return services;
    }
}
