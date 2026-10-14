namespace CampusPulse.Api.Auth;

/// <summary>
/// Microsoft sign-in for UPT accounts: tokens issued by UPT's Entra tenant for this API's app
/// registration. From <c>Auth__TenantId</c> (the tenant's GUID) and <c>Auth__Audience</c> (the app's
/// client id, or its default App ID URI <c>api://&lt;client-id&gt;</c>; both forms are accepted, since
/// v2 access tokens carry the client id and v1 ones the URI).
/// </summary>
public sealed record AuthSettings(string TenantId, IReadOnlyList<string> Audiences)
{
    public const string Scope = "access_as_user";

    public string Issuer => $"https://login.microsoftonline.com/{TenantId}/v2.0";

    /// <summary>Null when either setting is missing: sign-in is then not configured, and every
    /// endpoint that needs it answers 401.</summary>
    public static AuthSettings? FromConfiguration(IConfiguration configuration)
    {
        var tenant = configuration["Auth:TenantId"]?.Trim();
        var audience = configuration["Auth:Audience"]?.Trim();
        if (string.IsNullOrEmpty(tenant) || string.IsNullOrEmpty(audience))
        {
            return null;
        }

        if (!Guid.TryParse(tenant, out _))
        {
            throw new InvalidOperationException("Auth__TenantId must be the tenant's GUID.");
        }

        const string prefix = "api://";
        var clientId = audience.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? audience[prefix.Length..] : audience;
        if (!Guid.TryParse(clientId, out _))
        {
            throw new InvalidOperationException(
                "Auth__Audience must be the app's client id or api://<client-id> (the default App ID URI).");
        }

        return new AuthSettings(tenant, [clientId, prefix + clientId]);
    }
}
