using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CampusPulse.Api.Auth;
using CampusPulse.Api.Users;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CampusPulse.Api.Tests;

/// <summary>Sign-in configured, no database: every refused token must be 401 before /me runs.</summary>
public sealed class AuthTests(AuthTests.ConfiguredFactory factory) : IClassFixture<AuthTests.ConfiguredFactory>
{
    public sealed class ConfiguredFactory : WebApplicationFactory<Program>
    {
        public CollectingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Postgres",
                "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=2");
            TestAuth.Configure(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<ILoggerProvider>(Logs));
        }
    }

    private const string OtherTenant = "99999999-2222-3333-4444-555555555555";

    public static TheoryData<string, string> RefusedTokens => new()
    {
        { "another app's audience", TestAuth.Token(audience: "api://00000000-1111-2222-3333-444444444444") },
        { "another tenant (issuer and tid)", TestAuth.Token(issuer: TestAuth.Issuer("99999999-2222-3333-4444-555555555555"), tenant: "99999999-2222-3333-4444-555555555555") },
        { "the right issuer but another tid", TestAuth.Token(tenant: "99999999-2222-3333-4444-555555555555") },
        { "the v1 issuer", TestAuth.Token(issuer: $"https://sts.windows.net/{TestAuth.TenantId}/") },
        { "no access_as_user scope", TestAuth.Token(scope: "User.Read openid") },
        { "a scope that only contains the name", TestAuth.Token(scope: "access_as_user_admin") },
        { "expired", TestAuth.Token(expired: true) },
        { "signed by another key", TestAuth.Token(signWith: new RsaSecurityKey(RSA.Create(2048)) { KeyId = "test-key" }) },
        { "no oid", TestAuth.Token(oid: null) },
        { "issued to another app (azp)", TestAuth.Token(azp: "00000000-1111-2222-3333-444444444444") },
        { "no azp", TestAuth.Token(azp: null) },
        { "a guest from another tenant (idp)", TestAuth.Token(idp: $"https://sts.windows.net/{OtherTenant}/") },
        { "a guest with a personal Microsoft account (idp)", TestAuth.Token(idp: "live.com") },
        { "an app-only token (roles, no scp)", TestAuth.Token(scope: null, roles: ["Users.Read.All"], name: null, email: null,
            extra: new Dictionary<string, object> { ["idtyp"] = "app" }) },
        { "an ID token (aud = the bare client id, no scp, no azp)", TestAuth.Token(audience: TestAuth.ClientId, scope: null, azp: null,
            extra: new Dictionary<string, object> { ["nonce"] = "n-1" }) },
        { "signed with RS512, not RS256", TestAuth.Token(algorithm: SecurityAlgorithms.RsaSha512) },
    };

    [Fact]
    public async Task Without_a_token_me_is_401()
    {
        var response = await factory.CreateClient().GetAsync("/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task Without_a_token_creating_or_deleting_me_is_401(string method)
    {
        var response = await factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), "/me"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(RefusedTokens))]
    public async Task A_refused_token_is_401(string why, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await factory.CreateClient().SendAsync(request);

        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{why}: got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task A_refusal_is_logged_at_warning_with_its_reason_and_never_the_token()
    {
        var foreignApp = TestAuth.Token(azp: "00000000-1111-2222-3333-444444444444");
        var expired = TestAuth.Token(expired: true);
        foreach (var token in new[] { foreignApp, expired })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            await factory.CreateClient().SendAsync(request);
        }

        var refusals = factory.Logs.Entries.Where(e => e.Category == AuthSetup.LogCategory && e.Level == LogLevel.Warning).ToList();
        Assert.Contains(refusals, e => e.Message == "Bearer token refused: issued to another app (azp)");
        Assert.Contains(refusals, e => e.Message == "Bearer token refused: SecurityTokenExpiredException");
        Assert.DoesNotContain(factory.Logs.Entries, e => e.Message.Contains(foreignApp) || e.Message.Contains(expired));
    }

    [Fact]
    public async Task Only_the_root_health_and_the_contract_are_public()
    {
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
        // The fallback policy: anything else, even a path that doesn't exist, needs a token first.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/no-such-endpoint")).StatusCode);
        var signedIn = new HttpRequestMessage(HttpMethod.Get, "/no-such-endpoint");
        signedIn.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token());
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(signedIn)).StatusCode);
    }

    [Fact]
    public async Task The_contract_describes_the_bearer_scheme_and_me()
    {
        var doc = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/openapi/v1.json");

        var scheme = doc.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());
        var me = doc.GetProperty("paths").GetProperty("/me").GetProperty("get");
        Assert.True(me.GetProperty("security")[0].TryGetProperty("Bearer", out _));
        Assert.True(me.GetProperty("responses").TryGetProperty("200", out _));
        Assert.True(me.GetProperty("responses").TryGetProperty("401", out _));
        Assert.True(me.GetProperty("responses").TryGetProperty("404", out _));
        var postMe = doc.GetProperty("paths").GetProperty("/me").GetProperty("post");
        Assert.True(postMe.GetProperty("security")[0].TryGetProperty("Bearer", out _));
        foreach (var status in new[] { "200", "201", "401" })
        {
            Assert.True(postMe.GetProperty("responses").TryGetProperty(status, out _), $"POST /me {status}");
        }

        var deleteMe = doc.GetProperty("paths").GetProperty("/me").GetProperty("delete");
        Assert.True(deleteMe.GetProperty("security")[0].TryGetProperty("Bearer", out _));
        Assert.True(deleteMe.GetProperty("responses").TryGetProperty("204", out _));
        Assert.True(deleteMe.GetProperty("responses").TryGetProperty("401", out _));
        // Every operation is secured except the anonymous ones, the same rule as the fallback policy.
        foreach (var path in doc.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                var secured = operation.Value.TryGetProperty("security", out _);
                Assert.True(secured != path.Name.StartsWith("/health"), $"{operation.Name} {path.Name}: secured={secured}");
            }
        }
    }
}

