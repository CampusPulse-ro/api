using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
    /// only). With no home directory (the Pi's service users) it keeps its keys in memory and warns
    /// about that at every start (#39). Its category logs from Error up; everything else keeps its
    /// level. The warnings themselves can't be reproduced here: a test process has a home directory.</summary>
    [Fact]
    public void Data_Protection_logs_only_errors()
    {
        using var factory = new SignInFactory();
        var loggers = factory.Services.GetRequiredService<ILoggerFactory>();

        var keyManager = loggers.CreateLogger("Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager");
        Assert.False(keyManager.IsEnabled(LogLevel.Warning));
        Assert.True(keyManager.IsEnabled(LogLevel.Error));
        Assert.True(loggers.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics").IsEnabled(LogLevel.Warning));
    }

    /// <summary>EF Core logs every SQL command at Information; at Warning the API's log and the
    /// timetable import's output keep only EF's warnings and errors (#43).</summary>
    [Fact]
    public void EF_Core_logs_no_SQL_commands()
    {
        using var factory = new SignInFactory();
        var loggers = factory.Services.GetRequiredService<ILoggerFactory>();

        var commands = loggers.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command");
        Assert.False(commands.IsEnabled(LogLevel.Information));
        Assert.True(commands.IsEnabled(LogLevel.Warning));
        Assert.True(loggers.CreateLogger("CampusPulse.Api").IsEnabled(LogLevel.Information));
    }
}
