using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CampusPulse.Api.Tests;

/// <summary>
/// Hosts the real app in-process. Registering the DbContext opens no connection, so a
/// placeholder connection string is enough for endpoints that never query. Tests that need
/// the database are marked [PostgresFact] and get a real server (see PostgresFactAttribute).
/// </summary>
public sealed class CampusPulseAppFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // UseSetting, not ConfigureAppConfiguration: under minimal hosting Program.cs reads
        // builder.Configuration before the factory's configuration delegates run.
        // Port 1 on loopback: nothing listens there, so a connection attempt is refused at once
        // (never another project's database on a developer machine).
        builder.UseSetting("ConnectionStrings:Postgres",
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=2");
        // No reminder ticks in the background of a test; the reminder tests drive them by hand.
        builder.UseSetting("Reminders:Enabled", "false");
    }
}