/// <summary>The startup check of the tenant's sign-in metadata (the Pi reaches it through a proxy).</summary>
public sealed class AuthMetadataCheckTests
{
    private sealed class Factory(CollectingLoggerProvider logs, bool reachable) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Postgres",
                "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=2");
            TestAuth.Configure(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<ILoggerProvider>(logs);
                if (!reachable)
                {
                    services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                    {
                        options.Configuration = null;
                        options.ConfigurationManager = new UnreachableConfigurationManager();
                    });
                }
            });
        }
    }

    /// <summary>What the network guard without the proxy looks like to JwtBearer.</summary>
    private sealed class UnreachableConfigurationManager : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) =>
            throw new InvalidOperationException("IDX20803: Unable to obtain configuration.",
                new HttpRequestException("Connection refused (login.microsoftonline.com:443)"));

        public void RequestRefresh()
        {
        }
    }

    private static Task CheckDone(WebApplicationFactory<Program> factory) =>
        factory.Services.GetServices<IHostedService>().OfType<AuthMetadataCheck>().Single().ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task Unreachable_metadata_is_one_error_and_the_app_still_serves()
    {
        var logs = new CollectingLoggerProvider();
        using var factory = new Factory(logs, reachable: false);
        var client = factory.CreateClient();
        await CheckDone(factory);

        var error = Assert.Single(logs.Entries, e => e.Level >= LogLevel.Error);
        Assert.Equal(AuthSetup.LogCategory, error.Category);
        Assert.Contains("InvalidOperationException", error.Message);
        Assert.Contains("HttpRequestException", error.Message);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

        // A server fault, not a bad token: 500 (a ProblemDetails), so a client doesn't send the
        // student back through sign-in for it.
        var request = new HttpRequestMessage(HttpMethod.Get, "/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token());
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Reachable_metadata_is_one_information_line()
    {
        var logs = new CollectingLoggerProvider();
        using var factory = new Factory(logs, reachable: true);
        factory.CreateClient();
        await CheckDone(factory);

        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error);
        Assert.Single(logs.Entries, e => e.Category == AuthSetup.LogCategory && e.Message.StartsWith("Sign-in metadata loaded"));
    }
}

