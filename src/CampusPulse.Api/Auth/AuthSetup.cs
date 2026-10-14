using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace CampusPulse.Api.Auth;

public static class AuthSetup
{
    /// <summary>
    /// JWT bearer tokens from UPT's tenant. A token must: be signed by Microsoft (keys from the
    /// tenant's metadata), come from exactly <c>https://login.microsoftonline.com/{tenant}/v2.0</c>,
    /// be for this API (<see cref="AuthSettings.Audiences"/>), carry <c>tid</c> = the tenant and the
    /// <c>access_as_user</c> scope in <c>scp</c>, and have an <c>oid</c>. Anything else is 401.
    /// Unconfigured (no settings): fails closed. Requests without a token stay anonymous (so
    /// /health works), and any token is refused, so an endpoint that needs sign-in is always 401.
    /// </summary>
    public static AuthSettings? AddCampusPulseAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = AuthSettings.FromConfiguration(configuration);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Keep the claims as Microsoft names them (scp, oid, tid), not mapped to long URIs.
                options.MapInboundClaims = false;

                if (settings is null)
                {
                    options.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = context =>
                        {
                            if (context.Request.Headers.Authorization.Count > 0)
                            {
                                context.Fail("Sign-in is not configured on this server (Auth__TenantId, Auth__Audience).");
                            }
                            else
                            {
                                context.NoResult();
                            }

                            return Task.CompletedTask;
                        },
                    };
                    return;
                }

                options.Authority = settings.Issuer;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = settings.Issuer,
                    ValidateAudience = true,
                    ValidAudiences = settings.Audiences,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(2),
                    NameClaimType = "name",
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        var principal = context.Principal!;
                        if (principal.FindFirst("tid")?.Value != settings.TenantId)
                        {
                            context.Fail("The token is from another tenant.");
                        }
                        else if (!HasScope(principal.FindFirst("scp")?.Value))
                        {
                            context.Fail($"The token lacks the {AuthSettings.Scope} scope.");
                        }
                        else if (string.IsNullOrEmpty(principal.FindFirst("oid")?.Value))
                        {
                            context.Fail("The token has no oid.");
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorization();
        return settings;
    }

    private static bool HasScope(string? scp) =>
        scp is not null && scp.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(AuthSettings.Scope, StringComparer.Ordinal);
}
