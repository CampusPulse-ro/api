using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace CampusPulse.Api.Http;

/// <summary>
/// The API runs behind nginx on 127.0.0.1, so every connection comes from nginx. nginx appends the client's address
/// to <c>X-Forwarded-For</c> and sets <c>X-Forwarded-Proto</c>; this takes those two headers as the request's client
/// address and scheme, but only from a loopback proxy and only its last hop (the address nginx itself saw). A header
/// from anywhere else, or extra addresses a client put in front, are ignored, so a client can't choose the address
/// the rate limits count it under.
/// </summary>
public static class ProxyHeaders
{
    public static IServiceCollection AddCampusPulseProxyHeaders(this IServiceCollection services) =>
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            options.KnownProxies.Add(IPAddress.Loopback);
            options.KnownProxies.Add(IPAddress.IPv6Loopback);
        });
}
