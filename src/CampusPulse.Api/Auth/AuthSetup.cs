using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace CampusPulse.Api.Auth;

public static partial class AuthSetup
{
    public const string LogCategory = "CampusPulse.Api.Auth";

    /// <summary>
    /// JWT bearer tokens from UPT's tenant. A token must: be signed with RS256 by Microsoft (keys from
    /// the tenant's metadata), come from exactly <c>https://login.microsoftonline.com/{tenant}/v2.0</c>,
    /// be for this API (<see cref="AuthSettings.Audiences"/>) and issued to this app (<c>azp</c>), carry
    /// <c>tid</c> = the tenant, no foreign <c>idp</c> (guests are refused), the <c>access_as_user</c>
    /// scope in <c>scp</c> (so app-only tokens and ID tokens are refused), and an <c>oid</c>. Anything
    /// else is 401, logged at Warning with the reason only, never the token.
    /// Every endpoint needs a signed-in user unless it is marked <c>AllowAnonymous</c> (the fallback
    /// policy), so a new endpoint is protected by default.
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
                                context.Fail("Sign-in is not configured on this server (Auth__TenantId, Auth__ClientId).");
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
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ClockSkew = TimeSpan.FromMinutes(2),
                    NameClaimType = "name",
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        if (Refusal(context.Principal!, settings) is { } reason)
                        {
                            LogRefused(Logger(context.HttpContext), reason);
                            context.Fail($"The token was refused: {reason}.");
                        }

                        return Task.CompletedTask;
                    },
                    // Signature, issuer, audience, lifetime or algorithm: the exception's type says which.
                    OnAuthenticationFailed = context =>
                    {
                        LogRefused(Logger(context.HttpContext), context.Exception.GetType().Name);
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorization(options =>
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        if (settings is not null)
        {
            services.AddHostedService<AuthMetadataCheck>();
        }

        return settings;
    }

    /// <summary>Why a token that passed the signature, issuer, audience and lifetime checks is still
    /// refused, or null to accept it. Reasons are fixed texts: nothing from the token is echoed.</summary>
    private static string? Refusal(ClaimsPrincipal principal, AuthSettings settings)
    {
        if (principal.FindFirst("tid")?.Value != settings.TenantId)
        {
            return "another tenant";
        }

        if (principal.FindFirst("azp")?.Value != settings.ClientId)
        {
            return "issued to another app (azp)";
        }

        // Members of the tenant (students and staff) carry no idp, or the tenant's own; a guest's names
        // their home tenant or a personal Microsoft account.
        var idp = principal.FindFirst("idp")?.Value;
        if (idp is not null && idp != settings.TenantIdentityProvider && idp != settings.Issuer)
        {
            return "a guest account (idp)";
        }

        if (!HasScope(principal.FindFirst("scp")?.Value))
        {
            return $"no {AuthSettings.Scope} scope";
        }

        if (string.IsNullOrEmpty(principal.FindFirst("oid")?.Value))
        {
            return "no oid";
        }

        return null;
    }

    private static bool HasScope(string? scp) =>
        scp is not null && scp.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(AuthSettings.Scope, StringComparer.Ordinal);

    private static ILogger Logger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "Bearer token refused: {Reason}")]
    private static partial void LogRefused(ILogger logger, string reason);
}
