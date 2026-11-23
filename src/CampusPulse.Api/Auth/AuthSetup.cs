using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CampusPulse.Api.Auth;

/// <summary>What sign-in is configured: UPT Entra (<see cref="Entra"/>, null when unset) and our own tokens.</summary>
public sealed record AuthConfiguration(AuthSettings? Entra, bool OwnTokens, bool OwnTokensEphemeral);

public static partial class AuthSetup
{
    public const string LogCategory = "CampusPulse.Api.Auth";

    /// <summary>The default scheme: forwards each request to <see cref="CampusPulseTokens.Scheme"/> when its bearer
    /// token is one of ours (issuer <c>campuspulse</c>), else to the UPT Entra bearer
    /// (<see cref="JwtBearerDefaults.AuthenticationScheme"/>), which installed app versions still send.</summary>
    public const string SelectorScheme = "BearerByIssuer";

    /// <summary>
    /// Both bearer schemes (see <see cref="SelectorScheme"/>), our token service (<see cref="CampusPulseTokens"/>) and
    /// the Entra token check for tokens sent in a body (<see cref="EntraTokenValidator"/>). Every endpoint needs a
    /// signed-in caller unless it is marked <c>AllowAnonymous</c> (the fallback policy).
    /// </summary>
    public static AuthConfiguration AddCampusPulseAuth(this WebApplicationBuilder builder)
    {
        var entra = builder.Services.AddEntraBearer(builder.Configuration);
        var key = CampusPulseTokens.KeyFrom(builder.Configuration, builder.Environment, out var ephemeral);
        builder.Services.AddSingleton(provider => new CampusPulseTokens(key, provider.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<EntraTokenValidator>();
        builder.Services.AddAuthentication(SelectorScheme)
            .AddPolicyScheme(SelectorScheme, null, options => options.ForwardDefaultSelector = SchemeOf)
            .AddJwtBearer(CampusPulseTokens.Scheme, options =>
            {
                options.MapInboundClaims = false;
                // Without a key nothing validates: a random one nobody signs with.
                options.TokenValidationParameters = CampusPulseTokens.ValidationParameters(
                    key ?? new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(CampusPulseTokens.MinimumKeyBytes)));
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        if (CampusPulseTokens.UserIdOf(context.Principal!) is null)
                        {
                            LogRefused(Logger(context.HttpContext), "no account id (sub)");
                            context.Fail("The token was refused: no account id.");
                        }

                        return Task.CompletedTask;
                    },
                    OnAuthenticationFailed = context =>
                    {
                        LogRefused(Logger(context.HttpContext), context.Exception.GetType().Name);
                        return Task.CompletedTask;
                    },
                };
            });
        var configuration = new AuthConfiguration(entra, key is not null, ephemeral);
        builder.Services.AddSingleton(configuration);
        return configuration;
    }

    /// <summary>Our token (issuer <c>campuspulse</c>) or not: read without checking anything, only to pick the scheme
    /// that then checks it.</summary>
    private static string SchemeOf(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = header["Bearer ".Length..].Trim();
            var handler = new JsonWebTokenHandler();
            try
            {
                if (handler.CanReadToken(token) && handler.ReadJsonWebToken(token).Issuer == CampusPulseTokens.Issuer)
                {
                    return CampusPulseTokens.Scheme;
                }
            }
            catch (Exception e) when (e is ArgumentException or SecurityTokenException)
            {
                // Not a readable JWT: the Entra scheme refuses it like any other bad token.
            }
        }

        return JwtBearerDefaults.AuthenticationScheme;
    }

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
    private static AuthSettings? AddEntraBearer(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = AuthSettings.FromConfiguration(configuration);

        services.AddAuthentication()
            .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
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
    internal static string? Refusal(ClaimsPrincipal principal, AuthSettings settings)
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
