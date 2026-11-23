using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace CampusPulse.Api.Http;

/// <summary>Who a GET's answer is the same for, which decides how long a browser may keep it.</summary>
public enum HttpCacheScope
{
    /// <summary>The same for every student (the timetable): a browser may reuse it for 60 s without asking.
    /// <c>private</c> all the same, because it needs sign-in: no shared cache (a CDN, a proxy) may store it.</summary>
    Shared,

    /// <summary>One student's own data: <c>private, no-cache</c>, so a browser asks again every time (getting a 304
    /// when nothing changed) and never shows a change late.</summary>
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
    public const string PerStudentCacheControl = "private, no-cache";

    public static RouteHandlerBuilder WithHttpCaching(this RouteHandlerBuilder builder, HttpCacheScope scope) =>
        builder.AddEndpointFilter(new ConditionalGetFilter(scope)).Produces(StatusCodes.Status304NotModified);

    /// <summary>The weak entity tag of a JSON body: 128 bits of its SHA-256.</summary>
    public static EntityTagHeaderValue ETagOf(ReadOnlySpan<byte> body) =>
        new($"\"{Convert.ToHexStringLower(SHA256.HashData(body).AsSpan(0, 16))}\"", isWeak: true);

    private sealed class ConditionalGetFilter(HttpCacheScope scope) : IEndpointFilter
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
            http.Response.Headers.CacheControl = scope == HttpCacheScope.Shared ? SharedCacheControl : PerStudentCacheControl;
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
