using CampusPulse.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CampusPulse.Api.Tests;

/// <summary>
/// The real app on a real, migrated Postgres: a database of its own (unique name), created
/// and migrated before the tests and dropped after them. For test classes whose tests are
/// [PostgresFact]: without <c>CAMPUSPULSE_TEST_DB</c> those are skipped, so this does nothing
/// locally; on CI a missing variable fails instead.
/// </summary>
public sealed class PostgresAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public string? ConnectionString { get; private set; }

    public async Task InitializeAsync()
    {
        var server = PostgresFactAttribute.ServerOrNull();
        if (server is null)
        {
            if (PostgresFactAttribute.OnCi)
            {
                throw new InvalidOperationException(
                    $"{PostgresFactAttribute.Variable} is not set on CI, so the Postgres tests cannot run.");
            }

            return;
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(server)
        {
            Database = $"campuspulse_test_{Guid.NewGuid():N}",
        }.ConnectionString;

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync(); // creates the database, then applies every migration
    }

    public CampusPulseDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CampusPulseDbContext>();
        DependencyInjection.Configure(options, ConnectionString
            ?? throw new InvalidOperationException("No test database: the test should have been skipped."));
        return new CampusPulseDbContext(options.Options);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", ConnectionString
            ?? throw new InvalidOperationException("No test database: the test should have been skipped."));
        TestAuth.Configure(builder);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        // Stop the app first, so none of its pooled connections keeps the database in use.
        await base.DisposeAsync();
        if (ConnectionString is not null)
        {
            NpgsqlConnection.ClearAllPools();
            await using var db = CreateDbContext();
            await db.Database.EnsureDeletedAsync();
        }
    }
}
