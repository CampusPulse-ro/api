using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

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

    private sealed class SignInFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Postgres",
                "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=2");
            TestAuth.Configure(builder);
        }
    }

    /// <summary>Data Protection comes with AddAuthentication but protects nothing here (bearer tokens
    /// only), so its keys are ephemeral on purpose. Left to its defaults, a service user with no home
    /// directory (the Pi's) gets an in-memory key repository and three warnings at every start (#39).
    /// The warnings themselves can't be reproduced here: a test process has a home directory, so the
    /// defaults pick a file repository instead. This pins the configuration that avoids them.</summary>
    [Fact]
    public async Task Data_Protection_is_ephemeral_by_design()
    {
        using var factory = new SignInFactory();
        await factory.CreateClient().GetAsync("/health");

        Assert.IsType<EphemeralDataProtectionProvider>(factory.Services.GetRequiredService<IDataProtectionProvider>());
    }
}
