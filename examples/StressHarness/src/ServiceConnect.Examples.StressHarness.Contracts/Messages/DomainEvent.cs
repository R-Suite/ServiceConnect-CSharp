using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Contracts.Messages;

/// <summary>
/// Abstract base for the polymorphic-messages driver's event hierarchy. The driver
/// publishes concrete derived events (<see cref="OrderPlacedEvent"/> /
/// <see cref="OrderShippedEvent"/>) and a single
/// <c>IMessageHandler&lt;DomainEvent&gt;</c> registration catches both via the
/// dispatcher's base-type walk.
/// </summary>
public abstract class DomainEvent(Guid correlationId) : Message(correlationId);
