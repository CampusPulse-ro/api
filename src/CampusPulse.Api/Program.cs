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
    ready = "/health/ready",
}))
    .ExcludeFromDescription();

// Liveness only: it does not touch the database, so it answers while Postgres is down.
app.MapGet("/health", () => Results.Ok(new HealthResponse("ok")))
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
