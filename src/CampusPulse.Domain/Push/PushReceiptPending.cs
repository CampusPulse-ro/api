namespace CampusPulse.Domain.Push;

/// <summary>
/// A notification Expo accepted (an "ok" ticket) whose delivery receipt hasn't been read yet. Expo
/// reports there, some minutes later, whether the phone's push service took it; a
/// <c>DeviceNotRegistered</c> receipt means the app was uninstalled and the device is deleted.
/// </summary>
public sealed class PushReceiptPending
{
    public const int TicketIdMaxLength = 100;

    private PushReceiptPending()
    {
    }

    public PushReceiptPending(string ticketId, Guid deviceId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(ticketId) || ticketId.Length > TicketIdMaxLength)
        {
            throw new ArgumentException($"A ticket id of 1-{TicketIdMaxLength} characters is required.", nameof(ticketId));
        }

        TicketId = ticketId;
        DeviceId = deviceId;
        CreatedAt = now;
    }

    public string TicketId { get; private set; } = "";

    public Guid DeviceId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
