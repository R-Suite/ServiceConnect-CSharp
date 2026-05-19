using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Examples.StressHarness.Patterns;

/// <summary>
/// Drives the publish-subscribe fan-out across the bus pair. One direction per
/// <see cref="RunFlowAsync"/> invocation; <see cref="Orchestrator.FlowRunner"/>
/// schedules α→β and β→α concurrently against the same driver instance.
/// </summary>
/// <remarks>
/// <para>
/// Pub/sub uses a type-derived fanout exchange shared by both buses, so a publish from
/// one bus is delivered to both subscriber queues. <see cref="PubSubHandler"/> suppresses
/// the echo to the publishing bus by matching <c>OriginBus</c> against its own bus tag,
/// leaving exactly one cross-tenant invocation per flow — which matches the driver's
/// <c>expectedHandlerInvocations: 1</c> bookkeeping.
/// </para>
/// <para>
/// <see cref="PublishOptions"/> carries no <c>EndPoint</c>: routing is by exchange-binding
/// rather than queue name. The <c>receiver</c> bus parameter is required by the
/// <see cref="IPatternDriver"/> contract but unused here — the driver only touches the
/// sender side. Other patterns (request-reply, saga-reply-from-handler) need the
/// receiver handle, so the interface keeps both.
/// </para>
/// </remarks>
public sealed class PublishSubscribeDriver(FlowAccounting accounting, PerHandlerSignal signals) : IPatternDriver
{
    public string Name => "pubsub";
    public bool RequiresPersistence => false;

    [SuppressMessage("Style", "IDE0060", Justification = "Threaded through to satisfy IPatternDriver contract; other patterns use the receiver handle.")]
    public async Task<FlowResult> RunFlowAsync(IBus sender, IBus receiver, StressFlowContext context, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var failures = new List<string>();

        var message = new PubSubEvent(context.FlowId) { Topic = context.FlowId.ToString("N") };
        var publishOptions = new PublishOptions
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [StressHeaders.FlowId] = context.FlowId.ToString("N"),
                [StressHeaders.OriginBus] = context.Origin.ToHeaderValue(),
                [StressHeaders.Pattern] = Name,
            },
        };

        // Record the send before the await so the accounting layer cannot observe a
        // handler signal whose corresponding send hasn't been booked yet (would surface
        // as a spurious "handled without record of send" failure).
        accounting.RecordSend(context.FlowId, expectedHandlerInvocations: 1);
        await sender.PublishAsync(message, publishOptions, cancellationToken).ConfigureAwait(false);

        try
        {
            var invocation = await signals.AwaitAsync(context.FlowId, cancellationToken).ConfigureAwait(false);
            var crossCheck = CrossTenantAssertions.Check(invocation.Context, context.ExpectedReceiver, invocation.BusTag);
            if (!crossCheck.Ok)
            {
                failures.Add(crossCheck.Failure);
            }
        }
        catch (OperationCanceledException)
        {
            failures.Add($"pubsub {context.Origin.ToHeaderValue()}->{context.ExpectedReceiver.ToHeaderValue()}: handler did not fire within {context.FlowTimeout}");
        }

        sw.Stop();
        return failures.Count == 0
            ? FlowResult.Pass(sw.Elapsed, sent: 1, handled: 1)
            : FlowResult.Fail(sw.Elapsed, sent: 1, handled: 0, [.. failures]);
    }
}
