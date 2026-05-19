using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Examples.StressHarness.Patterns;

/// <summary>
/// Drives the basic point-to-point send-and-handle flow across the bus pair. One direction
/// per <see cref="RunFlowAsync"/> invocation; <see cref="Orchestrator.FlowRunner"/>
/// schedules α→β and β→α concurrently against the same driver instance.
/// </summary>
/// <remarks>
/// <para>
/// Routing is endpoint-explicit rather than queue-mapping-based: the driver computes the
/// receiver's queue name from <see cref="StressFlowContext.ExpectedReceiver"/> and stamps
/// it on <see cref="SendOptions.EndPoint"/>. This bypasses any per-type queue-mapping
/// state the harness host would otherwise need to configure, and keeps each direction's
/// destination unambiguous when both buses share the same handler type.
/// </para>
/// <para>
/// The <c>receiver</c> bus parameter is required by the <see cref="IPatternDriver"/>
/// contract but unused here — point-to-point traffic flows one way and the driver only
/// touches the sender. Other patterns (request-reply, saga-reply-from-handler) need the
/// receiver handle, so the interface keeps both.
/// </para>
/// </remarks>
public sealed class PointToPointDriver(FlowAccounting accounting, PerHandlerSignal signals) : IPatternDriver
{
    public string Name => "p2p";
    public bool RequiresPersistence => false;

    [SuppressMessage("Style", "IDE0060", Justification = "Threaded through to satisfy IPatternDriver contract; other patterns use the receiver handle.")]
    public async Task<FlowResult> RunFlowAsync(IBus sender, IBus receiver, StressFlowContext context, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var failures = new List<string>();

        // Receiver-side queue name matches HarnessHost.BuildServices: alpha = stress-a.work,
        // beta = stress-b.work. Default-exchange routing sends the message straight onto
        // the named queue with mandatory:true, so a typo here surfaces as a PublishException
        // rather than a silent broker drop.
        var receiverEndpoint = context.ExpectedReceiver == BusIdentity.Alpha ? "stress-a.work" : "stress-b.work";
        var message = new P2pPing(context.FlowId) { Token = context.FlowId.ToString("N") };
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

        // Record the send before the await so the accounting layer cannot observe a
        // handler signal whose corresponding send hasn't been booked yet (would surface
        // as a spurious "handled without record of send" failure).
        accounting.RecordSend(context.FlowId, expectedHandlerInvocations: 1);
        await sender.SendAsync(message, sendOptions, cancellationToken).ConfigureAwait(false);

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
            failures.Add($"p2p {context.Origin.ToHeaderValue()}->{context.ExpectedReceiver.ToHeaderValue()}: handler did not fire within {context.FlowTimeout}");
        }

        sw.Stop();
        return failures.Count == 0
            ? FlowResult.Pass(sw.Elapsed, sent: 1, handled: 1)
            : FlowResult.Fail(sw.Elapsed, sent: 1, handled: 0, [.. failures]);
    }
}
