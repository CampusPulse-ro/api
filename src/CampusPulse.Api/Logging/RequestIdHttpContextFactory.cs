using Microsoft.AspNetCore.Http.Features;

namespace CampusPulse.Api.Logging;

/// <summary>
/// Sets each request's <c>TraceIdentifier</c> to its request id (<see cref="RequestIds"/>) as the
/// context is created. Hosting opens its log scope right after this, and that scope's <c>RequestId</c>
/// is the TraceIdentifier, so every line logged during the request carries this one id (rather than
/// Kestrel's connection-based one plus a second scope of ours).
/// </summary>
/// <remarks>
/// Hosting reuses pooled contexts only with the default factory; wrapping it costs one context
/// allocation per request, which is nothing at this API's traffic.
/// </remarks>
public sealed class RequestIdHttpContextFactory(IHttpContextFactory inner) : IHttpContextFactory
{
    public HttpContext Create(IFeatureCollection featureCollection)
    {
        var context = inner.Create(featureCollection);
        context.TraceIdentifier = RequestIds.FromHeader(context.Request.Headers[RequestIds.HeaderName]) ?? RequestIds.New();
        return context;
    }

    public void Dispose(HttpContext httpContext) => inner.Dispose(httpContext);
}
