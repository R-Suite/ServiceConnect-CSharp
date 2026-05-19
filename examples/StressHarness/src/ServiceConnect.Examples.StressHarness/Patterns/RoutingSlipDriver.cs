using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Examples.StressHarness.Patterns.Handlers;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Patterns;

/// <summary>
/// Drives a two-hop routing slip across both buses. The sender bus's own queue is
/// the first destination so the slip stays on the issuing bus for hop 1, then the
/// framework's slip-forwarder hands it off to the other bus's queue for hop 2.
/// The assertion is that the per-flow trail recorded by the handler on each hop
/// reflects the destinations-list order: alpha-then-beta when alpha issued the
/// slip, beta-then-alpha when beta issued it.
/// </summary>
/// <remarks>
/// <para>
/// Routing is destination-list explicit: <see cref="ServiceConnect.Interfaces.IBus.RouteAsync"/>
/// sends to <c>destinations[0]</c> directly and encodes the remaining destinations
/// into the envelope's <c>RoutingSlip</c> header. After each handler completes,
/// <c>HandlerProcessor.ForwardRoutingSlipAsync</c> reads the inbound slip and
/// forwards the message along to the next destination — the handler never invokes
/// <see cref="ServiceConnect.Interfaces.IBus.RouteAsync"/> itself.
/// </para>
/// <para>
/// Synchronisation is via <see cref="SlipTrail.Snapshot"/> polling rather than the
/// per-handler rendezvous: <see cref="PerHandlerSignal"/> is one-shot per flow id
/// and the slip fires the handler twice (once per hop). Polling the trail length
/// until both hops have recorded their bus tags lets the driver observe the
/// full progression without coupling to a multi-shot rendezvous primitive.
/// </para>
/// <para>
/// Per-bus self-loops are explicitly allowed on the producer side — the bus that
/// issues the slip places its own queue first in the destination list, which the
/// framework's <c>Bus.RouteAsync</c> accepts. The downstream self-loop check fires
/// only when the inbound slip names the local queue as the *next* hop on the
/// already-running bus, which never happens here because the second hop targets
/// the other bus.
/// </para>
/// </remarks>
public sealed class RoutingSlipDriver(FlowAccounting accounting, PerHandlerSignal signals, SlipTrail trail) : IPatternDriver
{
    public string Name => "routing-slip";
    public bool RequiresPersistence => false;

    [SuppressMessage("Style", "IDE0060", Justification = "Threaded through to satisfy IPatternDriver contract; the slip's forwarding stays on a single bus per hop until the slip-forwarder hands off.")]
    public async Task<FlowResult> RunFlowAsync(IBus sender, IBus receiver, StressFlowContext context, CancellationToken cancellationToken)
    {
        _ = signals;
        _ = receiver;
        var sw = Stopwatch.StartNew();
        var failures = new List<string>();

        // Destination order encodes the assertion: the slip's first hop runs on the
        // sender's own queue, the second hop runs on the other bus's queue. The trail
        // appended by the handler must reflect that order.
        var senderQueue = context.Origin == BusIdentity.Alpha ? "stress-a.work" : "stress-b.work";
        var otherQueue = context.Origin == BusIdentity.Alpha ? "stress-b.work" : "stress-a.work";
        var senderTag = context.Origin.ToHeaderValue();
        var otherTag = context.Origin.Other().ToHeaderValue();

        // Two expected handler invocations per flow: one per hop. Booked up-front so
        // reconciliation cannot observe a handler signal whose corresponding send
        // hasn't been booked yet (would surface as a spurious
        // "handled without record of send" failure).
        accounting.RecordSend(context.FlowId, expectedHandlerInvocations: 2);

        // Flow correlation is on Message.CorrelationId rather than a header because
        // IBus.RouteAsync has no SendOptions overload — there is no caller-visible
        // path to attach custom harness headers. The handler decodes the flow id
        // from the body's correlation id, matching the convention the aggregator
        // driver uses for the same reason (its ExecuteAsync has no access to
        // per-message headers).
        var message = new SlipOrder(context.FlowId) { OrderId = context.FlowId.ToString("N") };
        try
        {
            await sender.RouteAsync(message, [senderQueue, otherQueue], cancellationToken).ConfigureAwait(false);

            // Poll the trail until both hops have recorded their bus tag, or the
            // flow timeout fires. The trail mutation is serialised inside SlipTrail
            // so a snapshot of length 2 reflects a true two-hop completion.
            var pollInterval = TimeSpan.FromMilliseconds(25);
            IReadOnlyList<string> observed = [];
            while (!cancellationToken.IsCancellationRequested)
            {
                observed = trail.Snapshot(context.FlowId);
                if (observed.Count >= 2)
                {
                    break;
                }
                await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            }

            if (observed.Count < 2)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"routing-slip {senderTag}->{otherTag}: expected 2-hop trail but observed [{string.Join(", ", observed)}] within {context.FlowTimeout}"));
            }
            else if (!string.Equals(observed[0], senderTag, StringComparison.Ordinal) ||
                     !string.Equals(observed[1], otherTag, StringComparison.Ordinal))
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"routing-slip {senderTag}->{otherTag}: expected hop order [{senderTag}, {otherTag}] but observed [{string.Join(", ", observed)}]"));
            }
        }
        catch (OperationCanceledException)
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture,
                $"routing-slip {senderTag}->{otherTag}: slip did not complete within {context.FlowTimeout}"));
        }

        sw.Stop();
        return failures.Count == 0
            ? FlowResult.Pass(sw.Elapsed, sent: 1, handled: 2)
            : FlowResult.Fail(sw.Elapsed, sent: 1, handled: 0, [.. failures]);
    }
}
