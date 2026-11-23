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

    /// <summary>The rate limits can refuse any call, so every operation lists the 429 (a ProblemDetails); the GETs that
    /// answer conditional requests list the 304.</summary>
    [Fact]
    public async Task The_contract_lists_429_everywhere_and_304_on_the_cached_reads()
    {
        var doc = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var operations = doc.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject().Select(op => (Path: path.Name, Method: op.Name, Responses: op.Value.GetProperty("responses"))))
            .ToList();

        Assert.All(operations, op => Assert.True(op.Responses.TryGetProperty("429", out var tooMany)
            && tooMany.GetProperty("content").TryGetProperty("application/problem+json", out _), $"{op.Method} {op.Path}: no 429"));
        var cached = operations.Where(op => op.Responses.TryGetProperty("304", out _)).Select(op => $"{op.Method} {op.Path}").Order(StringComparer.Ordinal);
        Assert.Equal(
            [
                "get /me", "get /me/reminders", "get /me/settings", "get /me/subjects", "get /me/timetable", "get /rooms",
                "get /rooms/{code}/timetable", "get /semesters/current", "get /timetable/groups/{id}", "get /timetable/groups/{id}/choices",
                "get /timetable/programmes",
            ], cached);
    }
}
