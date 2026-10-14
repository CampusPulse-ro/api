using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CampusPulse.Api.Users;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace CampusPulse.Api.Tests;

/// <summary>Sign-in configured, no database: every refused token must be 401 before /me runs.</summary>
public sealed class AuthTests(AuthTests.ConfiguredFactory factory) : IClassFixture<AuthTests.ConfiguredFactory>
{
    public sealed class ConfiguredFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Postgres",
                "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=2");
            TestAuth.Configure(builder);
        }
    }

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
    };

    [Fact]
    public async Task Without_a_token_me_is_401()
    {
        var response = await factory.CreateClient().GetAsync("/me");

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
    public async Task Health_needs_no_token()
    {
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/health")).StatusCode);
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
        Assert.False(doc.GetProperty("paths").GetProperty("/health").GetProperty("get").TryGetProperty("security", out _));
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

/// <summary>Sign-in configured, real Postgres: /me creates and returns the student.</summary>
public sealed class MeTests(PostgresAppFactory factory) : IClassFixture<PostgresAppFactory>
{
    private async Task<HttpResponseMessage> GetMe(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await factory.CreateClient().SendAsync(request);
    }

    [PostgresFact]
    public async Task The_first_call_creates_the_student_and_later_calls_return_the_same_one()
    {
        var oid = "oid-" + Guid.NewGuid().ToString("N");

        var first = await GetMe(TestAuth.Token(oid: oid));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var me = await first.Content.ReadFromJsonAsync<MeResponse>();
        Assert.Equal("ana@student.upt.ro", me!.Email);
        Assert.Equal("Ana Pop", me.DisplayName);

        var again = await (await GetMe(TestAuth.Token(oid: oid))).Content.ReadFromJsonAsync<MeResponse>();
        Assert.Equal(me.Id, again!.Id);

        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.Users.CountAsync(u => u.EntraObjectId == oid));
    }

    [PostgresFact]
    public async Task Both_audience_forms_are_accepted()
    {
        var oid = "oid-" + Guid.NewGuid().ToString("N");

        Assert.Equal(HttpStatusCode.OK, (await GetMe(TestAuth.Token(oid: oid, audience: TestAuth.ClientId))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetMe(TestAuth.Token(oid: oid, audience: $"api://{TestAuth.ClientId}"))).StatusCode);
    }

    [PostgresFact]
    public async Task Concurrent_first_calls_make_one_student()
    {
        var oid = "oid-" + Guid.NewGuid().ToString("N");
        var token = TestAuth.Token(oid: oid);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => GetMe(token)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var ids = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<MeResponse>())!.Id));
        Assert.Single(ids.Distinct());
        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.Users.CountAsync(u => u.EntraObjectId == oid));
    }
}
