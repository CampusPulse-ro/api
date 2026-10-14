using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CampusPulse.Api.Tests;

/// <summary>
/// The build writes openapi/v1.json without a connection string; a real start must still refuse
/// to run without one.
/// </summary>
public sealed class StartupTests
{
    private sealed class NoConnectionStringFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Postgres", "");
        }
    }

    [Fact]
    public void A_real_start_without_a_connection_string_fails_fast()
    {
        using var factory = new NoConnectionStringFactory();

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("ConnectionStrings__Postgres is not set", exception.ToString());
    }
}
