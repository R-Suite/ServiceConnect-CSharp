using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Contracts.Messages;

/// <summary>
/// Drives the telemetry pattern. Published on the sender bus with
/// <see cref="AddTelemetry"/> wired into both buses' pipelines so the framework
/// emits a publish-side Producer activity and a consume-side Consumer activity
/// for each flow. An in-process <c>ActivityListener</c> captures every span the
/// framework's <c>ServiceConnectActivitySource</c> emits; the driver asserts at
/// least one activity carrying this flow's correlation id was recorded.
/// </summary>
/// <remarks>
/// <see cref="Topic"/> echoes the flow id as a string so the driver can
/// payload-check alongside the activity assertion (catches a class of
/// serialisation regressions where the body arrives empty but the spans still
/// emit because the headers route correctly).
/// </remarks>
public sealed class TracedEvent(Guid correlationId) : Message(correlationId)
{
    /// <summary>
    /// Logical topic label echoed end-to-end. The driver sets it to the flow id so the
    /// payload check is independent of the broker's header-propagation path.
    /// </summary>
    public string Topic { get; init; } = string.Empty;
}
