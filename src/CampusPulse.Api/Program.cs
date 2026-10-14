using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CampusPulse.Api;
using CampusPulse.Api.Auth;
using CampusPulse.Api.Logging;
using CampusPulse.Api.Push;
using CampusPulse.Api.Users;
using CampusPulse.Infrastructure;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// JSON logs to stdout, and to a rolling file when Logging:File:Path is set (see LoggingSetup).
builder.AddCampusPulseLogging();

// Optional error reporting (not enabled; his decision, CampusPulse-ro/api#10): Sentry would be
// `builder.WebHost.UseSentry(o => o.Dsn = builder.Configuration["Sentry:Dsn"])` with the DSN in the
// env file, plus an allow rule for its host in the Pi's network guard (deploy/pi/netguard.nft).

// The build writes openapi/v1.json by running this file through GetDocument.Insider, with no
// configuration and no database. The context is registered the same way there, just without
// the fail-fast check; every real start still fails fast without a connection string.
var writingOpenApiDocument = Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";
builder.Services.AddCampusPulsePersistence(builder.Configuration,
    requireConnectionString: !writingOpenApiDocument);

// JSON shapes the OpenAPI document (and so the generated TypeScript types) describes: integers
// as numbers only (without Strict, .NET writes them as ["integer","string"] → `number | string`),
// enums as their camelCase names, like the properties (`"android" | "ios"` in TypeScript).
// Request bodies are read as strictly as the contract describes them: an enum only by its name
// (never a number), and every constructor parameter of a body record present.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
});

// A body that can't be read is a 400 in every environment (Development would throw instead).
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

builder.Services.AddOpenApi(options => options.AddBearerSecurity());

builder.Services.AddSingleton(TimeProvider.System);

// Push notifications through Expo, and the background check of their receipts (see PushSetup).
builder.Services.AddCampusPulsePush();

// AddAuthentication registers Data Protection, which nothing here uses: bearer tokens only, no
// cookies or antiforgery. Ephemeral keys say so, and keep the startup log free of warnings about
// unpersisted keys. A feature that must read protected data after a restart persists keys first.
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();

// Microsoft sign-in (UPT accounts). Unconfigured, it fails closed: see AuthSetup.
var auth = builder.Services.AddCampusPulseAuth(builder.Configuration);

// Unhandled exceptions become a ProblemDetails 500; the exception itself is logged with its stack
// trace by the exception handler. Every problem carries requestId (the X-Request-Id, which every
// log line of the request has as RequestId) besides the default traceId (the W3C trace id, for
// tracing tools), so a client's error report can be matched to its log lines.
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions["requestId"] = context.HttpContext.TraceIdentifier);

// The web app calls the API from the browser. Origins per environment: Cors:AllowedOrigins
// (see CorsOrigins). Bearer tokens, not cookies, so no credentials mode. X-Request-Id is exposed so
// the web app can read it (a browser hides non-safelisted response headers from scripts otherwise).
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .SetIsOriginAllowed(origin => CorsOrigins.IsAllowed(origin, allowedOrigins))
    .AllowAnyHeader()
    .AllowAnyMethod()
    .WithExposedHeaders(RequestIds.HeaderName)));

var app = builder.Build();

if (auth is null && !writingOpenApiDocument)
{
    app.Logger.LogWarning(
        "Sign-in is NOT configured (Auth__TenantId, Auth__ClientId unset): every endpoint that needs it answers 401.");
}

// Outermost, so the request's log line and its X-Request-Id cover the exception handler too.
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseExceptionHandler();
// A 400 with no body (a request body that couldn't be read: bad JSON, a missing field, an enum
// value outside the contract) gets a ProblemDetails body like every other error. Other empty
// statuses (401 from the token check, an unknown path's 404) stay as they are.
app.UseStatusCodePages(async context =>
{
    var http = context.HttpContext;
    if (http.Response.StatusCode == StatusCodes.Status400BadRequest)
    {
        await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails =
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Bad request",
                Detail = "The request body is missing, isn't JSON, or doesn't match the contract (/openapi/v1.json).",
            },
        });
    }
});
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// Served in every environment, not only Development: this document is the contract the
// mobile and web clients generate their typed API client from, so it has to be reachable
// on the deployed API too. Every endpoint needs sign-in unless it is marked AllowAnonymous (the
// fallback policy in AuthSetup); these few are public.
app.MapOpenApi().AllowAnonymous();

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
    .AllowAnonymous()
    .ExcludeFromDescription();

// Liveness only: it does not touch the database, so it answers while Postgres is down.
app.MapGet("/health", () => Results.Ok(new HealthResponse("ok")))
    .AllowAnonymous()
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
    .AllowAnonymous()
    .WithName("GetReadiness")
    .WithTags("Health")
    .Produces<HealthResponse>(StatusCodes.Status200OK)
    .Produces<HealthResponse>(StatusCodes.Status503ServiceUnavailable);

app.MapMe();
app.MapPush();

app.Run();

/// <summary>Body of <c>GET /health</c> and <c>GET /health/ready</c>: <c>ok</c> or <c>unavailable</c>.</summary>
public sealed record HealthResponse(string Status);

/// <summary>Marker so the integration tests can host this app in-process.</summary>
public partial class Program;
