using System.Diagnostics;
using System.Globalization;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Examples.StressHarness.Patterns;

/// <summary>
/// Drives a polymorphic-publish flow by publishing one <see cref="OrderPlacedEvent"/>
/// and one <see cref="OrderShippedEvent"/> per direction under a shared flow id, then
/// asserting that a single <see cref="Handlers.DomainEventHandler"/> registration
/// caught both deliveries via the dispatcher's base-type walk. Each concrete event
/// publishes through its own type-derived fanout exchange; the bus binds the receiver
/// queue to both exchanges via dedicated <c>HandlerReference</c> entries.
/// </summary>
/// <remarks>
/// <para>
/// The driver reuses the orchestrator-supplied <see cref="StressFlowContext.FlowId"/>
/// across both publishes so the two arrivals on the receiver record against the same
/// accounting bucket. <c>expectedHandlerInvocations: 2</c> matches the expected pair
/// of cross-tenant deliveries; the publisher-bus echoes are suppressed by the handler's
/// <c>OriginBus</c> check, so they don't inflate the observed count.
/// </para>
/// <para>
/// Synchronisation is via <see cref="FlowAccounting.Reconcile"/> polling rather than
/// <see cref="PerHandlerSignal"/>, because the signal's task completion source is
/// one-shot — it fires on the first handler invocation and the second is silently
/// dropped. Polling waits until the flow id's observed count catches up with the
/// expected pair, then the cross-tenant assertion runs against the (first) signal
/// snapshot for receiver-bus verification.
/// </para>
/// </remarks>
public sealed class PolymorphicMessagesDriver(FlowAccounting accounting, PerHandlerSignal signals) : IPatternDriver
{
    public string Name => "polymorphic";
    public bool RequiresPersistence => false;

    public async Task<FlowResult> RunFlowAsync(IBus sender, IBus receiver, StressFlowContext context, CancellationToken cancellationToken)
    {
        _ = receiver;
        var sw = Stopwatch.StartNew();
        var failures = new List<string>();

        var publishOptions = new PublishOptions
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [StressHeaders.FlowId] = context.FlowId.ToString("N"),
                [StressHeaders.OriginBus] = context.Origin.ToHeaderValue(),
                [StressHeaders.Pattern] = Name,
            },
        };

        // Two derived events under the same flow id ⇒ two expected handler invocations
        // on the receiver bus. The handler suppresses the echo to the publishing bus,
        // so this bookkeeping does not need to account for the local-bus copies.
        accounting.RecordSend(context.FlowId, expectedHandlerInvocations: 2);

        var placedMessage = new OrderPlacedEvent(context.FlowId) { OrderId = context.FlowId.ToString("N") };
        var shippedMessage = new OrderShippedEvent(context.FlowId) { ShippingId = context.FlowId.ToString("N") };

        await Task.WhenAll(
            sender.PublishAsync(placedMessage, publishOptions, cancellationToken),
            sender.PublishAsync(shippedMessage, publishOptions, cancellationToken)).ConfigureAwait(false);

        // Take the first signal as the cross-tenant snapshot in parallel with the drain
        // poll. AwaitAsync's TaskCompletionSource is one-shot — the second handler
        // invocation under the same flow id is a no-op TrySetResult — so the snapshot
        // we capture is the first arrival's headers / busTag, which is sufficient for
        // the receiver-side assertion.
        var signalTask = signals.AwaitAsync(context.FlowId, cancellationToken);

        var drainPollInterval = TimeSpan.FromMilliseconds(25);
        var drained = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var summary = accounting.Reconcile();
            if (!summary.MissingFlows.Contains(context.FlowId))
            {
                drained = true;
                break;
            }
            await Task.Delay(drainPollInterval, cancellationToken).ConfigureAwait(false);
        }

        if (!drained)
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture,
                $"polymorphic {context.Origin.ToHeaderValue()}->{context.ExpectedReceiver.ToHeaderValue()}: both handlers did not fire within {context.FlowTimeout}"));
        }
        else
        {
            try
            {
                var invocation = await signalTask.ConfigureAwait(false);
                var crossCheck = CrossTenantAssertions.Check(invocation.Headers, context.ExpectedReceiver, invocation.BusTag);
                if (!crossCheck.Ok)
                {
                    failures.Add(crossCheck.Failure);
                }
            }
            catch (OperationCanceledException)
            {
                failures.Add($"polymorphic {context.Origin.ToHeaderValue()}->{context.ExpectedReceiver.ToHeaderValue()}: signal task cancelled before arrival");
            }
        }

        sw.Stop();
        return failures.Count == 0
            ? FlowResult.Pass(sw.Elapsed, sent: 2, handled: 2)
            : FlowResult.Fail(sw.Elapsed, sent: 2, handled: 0, [.. failures]);
    }
}
