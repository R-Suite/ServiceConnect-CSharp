namespace ServiceConnect.Examples.StressHarness.Contracts.Messages;

/// <summary>
/// Concrete <see cref="DomainEvent"/> published by the polymorphic-messages driver's
/// second send. Distinct CLR type so RabbitMQ routes through a type-derived exchange of
/// its own — the bus binds the receiver queue to both this exchange and
/// <see cref="OrderPlacedEvent"/>'s exchange, but a single base-type handler catches
/// both deliveries.
/// </summary>
public sealed class OrderShippedEvent(Guid correlationId) : DomainEvent(correlationId)
{
    /// <summary>
    /// Shipping identifier carried end-to-end. The driver sets it to the flow id so the
    /// payload remains correlatable independently of the broker's header propagation
    /// path.
    /// </summary>
    public string ShippingId { get; init; } = string.Empty;
}
