using System.Text.RegularExpressions;

namespace CampusPulse.Domain.Push;

/// <summary>Which push service a device token belongs to. Expo hides the difference when sending;
/// it's kept for statistics and for a later switch to sending through FCM/APNs directly.</summary>
public enum PushPlatform
{
    Android,
    Ios,
}

/// <summary>
/// A phone that receives the student's notifications, identified by its Expo push token. A token
/// belongs to one install of the app, so it is unique across students: when another student signs
/// in on the same phone and registers it, the device moves to them.
/// </summary>
public sealed partial class PushDevice
{
    /// <summary>Expo tokens are about 40 characters; the limit only keeps junk out of the column.</summary>
    public const int TokenMaxLength = 200;

    /// <summary>A student's devices beyond this many drop the least recently registered.</summary>
    public const int MaxPerUser = 10;

    private PushDevice()
    {
    }

    public PushDevice(Guid userId, string token, PushPlatform platform, DateTimeOffset now)
    {
        if (!IsExpoToken(token))
        {
            throw new ArgumentException("Not an Expo push token.", nameof(token));
        }

        Id = Guid.CreateVersion7(now);
        UserId = userId;
        Token = token;
        Platform = platform;
        CreatedAt = now;
        LastRegisteredAt = now;
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string Token { get; private set; } = "";

    public PushPlatform Platform { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset LastRegisteredAt { get; private set; }

    /// <summary>The app registered this token again, possibly for another student (who signed in on
    /// this phone): it belongs to <paramref name="userId"/> from now on.</summary>
    public void Register(Guid userId, PushPlatform platform, DateTimeOffset now)
    {
        UserId = userId;
        Platform = platform;
        LastRegisteredAt = now;
    }

    /// <summary><c>ExponentPushToken[…]</c> or <c>ExpoPushToken[…]</c>, letters, digits, <c>_</c> and
    /// <c>-</c> inside. Anchored with <c>\z</c>, not <c>$</c>, which would also accept a trailing newline.</summary>
    public static bool IsExpoToken(string? token) =>
        token is { Length: > 0 and <= TokenMaxLength } && ExpoTokenPattern().IsMatch(token);

    [GeneratedRegex(@"^Expo(nent)?PushToken\[[A-Za-z0-9_-]+\]\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExpoTokenPattern();
}
