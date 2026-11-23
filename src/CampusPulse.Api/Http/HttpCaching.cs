using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace CampusPulse.Api.Http;

/// <summary>Who a GET's answer is the same for, and whether it depends on today's date: that decides what a browser may
/// keep.</summary>
public enum HttpCacheScope
{
    /// <summary>The same for every student and not tied to today (a given week, a room's list): a browser may reuse it
    /// for 60 s without asking. <c>private</c> all the same, because it needs sign-in: no shared cache (a CDN, a proxy)
    /// may store it.</summary>
    Shared,

    /// <summary>The same for every student but depending on today's date (the current semester and week, a timetable
    /// that defaults to today): <c>private, no-cache</c>, so a browser asks each time (a 304 when nothing changed) and
    /// never shows yesterday after midnight.</summary>
    SharedToday,

    /// <summary>One student's own data: <c>private, no-store</c>, so it is never written to a browser's disk cache (a
    /// shared lab PC keeps nothing after sign-out). A client that keeps its own copy can still send
    /// <c>If-None-Match</c> and get a 304.</summary>
    PerStudent,
}

/// <summary>
/// Conditional GETs: a weak <c>ETag</c> (a hash of the JSON body) on every 200, and <c>304 Not Modified</c> with
/// no body when the request's <c>If-None-Match</c> has it, so a client that already has the data downloads nothing.
/// The tag is weak because the same JSON may go out Brotli- or gzip-compressed. Responses that don't opt in get
/// <c>Cache-Control: no-store</c> from <see cref="SecurityHeaders"/>.
/// </summary>
public static class HttpCaching
{
    public const string SharedCacheControl = "private, max-age=60";
    public const string SharedTodayCacheControl = "private, no-cache";
    public const string PerStudentCacheControl = "private, no-store";

    public static RouteHandlerBuilder WithHttpCaching(this RouteHandlerBuilder builder, HttpCacheScope scope) =>
        builder.WithHttpCaching(_ => scope);

    /// <summary>Conditional GETs whose scope depends on the request (a timetable that defaults to today when it has no
    /// dates).</summary>
    public static RouteHandlerBuilder WithHttpCaching(this RouteHandlerBuilder builder, Func<HttpRequest, HttpCacheScope> scopeOf) =>
        builder.AddEndpointFilter(new ConditionalGetFilter(scopeOf)).Produces(StatusCodes.Status304NotModified);

    /// <summary>The weak entity tag of a JSON body: 128 bits of its SHA-256.</summary>
    public static EntityTagHeaderValue ETagOf(ReadOnlySpan<byte> body) =>
        new($"\"{Convert.ToHexStringLower(SHA256.HashData(body).AsSpan(0, 16))}\"", isWeak: true);

    private static string CacheControl(HttpCacheScope scope) => scope switch
    {
        HttpCacheScope.Shared => SharedCacheControl,
        HttpCacheScope.SharedToday => SharedTodayCacheControl,
        _ => PerStudentCacheControl,
    };

    private sealed class ConditionalGetFilter(Func<HttpRequest, HttpCacheScope> scopeOf) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var result = await next(context);
            var http = context.HttpContext;
            var inner = result is INestedHttpResult nested ? nested.Result : result;
            if (!HttpMethods.IsGet(http.Request.Method)
                || inner is not IStatusCodeHttpResult { StatusCode: StatusCodes.Status200OK }
                || inner is not IValueHttpResult { Value: { } value })
            {
                return result;
            }

            // Serialized here, exactly as Ok<T> would write it (its declared type, the app's JSON options), so the tag
            // is of the bytes that go out.
            var declared = inner.GetType() is { IsGenericType: true } type ? type.GetGenericArguments()[0] : value.GetType();
            var json = http.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
            var body = JsonSerializer.SerializeToUtf8Bytes(value, json.GetTypeInfo(declared));
            var etag = ETagOf(body);

            http.Response.GetTypedHeaders().ETag = etag;
            http.Response.Headers.CacheControl = CacheControl(scopeOf(http.Request));
            var ifNoneMatch = http.Request.GetTypedHeaders().IfNoneMatch;
            if (ifNoneMatch.Any(candidate => candidate.Equals(EntityTagHeaderValue.Any) || candidate.Compare(etag, useStrongComparison: false)))
            {
                return TypedResults.StatusCode(StatusCodes.Status304NotModified);
            }

            return new JsonBody(body);
        }
    }

    /// <summary>A 200 whose JSON is already serialized.</summary>
    private sealed class JsonBody(byte[] body) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            httpContext.Response.ContentType = "application/json; charset=utf-8";
            httpContext.Response.ContentLength = body.Length;
            return httpContext.Response.Body.WriteAsync(body).AsTask();
        }
    }
}
