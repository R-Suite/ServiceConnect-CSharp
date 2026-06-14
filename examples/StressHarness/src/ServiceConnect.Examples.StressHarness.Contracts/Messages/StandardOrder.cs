using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Contracts.Messages;

/// <summary>
/// Standard-priority order routed to <c>StandardOrderHandler</c> in the
/// content-based-routing driver. Distinct CLR type from <see cref="PremiumOrder"/>
/// so the framework's type-derived fanout exchange routes each variant to its own
/// handler — the driver's assertion is that the type-specific handlers each fire
/// for the matching publish, with no cross-leakage.
/// </summary>
public sealed class StandardOrder(Guid correlationId) : Message(correlationId)
{
    /// <summary>
    /// Customer identifier echoed end-to-end. The driver sets it to the flow id so
    /// the payload remains correlatable independently of the broker's header path.
    /// </summary>
    public string CustomerId { get; init; } = string.Empty;
}
