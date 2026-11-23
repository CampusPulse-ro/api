using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CampusPulse.Api;
using CampusPulse.Api.Accounts;
using CampusPulse.Api.Accounts.Email;
using CampusPulse.Api.Auth;
using CampusPulse.Api.Deadlines;
using CampusPulse.Api.Http;
using CampusPulse.Api.Logging;
using CampusPulse.Api.Metrics;
using CampusPulse.Api.Push;
using CampusPulse.Api.RateLimiting;
using CampusPulse.Api.Reminders;
using CampusPulse.Api.Timetable;
using CampusPulse.Api.Timetable.Import;
using CampusPulse.Api.Users;
using CampusPulse.Infrastructure;
using Microsoft.Net.Http.Headers;

// The one-off timetable import (CampusPulse-ro/api#5) is a mode of this same binary, so the Pi runs it
// from the deployed release like the migrations: `CampusPulse.Api import-timetable …`. It never starts
// the web app.
if (args is [ImportTimetableCommand.Name, .. var importArgs])
{
    return await ImportTimetableCommand.RunAsync(importArgs, Console.Out, Console.Error);
}

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

// Metrics on a loopback-only listener when Metrics:Port is set (see MetricsSetup); the instruments always exist.
builder.AddCampusPulseMetrics();

// The shared timetable, kept in memory and reloaded after an import (see TimetableCache).
builder.Services.AddCampusPulseTimetable(builder.Configuration);

// Push notifications through Expo, and the background check of their receipts (see PushSetup).
builder.Services.AddCampusPulsePush();

// Class reminders: a push before each class, at the lead the student picked (see ClassReminderSender).
builder.Services.AddCampusPulseReminders(builder.Configuration);

// Sign-in: our own tokens (Auth:SigningKey) and, for installed app versions, UPT Entra tokens; each request's bearer
// goes to the scheme of its issuer. Unconfigured, each fails closed: see AuthSetup.
var auth = builder.AddCampusPulseAuth();

// The sign-in code emails (Resend; a logging sender in Development), and the hourly cleanup of expired sign-in rows.
builder.AddCampusPulseEmail();
builder.Services.AddSingleton<SignInCleanup>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SignInCleanup>());

// Behind nginx: the client's address and scheme from nginx's X-Forwarded-* headers, trusted from loopback only.
builder.Services.AddCampusPulseProxyHeaders();

// Per-client rate limits (RateLimits section, see RateLimitSetup).
builder.Services.AddCampusPulseRateLimiting(builder.Configuration);

// Body size and time limits per request, no Server header, Brotli/gzip (Limits section, see RequestLimits).
builder.AddCampusPulseRequestLimits();

// HSTS on HTTPS responses (behind nginx, every public request): a browser never tries plain HTTP for a year.
builder.Services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(365));

// Unhandled exceptions become a ProblemDetails 500; the exception itself is logged with its stack
// trace by the exception handler. Every problem carries requestId (the X-Request-Id, which every
// log line of the request has as RequestId) besides the default traceId (the W3C trace id, for
// tracing tools), so a client's error report can be matched to its log lines.
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions["requestId"] = context.HttpContext.TraceIdentifier);

// The web app calls the API from the browser. Origins per environment: Cors:AllowedOrigins
// (see CorsOrigins). Bearer tokens, not cookies, so no credentials mode. X-Request-Id is exposed so
// the web app can read it (a browser hides non-safelisted response headers from scripts otherwise), and so is
// Retry-After, for a 429. A browser may reuse a preflight's answer for 2 hours (Chrome's cap) instead of sending an
// OPTIONS before every call: every call carries Authorization, so every call would need one.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .SetIsOriginAllowed(origin => CorsOrigins.IsAllowed(origin, allowedOrigins))
    .AllowAnyHeader()
    .AllowAnyMethod()
    .WithExposedHeaders(RequestIds.HeaderName, HeaderNames.RetryAfter)
    .SetPreflightMaxAge(TimeSpan.FromHours(2))));

var app = builder.Build();

