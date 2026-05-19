using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Contracts.Messages;

/// <summary>
/// Drives the filter-ordering pattern. A <see cref="BeforeConsumingFilters"/>-stage filter
/// records its own execution into a shared trail before the message reaches the matching
/// <c>IMessageHandler</c>, which then records its own marker; the driver asserts the trail
/// contains the filter entry strictly before the handler entry.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Token"/> echoes the flow id as a string so the driver can payload-check
/// alongside the trail-ordering assertion (catches a class of serialisation regressions
/// where the body arrives empty but the headers and filter pipeline still produce a
/// well-formed trail).
/// </para>
/// </remarks>
public sealed class FilteredMessage(Guid correlationId) : Message(correlationId)
{
    /// <summary>
    /// Flow-identifier discriminator echoed back on the receiver for the driver's payload check.
    /// </summary>
    public string Token { get; init; } = string.Empty;
}
