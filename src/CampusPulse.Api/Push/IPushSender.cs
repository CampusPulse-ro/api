namespace CampusPulse.Api.Push;

/// <summary>
/// Sends notifications to device tokens and reads their delivery receipts. Today that's Expo's push
/// service (<see cref="ExpoPushSender"/>); sending through FCM/APNs directly would be another
/// implementation of this.
/// </summary>
public interface IPushSender
{
    /// <summary>One ticket per message, in the same order. A request that fails gives its messages
    /// a <see cref="PushErrors.RequestFailed"/>, <see cref="PushErrors.TimedOut"/> or
    /// <see cref="PushErrors.MalformedResponse"/> ticket instead of throwing.</summary>
    Task<IReadOnlyList<PushTicket>> SendAsync(IReadOnlyList<PushMessage> messages, CancellationToken cancellationToken);

    /// <summary>The receipts that are ready, by ticket id; a ticket that isn't in the result isn't
    /// ready yet. Throws <see cref="PushServiceException"/> when the service can't answer.</summary>
    Task<IReadOnlyDictionary<string, PushReceipt>> GetReceiptsAsync(IReadOnlyList<string> ticketIds, CancellationToken cancellationToken);
}

/// <summary>A notification for one device token. Optionally: <see cref="Data"/> for the app (Expo's <c>data</c>, about
/// 4 KiB at most), <see cref="HighPriority"/> for something due soon (delivered at once, waking a sleeping Android phone;
/// otherwise Android may hold it back to save battery), and <see cref="TimeToLive"/>, how long it's worth delivering to
/// a phone that is offline (otherwise the push services keep it up to four weeks).</summary>
public sealed record PushMessage(string To, string Title, string Body)
{
    public IReadOnlyDictionary<string, string>? Data { get; init; }

    public bool HighPriority { get; init; }

    public TimeSpan? TimeToLive { get; init; }
}

/// <summary>Accepted (<see cref="Id"/> is the ticket to read the receipt of later), or refused with
/// an error code: Expo's (<c>DeviceNotRegistered</c>, <c>MessageTooBig</c>…) or one of
/// <see cref="PushErrors"/>.</summary>
public sealed record PushTicket(string? Id, string? Error)
{
    public bool Ok => Id is not null;
}

/// <summary>Delivered to the phone's push service, or not, with an error code like a ticket's.</summary>
public sealed record PushReceipt(string? Error)
{
    public bool Ok => Error is null;
}

public static class PushErrors
{
    /// <summary>The app was uninstalled or the token is no longer valid: delete the device.</summary>
    public const string DeviceNotRegistered = "DeviceNotRegistered";

    /// <summary>The push service couldn't be reached, or answered with an error status or request-level errors:
    /// it took none of the messages.</summary>
    public const string RequestFailed = "RequestFailed";

    /// <summary>No answer in time: the push service may or may not have taken the messages.</summary>
    public const string TimedOut = "TimedOut";

    /// <summary>The push service answered something this code can't read: it may have taken the messages.</summary>
    public const string MalformedResponse = "MalformedResponse";

    /// <summary>An error without a code we could read.</summary>
    public const string Unknown = "Unknown";
}

public sealed class PushServiceException(string message) : Exception(message);
