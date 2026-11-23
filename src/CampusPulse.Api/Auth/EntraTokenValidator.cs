using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CampusPulse.Api.Auth;

/// <summary>How an Entra token sent in a body came out.</summary>
public enum EntraCheck
{
    Valid,

    /// <summary>Refused: signature, issuer, audience, lifetime, tenant, app, guest, scope or oid.</summary>
    Invalid,

    /// <summary>Sign-in with UPT isn't configured, or Microsoft's keys can't be loaded right now.</summary>
    Unavailable,
}

/// <summary>
/// Checks a UPT Entra access token that a client sends in a body (<c>POST /auth/microsoft</c>, linking UPT) exactly as
/// the Entra bearer scheme checks one in the Authorization header: the same validation parameters and Microsoft keys
/// (from that scheme's options), then the same refusals (<see cref="AuthSetup.Refusal"/>).
/// </summary>
public sealed partial class EntraTokenValidator(IOptionsMonitor<JwtBearerOptions> bearers, AuthConfiguration auth,
    ILogger<EntraTokenValidator> logger)
{
    public async Task<(EntraCheck Result, ClaimsPrincipal? Principal)> ValidateAsync(string? token, CancellationToken cancellationToken)
    {
        if (auth.Entra is not { } settings)
        {
            return (EntraCheck.Unavailable, null);
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return (EntraCheck.Invalid, null);
        }

        var bearer = bearers.Get(JwtBearerDefaults.AuthenticationScheme);
        var parameters = bearer.TokenValidationParameters.Clone();
        try
        {
            var configuration = await bearer.ConfigurationManager!.GetConfigurationAsync(cancellationToken);
            parameters.IssuerSigningKeys = configuration.SigningKeys;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogKeysUnavailable(logger, e.GetType().Name);
            return (EntraCheck.Unavailable, null);
        }

        var result = await new JsonWebTokenHandler { MapInboundClaims = false }.ValidateTokenAsync(token.Trim(), parameters);
        if (!result.IsValid)
        {
            LogRefused(logger, result.Exception?.GetType().Name ?? "invalid");
            return (EntraCheck.Invalid, null);
        }

        var principal = new ClaimsPrincipal(result.ClaimsIdentity);
        if (AuthSetup.Refusal(principal, settings) is { } reason)
        {
            LogRefused(logger, reason);
            return (EntraCheck.Invalid, null);
        }

        return (EntraCheck.Valid, principal);
    }

    [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "Microsoft token in a request body refused: {Reason}")]
    private static partial void LogRefused(ILogger logger, string reason);

    [LoggerMessage(EventId = 13, Level = LogLevel.Error, Message = "Microsoft's sign-in keys couldn't be loaded ({Reason}): UPT sign-in answers 503")]
    private static partial void LogKeysUnavailable(ILogger logger, string reason);
}
