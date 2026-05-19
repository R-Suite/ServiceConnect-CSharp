using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Contracts.Messages;

/// <summary>
/// Reply half of the stress harness's request-reply pattern. The handler constructs
/// this with the request's correlation id so <see cref="ServiceConnect.Interfaces.IBus.SendRequestAsync"/>
/// can route the reply back to the originating call.
/// </summary>
public sealed class QuoteResponse(Guid correlationId) : Message(correlationId)
{
    /// <summary>
    /// Quoted price. Fixed at handler-construction so the driver can assert the
    /// reply-content path is intact alongside the correlation-id check.
    /// </summary>
    public decimal Price { get; init; }
}
