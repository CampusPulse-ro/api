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
        builder.UseSetting("Auth:Audience", $"api://{ClientId}");
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

    public static string Token(
        string? audience = null,
        string? issuer = null,
        string? tenant = null,
        string scope = "access_as_user",
        string? oid = "oid-ana",
        bool expired = false,
        SecurityKey? signWith = null,
        string email = "ana@student.upt.ro",
        string name = "Ana Pop")
    {
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object>
        {
            ["tid"] = tenant ?? TenantId,
            ["scp"] = scope,
            ["preferred_username"] = email,
            ["name"] = name,
            ["ver"] = "2.0",
        };
        if (oid is not null)
        {
            claims["oid"] = oid;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer ?? Issuer(),
            Audience = audience ?? ClientId,
            Claims = claims,
            IssuedAt = expired ? now.AddHours(-2) : now.AddMinutes(-1),
            NotBefore = expired ? now.AddHours(-2) : now.AddMinutes(-1),
            Expires = expired ? now.AddMinutes(-10) : now.AddHours(1),
            SigningCredentials = new SigningCredentials(signWith ?? Key, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
