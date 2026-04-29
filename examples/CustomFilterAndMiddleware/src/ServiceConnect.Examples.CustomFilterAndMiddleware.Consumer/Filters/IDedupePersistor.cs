namespace ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.Filters;

/// <summary>
/// Sample dedupe persistor contract. The atomic <see cref="TryInsertAsync"/>
/// returns true on first insert, false on duplicate — letting the on-success
/// filter make the consume-side dedup decision in a single round trip.
/// </summary>
public interface IDedupePersistor
{
    Task<bool> ContainsAsync(Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomic insert. Returns true if the id was new, false if it was already present.
    /// </summary>
    Task<bool> TryInsertAsync(Guid messageId, DateTime expiry, CancellationToken cancellationToken = default);
}
