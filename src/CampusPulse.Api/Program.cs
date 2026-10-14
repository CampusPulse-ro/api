using System.Reflection;
using System.Text.Json.Serialization;
using CampusPulse.Api;
using CampusPulse.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// The build writes openapi/v1.json by running this file through GetDocument.Insider, with no
// configuration and no database. The context is registered the same way there, just without
// the fail-fast check; every real start still fails fast without a connection string.
var writingOpenApiDocument = Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";
builder.Services.AddCampusPulsePersistence(builder.Configuration,
    requireConnectionString: !writingOpenApiDocument);

// JSON shapes the OpenAPI document (and so the generated TypeScript types) describes: integers
// as numbers only (without Strict, .NET writes them as ["integer","string"] → `number | string`),
// enums as their names (string unions in TypeScript).
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddOpenApi();

// The web app calls the API from the browser. Origins per environment: Cors:AllowedOrigins
// (see CorsOrigins). Bearer tokens, not cookies, so no credentials mode.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .SetIsOriginAllowed(origin => CorsOrigins.IsAllowed(origin, allowedOrigins))
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

app.UseCors();

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
    ready = "/health/ready",
}))
    .ExcludeFromDescription();

// Liveness only: it does not touch the database, so it answers while Postgres is down.
app.MapGet("/health", () => Results.Ok(new HealthResponse("ok")))
    .Produces<HealthResponse>()
    .WithName("GetHealth")
    .WithTags("Health");

// Readiness: the API can reach its database. The deploy checks this one, so a release that
// can't reach its database is rolled back. Bounded, so a hung connection answers 503 fast.
app.MapGet("/health/ready", async (CampusPulseDbContext db, CancellationToken requestAborted) =>
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        bool reachable;
        try
        {
            reachable = await db.Database.CanConnectAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!requestAborted.IsCancellationRequested)
        {
            reachable = false;
        }

        return reachable
            ? Results.Ok(new HealthResponse("ok"))
            : Results.Json(new HealthResponse("unavailable"), statusCode: StatusCodes.Status503ServiceUnavailable);
    })
    .WithName("GetReadiness")
    .WithTags("Health")
    .Produces<HealthResponse>(StatusCodes.Status200OK)
    .Produces<HealthResponse>(StatusCodes.Status503ServiceUnavailable);

app.Run();

/// <summary>Body of <c>GET /health</c> and <c>GET /health/ready</c>: <c>ok</c> or <c>unavailable</c>.</summary>
public sealed record HealthResponse(string Status);

/// <summary>Marker so the integration tests can host this app in-process.</summary>
public partial class Program;
