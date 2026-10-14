using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CampusPulse.Api.Tests;

public sealed class HealthAndContractTests(CampusPulseAppFactory factory)
    : IClassFixture<CampusPulseAppFactory>
{
    [Fact]
    public async Task Health_returns_ok_without_a_database()
    {
        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task OpenApi_document_is_served_and_describes_health()
    {
        var response = await factory.CreateClient().GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("3.", doc.GetProperty("openapi").GetString());
        Assert.True(doc.GetProperty("paths").TryGetProperty("/health", out _));
        Assert.True(doc.GetProperty("paths").TryGetProperty("/health/ready", out var ready));
        var responses = ready.GetProperty("get").GetProperty("responses");
        Assert.True(responses.TryGetProperty("200", out _));
        Assert.True(responses.TryGetProperty("503", out _));
    }
}
