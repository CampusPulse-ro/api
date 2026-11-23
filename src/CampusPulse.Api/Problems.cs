using Microsoft.AspNetCore.Http.HttpResults;

namespace CampusPulse.Api;

/// <summary>ProblemDetails with a stable <c>type</c> a client can switch on (<c>invalid-code</c>, <c>identity-in-use</c>,
/// …): the type is the contract, the title and detail are for people.</summary>
public static class Problems
{
    public static ProblemHttpResult Of(int status, string type, string title, string? detail = null) =>
        TypedResults.Problem(statusCode: status, type: type, title: title, detail: detail);
}
