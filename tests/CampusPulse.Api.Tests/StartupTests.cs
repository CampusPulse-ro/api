using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

    private sealed class LoggingFactory(CollectingLoggerProvider logs) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Postgres",
                "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=2");
            TestAuth.Configure(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<ILoggerProvider>(logs));
        }
    }

    /// <summary>Data Protection comes with AddAuthentication but protects nothing here; it is
    /// ephemeral on purpose and must not warn about unpersisted keys at every start (#39).</summary>
    [Fact]
    public async Task A_start_logs_no_Data_Protection_warnings()
    {
        var logs = new CollectingLoggerProvider();
        using var factory = new LoggingFactory(logs);

        // A request, so the app (and its hosted services) has fully started.
        await factory.CreateClient().GetAsync("/health");

        Assert.DoesNotContain(logs.Entries, e =>
            e.Category.StartsWith("Microsoft.AspNetCore.DataProtection", StringComparison.Ordinal) && e.Level >= LogLevel.Warning);
    }
}
