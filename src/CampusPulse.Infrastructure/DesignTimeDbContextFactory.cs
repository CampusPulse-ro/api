using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CampusPulse.Infrastructure;

/// <summary>
/// How <c>dotnet ef</c> and the migrations bundle create the context, instead of booting the
/// API. The connection string comes from <c>ConnectionStrings__Postgres</c> in the
/// environment, never from argv (the Pi deploy loads it from the environment's env file and
/// runs <c>./efbundle</c> with no arguments). When it is unset, the context has no connection
/// string at all: <c>migrations add</c>, <c>has-pending-model-changes</c> and building a
/// bundle only need the model, and anything that tries to connect fails clearly.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CampusPulseDbContext>
{
    public CampusPulseDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        var options = new DbContextOptionsBuilder<CampusPulseDbContext>();
        DependencyInjection.Configure(options, string.IsNullOrWhiteSpace(connectionString) ? null : connectionString);
        return new CampusPulseDbContext(options.Options);
    }
}
