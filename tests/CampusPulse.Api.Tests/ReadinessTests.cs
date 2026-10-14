using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CampusPulse.Api.Tests;

public sealed class ReadinessWithDatabaseTests(PostgresAppFactory factory) : IClassFixture<PostgresAppFactory>
{
    [PostgresFact]
    public async Task Ready_is_200_when_the_database_is_reachable()
    {
        var response = await factory.CreateClient().GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }
}

public sealed class ReadinessWithoutDatabaseTests(CampusPulseAppFactory factory) : IClassFixture<CampusPulseAppFactory>
{
    [Fact]
    public async Task Ready_is_503_when_the_database_is_unreachable_while_liveness_stays_200()
    {
        var client = factory.CreateClient();

        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        var body = await ready.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unavailable", body.GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }
}
