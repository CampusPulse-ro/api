using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CampusPulse.Api.Tests;

/// <summary>
/// Hosts the real app in-process. No database yet: registering the DbContext opens no
/// connection, so a placeholder connection string is enough for endpoints that never query.
/// Switch to a real Postgres (as amicus-api does) once a test touches the database.
/// </summary>
public sealed class CampusPulseAppFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // UseSetting, not ConfigureAppConfiguration: under minimal hosting Program.cs reads
        // builder.Configuration before the factory's configuration delegates run.
        builder.UseSetting("ConnectionStrings:Postgres",
            "Host=localhost;Database=unused;Username=unused;Password=unused");
    }
}
