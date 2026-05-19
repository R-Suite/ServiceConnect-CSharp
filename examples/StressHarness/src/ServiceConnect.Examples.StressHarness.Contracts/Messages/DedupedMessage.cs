using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Contracts.Messages;

/// <summary>
/// Drives the full inbound pipeline-ordering pattern. A
/// <c>BeforeConsumingFilters</c>-stage filter, an <see cref="IMessageProcessingMiddleware"/>
/// wrapping the dispatch, the matching <c>IMessageHandler</c>, and an
/// <c>OnConsumedSuccessfullyFilters</c>-stage filter each append a marker into a
/// shared trail. The driver asserts the trail is
/// <c>[before, mid-enter, handler, mid-exit, on-success]</c>.
/// </summary>
/// <remarks>
/// <see cref="Token"/> echoes the flow id as a string so the driver can payload-check
/// alongside the trail-ordering assertion (catches a class of serialisation regressions
/// where the body arrives empty but the headers and pipeline still produce a
/// well-formed trail).
/// </remarks>
public sealed class DedupedMessage(Guid correlationId) : Message(correlationId)
{
    /// <summary>
    /// Flow-identifier discriminator echoed back on the receiver for the driver's payload check.
    /// </summary>
    public string Token { get; init; } = string.Empty;
}
