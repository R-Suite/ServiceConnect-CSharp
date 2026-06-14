using System.Diagnostics;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Examples.StressHarness.Patterns;

/// <summary>
/// Drives a content-based-routing flow by publishing one <see cref="PremiumOrder"/> and
/// one <see cref="StandardOrder"/> per direction, then awaiting that each derived type's
/// own handler fires exactly once on the receiver bus. Verifies that the dispatcher
/// routes by concrete message type rather than by a shared base type — a regression in
/// type-derived exchange binding would cause both messages to route to one handler (or
/// neither), and the per-flow-id rendezvous would time out.
/// </summary>
/// <remarks>
/// <para>
/// Each publish gets its own flow id (independent of the orchestrator-supplied
/// <see cref="StressFlowContext.FlowId"/>) so the two rendezvous can be tracked
/// separately and accounting can reconcile per-message rather than per-direction. The
/// two awaits run under <see cref="Task.WhenAll(Task[])"/> so the driver elapses with
/// the slower of the two arrivals rather than serialising them.
/// </para>
/// <para>
/// Pub/sub uses a type-derived fanout exchange shared by both buses, so a publish from
/// one bus is delivered to BOTH subscriber queues. The PremiumOrder / StandardOrder
/// handlers each suppress the echo to the publishing bus, leaving exactly one
/// cross-tenant invocation per flow id.
/// </para>
/// </remarks>
public sealed class ContentBasedRoutingDriver(FlowAccounting accounting, PerHandlerSignal signals) : IPatternDriver
{
    public string Name => "content-based-routing";
    public bool RequiresPersistence => false;

    public async Task<FlowResult> RunFlowAsync(IBus sender, IBus receiver, StressFlowContext context, CancellationToken cancellationToken)
    {
        _ = receiver;
        var sw = Stopwatch.StartNew();
        var failures = new List<string>();

        var premiumFlowId = Guid.NewGuid();
        var standardFlowId = Guid.NewGuid();

        var premiumOptions = BuildPublishOptions(premiumFlowId, context.Origin);
        var standardOptions = BuildPublishOptions(standardFlowId, context.Origin);

        // Bookings happen before the awaits so a fast handler signal can't observe a
        // flow id whose corresponding send hasn't been recorded (would surface as a
        // spurious "handled without record of send" failure in reconciliation).
        accounting.RecordSend(premiumFlowId, expectedHandlerInvocations: 1);
        accounting.RecordSend(standardFlowId, expectedHandlerInvocations: 1);

        var premiumMessage = new PremiumOrder(premiumFlowId) { CustomerId = premiumFlowId.ToString("N") };
        var standardMessage = new StandardOrder(standardFlowId) { CustomerId = standardFlowId.ToString("N") };

        await Task.WhenAll(
            sender.PublishAsync(premiumMessage, premiumOptions, cancellationToken),
            sender.PublishAsync(standardMessage, standardOptions, cancellationToken)).ConfigureAwait(false);

        try
        {
            var premiumAwait = signals.AwaitAsync(premiumFlowId, cancellationToken);
            var standardAwait = signals.AwaitAsync(standardFlowId, cancellationToken);
            var invocations = await Task.WhenAll(premiumAwait, standardAwait).ConfigureAwait(false);

            foreach (var invocation in invocations)
            {
                var crossCheck = CrossTenantAssertions.Check(invocation.Headers, context.ExpectedReceiver, invocation.BusTag);
                if (!crossCheck.Ok)
                {
                    failures.Add(crossCheck.Failure);
                }
            }
        }
        catch (OperationCanceledException)
        {
            failures.Add($"content-based-routing {context.Origin.ToHeaderValue()}->{context.ExpectedReceiver.ToHeaderValue()}: one or both handlers did not fire within {context.FlowTimeout}");
        }

        sw.Stop();
        return failures.Count == 0
            ? FlowResult.Pass(sw.Elapsed, sent: 2, handled: 2)
            : FlowResult.Fail(sw.Elapsed, sent: 2, handled: 0, [.. failures]);
    }

    private PublishOptions BuildPublishOptions(Guid flowId, BusIdentity origin) => new()
    {
        Headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StressHeaders.FlowId] = flowId.ToString("N"),
            [StressHeaders.OriginBus] = origin.ToHeaderValue(),
            [StressHeaders.Pattern] = Name,
        },
    };
}
