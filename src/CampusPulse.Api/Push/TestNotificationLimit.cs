namespace CampusPulse.Api.Push;

/// <summary>
/// One test notification per student per minute. In memory: a restart forgets it, and with more
/// than one instance of the API each would count on its own. Fine for a test button.
/// </summary>
public sealed class TestNotificationLimit(TimeProvider time)
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private readonly Dictionary<Guid, DateTimeOffset> _lastSent = [];

    /// <summary>Records a send for <paramref name="userId"/> now, or returns false with how long to
    /// wait when their last one was less than <see cref="Window"/> ago.</summary>
    public bool TryStart(Guid userId, out TimeSpan retryAfter)
    {
        var now = time.GetUtcNow();
        lock (_lastSent)
        {
            if (_lastSent.TryGetValue(userId, out var last) && now - last < Window)
            {
                retryAfter = Window - (now - last);
                return false;
            }

            _lastSent[userId] = now;
            if (_lastSent.Count > 1000)
            {
                foreach (var stale in _lastSent.Where(kv => now - kv.Value >= Window).Select(kv => kv.Key).ToList())
                {
                    _lastSent.Remove(stale);
                }
            }
        }

        retryAfter = TimeSpan.Zero;
        return true;
    }
}
