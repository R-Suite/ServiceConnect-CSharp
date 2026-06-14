namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Tracks the current expiry window for a sliding-expiration cache entry.
/// </summary>
internal sealed class SlidingDetails
{
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new <see cref="SlidingDetails"/> with a sliding expiry window.
    /// </summary>
    public SlidingDetails(TimeSpan relativeExpiry, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        RelativeExpiry = relativeExpiry;
        Slide();
    }

    private TimeSpan RelativeExpiry { get; set; }

    // Stored as UTC ticks so Volatile.Read/Write provide atomic 64-bit access.
    // DateTimeOffset is 16 bytes and can tear on weak memory models.
    private long _expireAtUtcTicks;

    /// <summary>
    /// Returns true if the sliding window has elapsed. When false, <paramref name="tryAfter"/>
    /// gives the caller the remaining time before the next expiry check should run.
    /// </summary>
    public bool CanExpire(out TimeSpan tryAfter)
    {
        var expireTicks = Volatile.Read(ref _expireAtUtcTicks);
        var nowTicks = _timeProvider.GetUtcNow().UtcTicks;
        tryAfter = TimeSpan.FromTicks(expireTicks - nowTicks);
        return tryAfter.Ticks <= 0;
    }

    /// <summary>
    /// Resets the sliding window so the expiry is <see cref="RelativeExpiry"/> from now.
    /// </summary>
    public void Slide()
    {
        var newTicks = _timeProvider.GetUtcNow().Add(RelativeExpiry).UtcTicks;
        Volatile.Write(ref _expireAtUtcTicks, newTicks);
    }
}