if (!writingOpenApiDocument)
{
    if (auth.Entra is null)
    {
        app.Logger.LogWarning(
            "UPT sign-in is NOT configured (Auth__TenantId, Auth__ClientId unset): UPT tokens are refused and /auth/microsoft answers 503.");
    }

    if (!auth.OwnTokens)
    {
        app.Logger.LogWarning("Auth__SigningKey is NOT set: no session can be issued, so /auth/* answers 503 and only UPT tokens work.");
    }
    else if (auth.OwnTokensEphemeral)
    {
        app.Logger.LogWarning("Development: Auth__SigningKey is not set, so access tokens are signed with a key that dies with this process.");
    }
}

// Metrics first, on their own loopback port only (see MetricsSetup).
app.UseCampusPulseMetrics();
// The client's real address and scheme, before anything reads them (the rate limits, HSTS, compression).
app.UseForwardedHeaders();
// Outermost after those, so the request's log line and its X-Request-Id cover the exception handler too.
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseMiddleware<SecurityHeaders>();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseResponseCompression();
app.UseExceptionHandler();
// A 400 with no body (a request body that couldn't be read: bad JSON, a missing field, an enum
// value outside the contract) or a 413 (a body over Limits:MaxRequestBodyBytes) gets a ProblemDetails
// body like every other error, with its own type (invalid-request, body-too-large) for clients to branch on. Other empty statuses (401 from the token check, an unknown path's
// 404) stay as they are.
app.UseStatusCodePages(async context =>
{
    var http = context.HttpContext;
    var problem = http.Response.StatusCode switch
    {
        StatusCodes.Status400BadRequest => new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Type = "invalid-request",
            Status = StatusCodes.Status400BadRequest,
            Title = "Bad request",
            Detail = "The request doesn't match the contract (/openapi/v1.json): a body that is missing, isn't JSON or lacks a "
                + "required field, or a query parameter of the wrong type.",
        },
        StatusCodes.Status413PayloadTooLarge => new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Type = "body-too-large",
            Status = StatusCodes.Status413PayloadTooLarge,
            Title = "Request body too large",
            Detail = "Request bodies are limited to a few kilobytes: this API only takes small JSON bodies.",
        },
        _ => null,
    };
    if (problem is not null)
    {
        await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = problem,
        });
    }
});
app.UseCors();
app.UseAuthentication();
app.UseCampusPulseRateLimiting();
app.UseRequestTimeouts();
app.UseAuthorization();

// Served in every environment, not only Development: this document is the contract the
// mobile and web clients generate their typed API client from, so it has to be reachable
// on the deployed API too. Every endpoint needs sign-in unless it is marked AllowAnonymous (the
// fallback policy in AuthSetup); these few are public.
app.MapOpenApi().AllowAnonymous();

// Every endpoint below can be refused by the rate limits: the contract says so, with the ProblemDetails body
// (added last, so it is listed after each endpoint's own responses).
var api = app.MapGroup("");
((IEndpointConventionBuilder)api).Finally(endpoint => endpoint.Metadata.Add(new ProducesResponseTypeMetadata(StatusCodes.Status429TooManyRequests,
    typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), ["application/problem+json"])));

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

// Liveness only: it does not touch the database, so it answers while Postgres is down. Not rate limited (and so not
// in the group above): answering it costs less than refusing it, and a status check must never report a false outage.
app.MapGet("/health", () => Results.Ok(new HealthResponse("ok")))
    .AllowAnonymous()
    .DisableRateLimiting()
    .Produces<HealthResponse>()
    .WithName("GetHealth")
    .WithTags("Health");

// Readiness: the API can reach its database. The deploy checks this one, so a release that
// can't reach its database is rolled back. Bounded, so a hung connection answers 503 fast.
api.MapGet("/health/ready", async (CampusPulseDbContext db, CancellationToken requestAborted) =>
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

api.MapAuth();
api.MapMe();
api.MapDeadlines();
api.MapPush();
api.MapTimetable();
api.MapReminders();

app.Run();
return 0;

/// <summary>Body of <c>GET /health</c> and <c>GET /health/ready</c>: <c>ok</c> or <c>unavailable</c>.</summary>
public sealed record HealthResponse(string Status);

/// <summary>Marker so the integration tests can host this app in-process.</summary>
public partial class Program;