/// <summary>Sign-in NOT configured: the app still starts, health works, and nothing gets through.</summary>
public sealed class UnconfiguredAuthTests(CampusPulseAppFactory factory) : IClassFixture<CampusPulseAppFactory>
{
    [Fact]
    public async Task Even_a_well_formed_token_is_401_and_health_still_answers()
    {
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token());

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }
}

/// <summary>Sign-in configured, real Postgres: POST /me creates the student, GET /me reads them.</summary>
public sealed class MeTests(PostgresAppFactory factory) : IClassFixture<PostgresAppFactory>
{
    private async Task<HttpResponseMessage> Send(HttpMethod method, string token)
    {
        var request = new HttpRequestMessage(method, "/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await factory.CreateClient().SendAsync(request);
    }

    private Task<HttpResponseMessage> GetMe(string token) => Send(HttpMethod.Get, token);

    private Task<HttpResponseMessage> PostMe(string token) => Send(HttpMethod.Post, token);

    [PostgresFact]
    public async Task Get_never_creates_a_student_and_answers_404_problem_details()
    {
        var oid = "oid-" + Guid.NewGuid().ToString("N");

        var response = await GetMe(TestAuth.Token(oid: oid));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.True(problem.TryGetProperty("requestId", out _));
        await using var db = factory.CreateDbContext();
        Assert.False(await db.Users.AnyAsync(u => u.EntraObjectId == oid));
    }

    [PostgresFact]
    public async Task Post_creates_the_student_then_returns_the_same_one_and_get_reads_it()
    {
        var oid = "oid-" + Guid.NewGuid().ToString("N");

        var created = await PostMe(TestAuth.Token(oid: oid));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var me = await created.Content.ReadFromJsonAsync<MeResponse>();
        Assert.Equal("ana@student.upt.ro", me!.Email);
        Assert.Equal("Ana Pop", me.DisplayName);

        var again = await PostMe(TestAuth.Token(oid: oid));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(me.Id, (await again.Content.ReadFromJsonAsync<MeResponse>())!.Id);

        var read = await GetMe(TestAuth.Token(oid: oid));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(me, await read.Content.ReadFromJsonAsync<MeResponse>());

        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.Users.CountAsync(u => u.EntraObjectId == oid));
    }

    [PostgresFact]
    public async Task Both_audience_forms_are_accepted()
    {
        var oid = "oid-" + Guid.NewGuid().ToString("N");

        Assert.Equal(HttpStatusCode.Created, (await PostMe(TestAuth.Token(oid: oid, audience: TestAuth.ClientId))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetMe(TestAuth.Token(oid: oid, audience: $"api://{TestAuth.ClientId}"))).StatusCode);
    }

    [PostgresFact]
    public async Task Members_are_accepted_with_no_idp_or_the_tenants_own()
    {
        Assert.Equal(HttpStatusCode.Created, (await PostMe(TestAuth.Token(oid: "oid-" + Guid.NewGuid().ToString("N")))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostMe(TestAuth.Token(oid: "oid-" + Guid.NewGuid().ToString("N"),
            idp: $"https://sts.windows.net/{TestAuth.TenantId}/"))).StatusCode);
    }

    [PostgresFact]
    public async Task A_token_without_name_or_email_keeps_the_stored_ones()
    {
        var oid = "oid-" + Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.Created, (await PostMe(TestAuth.Token(oid: oid))).StatusCode);

        var bare = await GetMe(TestAuth.Token(oid: oid, name: null, email: null));

        var me = await bare.Content.ReadFromJsonAsync<MeResponse>();
        Assert.Equal("ana@student.upt.ro", me!.Email);
        Assert.Equal("Ana Pop", me.DisplayName);
    }

    [PostgresFact]
    public async Task Concurrent_first_posts_make_one_student()
    {
        var oid = "oid-" + Guid.NewGuid().ToString("N");
        var token = TestAuth.Token(oid: oid);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => PostMe(token)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"{(int)r.StatusCode}"));
        var ids = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<MeResponse>())!.Id));
        Assert.Single(ids.Distinct());
        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.Users.CountAsync(u => u.EntraObjectId == oid));
    }
}
