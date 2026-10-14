namespace CampusPulse.Api.Push;

/// <summary>
/// One test notification per student per minute. In memory: a restart forgets it, and with more
/// than one instance of the API each would count on its own. Fine for a test button. Entries older
/// than the window are dropped on every call, so nothing about a student outlives it here (a
/// deleted account's id included).
/// </summary>
public sealed class TestNotificationLimit(TimeProvider time)
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private readonly Dictionary<Guid, DateTimeOffset> _lastSent = [];

    /// <summary>Takes <paramref name="userId"/>'s slot now, or returns false with how long to wait when
    /// it was taken less than <see cref="Window"/> ago. A send that never reached the push service
    /// gives it back with <see cref="Release"/>.</summary>
    public bool TryStart(Guid userId, out TimeSpan retryAfter)
    {
        var now = time.GetUtcNow();
        lock (_lastSent)
        {
            foreach (var stale in _lastSent.Where(kv => now - kv.Value >= Window).Select(kv => kv.Key).ToList())
            {
                _lastSent.Remove(stale);
            }

            if (_lastSent.TryGetValue(userId, out var last))
            {
                retryAfter = Window - (now - last);
                return false;
            }

            _lastSent[userId] = now;
        }

        retryAfter = TimeSpan.Zero;
        return true;
    }

    /// <summary>Gives the slot back: the send reached no push service (no devices, or it was down).</summary>
    public void Release(Guid userId)
    {
        lock (_lastSent)
        {
            _lastSent.Remove(userId);
        }
    }

    /// <summary>Students currently holding a slot (for tests).</summary>
    public int Count
    {
        get
        {
            lock (_lastSent)
            {
                return _lastSent.Count;
            }
        }
    }
}
