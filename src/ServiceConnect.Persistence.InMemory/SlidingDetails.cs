namespace ServiceConnect.Persistence.InMemory;

public sealed class SlidingDetails
{
    /// <summary>
    /// Initializes a new <see cref="SlidingDetails"/> with a sliding expiry window.
    /// </summary>
    public SlidingDetails(TimeSpan relativeExpiry)
    {
        RelativeExpiry = relativeExpiry;
        Slide();
    }

    private TimeSpan RelativeExpiry { get; set; }

    private DateTime ExpireAt { get; set; }

    /// <summary>
    /// Returns true if the sliding window has elapsed. When false, <paramref name="tryAfter"/>
    /// gives the caller the remaining time before the next expiry check should run.
    /// </summary>
    public bool CanExpire(out TimeSpan tryAfter)
    {
        tryAfter = ExpireAt - DateTime.UtcNow;
        return 0 > tryAfter.Ticks;
    }

    /// <summary>
    /// Resets the sliding window so the expiry is <see cref="RelativeExpiry"/> from now.
    /// </summary>
    public void Slide()
    {
        ExpireAt = DateTime.UtcNow.Add(RelativeExpiry);
    }
}
