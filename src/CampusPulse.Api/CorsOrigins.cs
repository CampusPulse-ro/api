namespace CampusPulse.Api;

/// <summary>
/// Which browser origins may call the API. The list comes from configuration
/// (<c>Cors:AllowedOrigins</c>, set per environment in the env file as
/// <c>Cors__AllowedOrigins__0=…</c>). An entry is an exact origin
/// (<c>https://campuspulse.thorsp.net</c>) or a subdomain wildcard
/// (<c>https://*.campuspulse-web.pages.dev</c>, for Cloudflare Pages PR previews). With no
/// entries, no browser origin is allowed.
/// </summary>
public static class CorsOrigins
{
    private const string WildcardPrefix = "*.";

    public static bool IsAllowed(string origin, IReadOnlyCollection<string> allowed)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var candidate))
        {
            return false;
        }

        foreach (var entry in allowed)
        {
            if (Matches(candidate, entry.Trim()))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Matches(Uri candidate, string entry)
    {
        var schemeEnd = entry.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            return false;
        }

        var scheme = entry[..schemeEnd];
        var hostAndPort = entry[(schemeEnd + 3)..].TrimEnd('/');
        if (!string.Equals(candidate.Scheme, scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!hostAndPort.StartsWith(WildcardPrefix, StringComparison.Ordinal))
        {
            return Uri.TryCreate($"{scheme}://{hostAndPort}", UriKind.Absolute, out var exact)
                && string.Equals(candidate.Host, exact.Host, StringComparison.OrdinalIgnoreCase)
                && candidate.Port == exact.Port;
        }

        // "*.example.com" matches "a.example.com" and "a.b.example.com", never "example.com"
        // itself or a look-alike such as "evil-example.com".
        var suffix = hostAndPort[1..]; // ".example.com"
        return candidate.IsDefaultPort
            && candidate.Host.Length > suffix.Length
            && candidate.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }
}
