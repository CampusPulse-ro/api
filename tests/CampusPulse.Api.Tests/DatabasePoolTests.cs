using CampusPulse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace CampusPulse.Api.Tests;

/// <summary>The connection pool is sized explicitly (<c>Database:MaxPoolSize</c>, <c>Database:MinPoolSize</c>, else the
/// connection string's size), not left at Npgsql's 100 connections per process.</summary>
public sealed class DatabasePoolTests
{
    private const string ConnectionString = "Host=localhost;Database=campuspulse;Username=campuspulse;Password=secret";

    private static NpgsqlConnectionStringBuilder Sized(string connectionString, params (string Key, string Value)[] settings) =>
        new(DependencyInjection.WithPoolSize(connectionString,
            new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(s => $"Database:{s.Key}", s => (string?)s.Value)).Build()));

    [Fact]
    public void By_default_a_process_holds_at_most_20_connections()
    {
        var pool = Sized(ConnectionString);

        Assert.Equal((20, 0), (pool.MaxPoolSize, pool.MinPoolSize));
        Assert.Equal("secret", pool.Password);   // the rest is kept as it was
    }

    [Fact]
    public void The_settings_win_over_the_connection_string()
    {
        var pool = Sized(ConnectionString + ";Maximum Pool Size=100", ("MaxPoolSize", "8"), ("MinPoolSize", "2"));

        Assert.Equal((8, 2), (pool.MaxPoolSize, pool.MinPoolSize));
    }

    [Theory]
    [InlineData(";Maximum Pool Size=40;Minimum Pool Size=5")]
    [InlineData(";MaxPoolSize=40;MinPoolSize=5")]
    public void Without_settings_a_size_in_the_connection_string_is_kept(string sizes)
    {
        var pool = Sized(ConnectionString + sizes);

        Assert.Equal((40, 5), (pool.MaxPoolSize, pool.MinPoolSize));
    }

    [Theory]
    [InlineData("MaxPoolSize", "0")]
    [InlineData("MaxPoolSize", "lots")]
    [InlineData("MinPoolSize", "-1")]
    [InlineData("MinPoolSize", "30")]
    public void Nonsense_fails_the_start(string key, string value) =>
        Assert.Throws<InvalidOperationException>(() => Sized(ConnectionString, (key, value)));
}
