using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Contracts.Messages;

/// <summary>
/// Pub/sub fan-out event carried by the stress harness's publish-subscribe flow.
/// </summary>
/// <remarks>
/// Both buses bind a queue to the shared type-derived fanout exchange, so every
/// <see cref="ServiceConnect.Interfaces.IBus.PublishAsync"/> from one bus is delivered
/// to both subscribers. The driver records exactly one expected invocation and the
/// handler suppresses the echo to its own bus by comparing the <c>OriginBus</c> header
/// to the bus tag stamped at handler construction.
/// </remarks>
public sealed class PubSubEvent(Guid correlationId) : Message(correlationId)
{
    /// <summary>
    /// Logical topic label echoed end-to-end. The driver sets it to the flow id so the
    /// payload check is independent of the broker's header-propagation path.
    /// </summary>
    public string Topic { get; init; } = string.Empty;
}
