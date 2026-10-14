namespace CampusPulse.Api.Logging;

/// <summary>
/// Request ids: taken from the caller's <c>X-Request-Id</c> when it looks like one, so a client's
/// bug report and the API's log line share an id; otherwise a new one.
/// </summary>
public static class RequestIds
{
    public const string HeaderName = "X-Request-Id";
    public const int MaxLength = 64;

    /// <summary>The caller's id if it is 1–64 characters of letters, digits, <c>.</c>, <c>_</c>, <c>-</c> or <c>:</c>; else null.</summary>
    public static string? FromHeader(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return null;
        }

        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':'))
            {
                return null;
            }
        }

        return value;
    }

    /// <summary>A new id: 32 lowercase hex characters.</summary>
    public static string New() => Guid.NewGuid().ToString("N");
}
