namespace ServiceConnect.Interfaces;

/// <summary>
/// A correlation-id carrier. Implemented by <see cref="Message"/> and by any
/// aggregator data type that stores a per-message correlation key.
/// </summary>
/// <remarks>
/// Replaces the v7 reflection-based discovery of a public <c>Guid CorrelationId</c>
/// property. Aggregator persistors require this interface on stored data so they
/// can locate entries by correlation id without per-type reflection.
/// </remarks>
public interface IHasCorrelationId
{
    /// <summary>The correlation identifier carried by the implementing instance.</summary>
    Guid CorrelationId { get; }
}
