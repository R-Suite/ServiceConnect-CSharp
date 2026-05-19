using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Contracts.Messages;

/// <summary>
/// Request half of the stress harness's request-reply pattern. The driver awaits the
/// matching <see cref="QuoteResponse"/> via <see cref="ServiceConnect.Interfaces.IBus.SendRequestAsync"/>
/// rather than the per-handler signal, so the reply itself is the synchronisation point.
/// </summary>
public sealed class QuoteRequest(Guid correlationId) : Message(correlationId)
{
    /// <summary>
    /// Product identifier echoed back in the response. Driver sets it to the flow id so
    /// the payload remains correlatable end-to-end independently of the request-reply
    /// header propagation path.
    /// </summary>
    public string ProductId { get; init; } = string.Empty;
}
