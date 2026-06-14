using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Contracts.Messages;

/// <summary>
/// Point-to-point ping carried by the stress harness's first end-to-end flow.
/// </summary>
/// <remarks>
/// <para>
/// The <c>CorrelationId</c> base-class property carries the flow identifier so the message
/// is correlatable end-to-end without parsing headers. <see cref="Token"/> repeats the same
/// id as a string so the driver can echo-check the payload independently of the broker's
/// header propagation path (catches a class of serialisation regressions where the body
/// arrives empty but the headers route correctly).
/// </para>
/// </remarks>
public sealed class P2pPing(Guid correlationId) : Message(correlationId)
{
    /// <summary>
    /// Flow-identifier discriminator echoed back on the receiver for the driver's payload check.
    /// </summary>
    public string Token { get; init; } = string.Empty;
}
