using Microsoft.Net.Http.Headers;

namespace CampusPulse.Api.Http;

/// <summary>
/// Headers on every response, errors included, that keep a browser from doing anything with an API answer but read
/// it: no MIME sniffing (<c>nosniff</c>), never framed (<c>X-Frame-Options: DENY</c>, CSP <c>frame-ancestors 'none'</c>),
/// no scripts, styles or anything else loaded or run if one is opened as a page (CSP <c>default-src 'none'</c>), no
/// <c>Referer</c> sent from it, and nothing stored by any cache (<c>Cache-Control: no-store</c>) unless the endpoint
/// set its own (<see cref="HttpCaching"/>). HSTS is added by <c>UseHsts</c> on HTTPS requests.
/// </summary>
public sealed class SecurityHeaders(RequestDelegate next)
{
    public const string ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var headers = ((HttpResponse)state).Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers["Referrer-Policy"] = "no-referrer";
            if (!headers.ContainsKey(HeaderNames.CacheControl))
            {
                headers.CacheControl = "no-store";
            }

            return Task.CompletedTask;
        }, context.Response);
        return next(context);
    }
}
