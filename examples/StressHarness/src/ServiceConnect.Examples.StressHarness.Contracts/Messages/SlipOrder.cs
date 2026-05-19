using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Contracts.Messages;

/// <summary>
/// Message carried through the stress harness's routing-slip pattern. The driver
/// posts one of these via <see cref="ServiceConnect.Interfaces.IBus.RouteAsync"/>
/// with an ordered destination list; the framework's
/// <c>HandlerProcessor.ForwardRoutingSlipAsync</c> reads the encoded slip from the
/// inbound headers after each hop's handler runs and forwards the message along
/// to the next destination in the slip.
/// </summary>
/// <remarks>
/// The slip itself lives in the message envelope (<c>HeaderKeys.RoutingSlip</c>);
/// this body only carries the order identifier so a per-flow observation log can
/// record the handler arrivals in order without coupling to envelope headers.
/// </remarks>
public sealed class SlipOrder(Guid correlationId) : Message(correlationId)
{
    /// <summary>
    /// Business identifier echoed in the trail observations. Driver sets it to the
    /// flow id so the per-bus arrival log remains correlatable to the orchestrator's
    /// flow accounting independently of the framework's correlation-id header.
    /// </summary>
    public string OrderId { get; init; } = string.Empty;
}
