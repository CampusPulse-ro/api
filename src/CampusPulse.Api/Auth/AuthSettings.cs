namespace CampusPulse.Api.Auth;

/// <summary>
/// Microsoft sign-in for UPT accounts: tokens issued by UPT's Entra tenant for this API's app
/// registration. From <c>Auth__TenantId</c> (the tenant's GUID) and <c>Auth__ClientId</c> (the app
/// registration's client id). The web app, the mobile app and the API are that one registration, so
/// the client id is both the token's audience and the app it was issued to (<c>azp</c>).
/// </summary>
public sealed record AuthSettings(string TenantId, string ClientId)
{
    public const string Scope = "access_as_user";

    /// <summary>The v2 issuer, exactly as Entra writes it (lowercase GUID).</summary>
    public string Issuer => $"https://login.microsoftonline.com/{TenantId}/v2.0";

    /// <summary>The <c>idp</c> of an account that belongs to the tenant, when a token carries one.</summary>
    public string TenantIdentityProvider => $"https://sts.windows.net/{TenantId}/";

    /// <summary>Both audience forms: v2 access tokens carry the client id, v1 ones the default App ID
    /// URI <c>api://&lt;client-id&gt;</c>.</summary>
    public IReadOnlyList<string> Audiences => [ClientId, $"api://{ClientId}"];

    /// <summary>Null when either setting is missing: sign-in is then not configured, and every
    /// endpoint that needs it answers 401.</summary>
    public static AuthSettings? FromConfiguration(IConfiguration configuration)
    {
        var tenant = configuration["Auth:TenantId"]?.Trim();
        var clientId = configuration["Auth:ClientId"]?.Trim();
        if (string.IsNullOrEmpty(tenant) || string.IsNullOrEmpty(clientId))
        {
            return null;
        }

        // Parsed and written back, so the issuer and audiences are in Entra's form (lowercase,
        // hyphenated) whatever case or braces the setting used.
        if (!Guid.TryParse(tenant, out var tenantGuid))
        {
            throw new InvalidOperationException("Auth__TenantId must be the tenant's GUID.");
        }

        if (!Guid.TryParse(clientId, out var clientGuid))
        {
            throw new InvalidOperationException("Auth__ClientId must be the app registration's client id (a GUID).");
        }

        return new AuthSettings(tenantGuid.ToString(), clientGuid.ToString());
    }
}
