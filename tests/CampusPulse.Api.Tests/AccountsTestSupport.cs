using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CampusPulse.Api.Accounts;
using CampusPulse.Api.Accounts.Email;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CampusPulse.Api.Tests;

/// <summary>
/// The app on a test database with sign-in emails captured (<see cref="Email"/>) and the app's clock in the test's hand
/// (<see cref="Clock"/>, starting now, so UPT tokens, checked against the real clock, stay valid). Our own tokens are
/// issued and checked on that clock.
/// </summary>
public sealed class AccountsApp : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public AccountsApp(PostgresAppFactory factory, IReadOnlyDictionary<string, string>? settings = null, ILoggerProvider? logs = null)
    {
        App = factory.WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IEmailSender>(Email);
                services.AddSingleton<TimeProvider>(Clock);
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options => options.TimeProvider = TimeProvider.System);
                if (logs is not null)
                {
                    services.AddSingleton<ILoggerProvider>(logs);
                }
            });
        });
        Client = App.CreateClient();
    }

    public WebApplicationFactory<Program> App { get; }

    public HttpClient Client { get; }

    public CapturingEmailSender Email { get; } = new();

    public TestClock Clock { get; } = new(DateTimeOffset.UtcNow);

    public static string NewEmail() => $"student-{Guid.NewGuid():N}@example.com";

    public Task<HttpResponseMessage> Post(string path, object body, string? bearer = null) => Send(HttpMethod.Post, path, body, bearer);

    public async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null, string? bearer = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return await Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> StartEmail(string email, string language = "en") =>
        Post("/auth/email/start", new { email, language });

    public Task<HttpResponseMessage> VerifyEmail(string email, string code) => Post("/auth/email/verify", new { email, code });

    /// <summary>Signs in with an email code, as an app does.</summary>
    public async Task<TokenResponse> SignInByEmail(string email)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await StartEmail(email)).StatusCode);
        return await Tokens(await VerifyEmail(email, Email.CodeFor(email)));
    }

    /// <summary>Signs in with UPT (<c>POST /auth/microsoft</c>) as the UPT account <paramref name="oid"/>.</summary>
    public async Task<TokenResponse> SignInWithUpt(string oid, string? email = TestAuth.EmailOfOid, string? name = "Ana Pop") =>
        await Tokens(await Post("/auth/microsoft", new { accessToken = TestAuth.Token(oid: oid, email: email, name: name) }));

    public static async Task<TokenResponse> Tokens(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
    }

    /// <summary>The status and the ProblemDetails <c>type</c> of a refusal.</summary>
    public static async Task<(HttpStatusCode Status, string? Type)> Problem(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return (response.StatusCode, body.TryGetProperty("type", out var type) ? type.GetString() : null);
    }

    public void Dispose() => App.Dispose();
}
