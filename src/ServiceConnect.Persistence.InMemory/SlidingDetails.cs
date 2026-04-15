namespace ServiceConnect.Persistence.InMemory;

public sealed class SlidingDetails
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

    private DateTimeOffset ExpireAt { get; set; }

    /// <summary>
    /// Returns true if the sliding window has elapsed. When false, <paramref name="tryAfter"/>
    /// gives the caller the remaining time before the next expiry check should run.
    /// </summary>
    public bool CanExpire(out TimeSpan tryAfter)
    {
        tryAfter = ExpireAt - _timeProvider.GetUtcNow();
        return tryAfter.Ticks <= 0;
    }

    /// <summary>
    /// Resets the sliding window so the expiry is <see cref="RelativeExpiry"/> from now.
    /// </summary>
    public void Slide()
    {
        ExpireAt = _timeProvider.GetUtcNow().Add(RelativeExpiry);
    }
}
