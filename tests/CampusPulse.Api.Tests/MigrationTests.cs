using Microsoft.EntityFrameworkCore;

namespace CampusPulse.Api.Tests;

public sealed class MigrationTests(PostgresAppFactory factory) : IClassFixture<PostgresAppFactory>
{
    [PostgresFact]
    public async Task Every_migration_applies_cleanly_to_an_empty_database()
    {
        // The fixture created a fresh database and migrated it; check what got recorded.
        await using var db = factory.CreateDbContext();

        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        Assert.Contains(applied, id => id.EndsWith("_InitialCreate", StringComparison.Ordinal));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }
}
