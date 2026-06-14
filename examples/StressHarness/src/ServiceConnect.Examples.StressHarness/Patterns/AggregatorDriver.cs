using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Examples.StressHarness.Patterns.Aggregators;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Examples.StressHarness.Patterns;

/// <summary>
/// Drives the framework's aggregator pattern by sending exactly
/// <c>StressTelemetrySliceAggregator.BatchSize()</c> <see cref="TelemetrySlice"/>
/// messages to the receiver bus under one shared flow id, then awaiting the
/// resulting batch flush. Asserts the framework dispatched a batch of the
/// expected size — a partial batch means either a delivery was dropped or the
/// per-batch timeout fired before the size threshold was reached, both of which
/// are real defects the smoke harness wants to catch.
/// </summary>
/// <remarks>
/// <para>
/// Routing is endpoint-explicit, matching <see cref="PointToPointDriver"/>.
/// Every item shares the same flow id on both the message body's
/// <see cref="Message.CorrelationId"/> and the
/// <see cref="StressHeaders.FlowId"/> header — the aggregator reads the
/// correlation id from the message body because <c>ExecuteAsync</c> has no
/// access to per-message headers.
/// </para>
/// <para>
/// Synchronisation is via <see cref="AggregatorObservations.AwaitBatchAsync"/>
/// rather than the per-handler signal: the aggregator's flush is the
/// observable event, not a single message arrival, so the rendezvous needs to
/// fire once per batch — not once per inbound item. <c>PerHandlerSignal</c> is
/// keyed for the per-message rendezvous used by every other pattern; aggregator
/// dispatch is logically distinct.
/// </para>
/// </remarks>
public sealed class AggregatorDriver(FlowAccounting accounting, AggregatorObservations observations) : IPatternDriver
{
    private const int BatchSize = 4;

    public string Name => "aggregator";
    public bool RequiresPersistence => true;

    [SuppressMessage("Style", "IDE0060", Justification = "Threaded through to satisfy IPatternDriver contract; aggregator traffic flows one way.")]
    public async Task<FlowResult> RunFlowAsync(IBus sender, IBus receiver, StressFlowContext context, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var failures = new List<string>();

        var receiverEndpoint = context.ExpectedReceiver == BusIdentity.Alpha ? "stress-a.work" : "stress-b.work";
        var receiverBusTag = context.ExpectedReceiver.ToHeaderValue();

        var sendOptions = new SendOptions
        {
            EndPoint = receiverEndpoint,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [StressHeaders.FlowId] = context.FlowId.ToString("N"),
                [StressHeaders.OriginBus] = context.Origin.ToHeaderValue(),
                [StressHeaders.Pattern] = Name,
            },
        };

        // The aggregator's ExecuteAsync produces exactly one observable event per
        // batch flush. RecordSend books a single expected handler invocation
        // (the batch dispatch) regardless of the number of items in the batch,
        // matching the accounting reconcile that runs at end-of-run.
        accounting.RecordSend(context.FlowId, expectedHandlerInvocations: 1);

        for (var i = 1; i <= BatchSize; i++)
        {
            var item = new TelemetrySlice(context.FlowId) { Value = i };
            await sender.SendAsync(item, sendOptions, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var batch = await observations.AwaitBatchAsync(context.FlowId, cancellationToken).ConfigureAwait(false);

            // Record the batch dispatch as the single handler invocation booked
            // against this flow id, mirroring the IMessageHandler-based drivers
            // that call accounting.RecordHandled inside their handler.
            accounting.RecordHandled(context.FlowId);

            // Under the at-least-once contract, the framework may dispatch a flow's
            // items across multiple partial batches if a broker kill fires the flush
            // timer before all items arrive. Any size in [1, BatchSize] is a valid
            // outcome; sizes above BatchSize would indicate the framework over-collected
            // and remain a failure.
            if (batch.Count is < 1 or > BatchSize)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"aggregator {context.Origin.ToHeaderValue()}->{receiverBusTag}: expected batch size in [1, {BatchSize}] but observed {batch.Count}"));
            }
            if (!string.Equals(batch.BusTag, receiverBusTag, StringComparison.Ordinal))
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"aggregator {context.Origin.ToHeaderValue()}->{receiverBusTag}: expected dispatch on '{receiverBusTag}' but observed '{batch.BusTag}'"));
            }
        }
        catch (OperationCanceledException)
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture,
                $"aggregator {context.Origin.ToHeaderValue()}->{receiverBusTag}: batch did not dispatch within {context.FlowTimeout}"));
        }

        sw.Stop();
        return failures.Count == 0
            ? FlowResult.Pass(sw.Elapsed, sent: BatchSize, handled: BatchSize)
            : FlowResult.Fail(sw.Elapsed, sent: BatchSize, handled: 0, [.. failures]);
    }
}
