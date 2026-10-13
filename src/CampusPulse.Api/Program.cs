using CampusPulse.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCampusPulsePersistence(builder.Configuration);
builder.Services.AddOpenApi();

var app = builder.Build();

// Served in every environment, not only Development: this document is the contract the
// mobile and web clients generate their typed API client from, so it has to be reachable
// on the deployed API too.
app.MapOpenApi();

// A friendly root, so opening the base URL in a browser shows the API is alive instead of
// a bare 404, and names the paths a human would look for.
app.MapGet("/", () => Results.Ok(new
{
    service = "campuspulse-api",
    status = "ok",
    docs = "/openapi/v1.json",
    health = "/health",
}))
    .ExcludeFromDescription();

// Liveness only: it does not touch the database, so it answers while Postgres is down.
app.MapGet("/health", () => Results.Ok(new HealthResponse("ok")))
    .WithName("GetHealth")
    .WithTags("Health");

app.Run();

/// <summary>Body of <c>GET /health</c>.</summary>
public sealed record HealthResponse(string Status);

/// <summary>Marker so the integration tests can host this app in-process.</summary>
public partial class Program;
