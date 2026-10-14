using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CampusPulse.Api.Tests;

/// <summary>
/// Stands in for Microsoft: a local RSA key signs tokens shaped like UPT's v2 access tokens, and
/// the app is pointed at that key instead of the tenant's metadata (no network).
/// </summary>
public static class TestAuth
{
    public const string TenantId = "11111111-2222-3333-4444-555555555555";
    public const string ClientId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    public static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "test-key" };

    public static string Issuer(string tenant = TenantId) => $"https://login.microsoftonline.com/{tenant}/v2.0";

    /// <summary>Configures sign-in for <see cref="TenantId"/> / <see cref="ClientId"/> and trusts <see cref="Key"/>.</summary>
    public static void Configure(IWebHostBuilder builder)
    {
        builder.UseSetting("Auth:TenantId", TenantId);
        builder.UseSetting("Auth:ClientId", ClientId);
        builder.ConfigureTestServices(services =>
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                // Runs after JwtBearer's own post-configure, which already made a manager that
                // would fetch the tenant's metadata: replace it with a static one.
                var configuration = new OpenIdConnectConfiguration { Issuer = Issuer() };
                configuration.SigningKeys.Add(Key);
                options.Configuration = configuration;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            }));
    }

    /// <summary>A v2 access token for a UPT member; each argument bends one claim (null leaves it out).</summary>
    public static string Token(
        string? audience = null,
        string? issuer = null,
        string? tenant = null,
        string? scope = "access_as_user",
        string? oid = "oid-ana",
        string? azp = ClientId,
        string? idp = null,
        string[]? roles = null,
        bool expired = false,
        SecurityKey? signWith = null,
        string algorithm = SecurityAlgorithms.RsaSha256,
        string? email = "ana@student.upt.ro",
        string? name = "Ana Pop",
        IReadOnlyDictionary<string, object>? extra = null)
    {
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object>
        {
            ["tid"] = tenant ?? TenantId,
            ["ver"] = "2.0",
        };
        void Add(string type, object? value)
        {
            if (value is not null)
            {
                claims[type] = value;
            }
        }

        Add("scp", scope);
        Add("oid", oid);
        Add("azp", azp);
        Add("idp", idp);
        Add("roles", roles);
        Add("preferred_username", email);
        Add("name", name);
        foreach (var (type, value) in extra ?? new Dictionary<string, object>())
        {
            claims[type] = value;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer ?? Issuer(),
            Audience = audience ?? ClientId,
            Claims = claims,
            IssuedAt = expired ? now.AddHours(-2) : now.AddMinutes(-1),
            NotBefore = expired ? now.AddHours(-2) : now.AddMinutes(-1),
            Expires = expired ? now.AddMinutes(-10) : now.AddHours(1),
            SigningCredentials = new SigningCredentials(signWith ?? Key, algorithm),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
