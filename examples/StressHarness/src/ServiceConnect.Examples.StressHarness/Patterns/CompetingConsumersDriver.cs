using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Examples.StressHarness.Patterns.Handlers;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Examples.StressHarness.Patterns;

/// <summary>
/// Drives a batch of <see cref="WorkItem"/> sends to the receiver bus, then asserts that
/// more than one distinct <see cref="WorkItemHandler"/> instance recorded at least one
/// arrival. Two handler registrations exist per bus (distinguished by their handler tag);
/// the framework dispatches every delivery to both, so the assertion passes whenever the
/// fan-out across registrations is non-empty — the harness's single-process bus pair
/// cannot model a true round-robin between separate consumer processes, but the
/// multi-handler dispatch path is what's under test here.
/// </summary>
/// <remarks>
/// <para>
/// Routing is endpoint-explicit: every item is stamped with the receiver's queue name on
/// <see cref="SendOptions.EndPoint"/>, matching <see cref="PointToPointDriver"/>'s
/// addressing. The driver re-uses a single flow id across the entire batch because the
/// receiver-side accounting only cares about handler-invocation totals per flow; per-item
/// correlation lives in <see cref="WorkItem.Sequence"/> for broker-capture forensics.
/// </para>
/// <para>
/// Reconciliation uses a bounded polling loop (rather than awaiting
/// <see cref="PerHandlerSignal"/>) because the rendezvous only gates the first arrival —
/// the driver needs to observe the full batch drain through the broker before sampling
/// the per-handler counters. The loop exits early once
/// <see cref="FlowAccounting.Reconcile"/> reports the flow id is no longer missing, and
/// bounds by <see cref="StressFlowContext.FlowTimeout"/> so a stuck broker can't pin the
/// orchestrator.
/// </para>
/// </remarks>
public sealed class CompetingConsumersDriver(
    FlowAccounting accounting,
    PerHandlerSignal signals,
    WorkItemCounters counters) : IPatternDriver
{
    private const int BatchSize = 10;

    public string Name => "competing-consumers";
    public bool RequiresPersistence => false;

    [SuppressMessage("Style", "IDE0060", Justification = "Threaded through to satisfy IPatternDriver contract; competing-consumers traffic flows one way.")]
    public async Task<FlowResult> RunFlowAsync(IBus sender, IBus receiver, StressFlowContext context, CancellationToken cancellationToken)
    {
        _ = signals;
        var sw = Stopwatch.StartNew();
        var failures = new List<string>();

        // Receiver-side queue name matches HarnessHost.BuildServices: alpha = stress-a.work,
        // beta = stress-b.work. Default-exchange routing sends each item straight onto the
        // named queue with mandatory:true, so a typo here surfaces as a PublishException
        // rather than a silent broker drop.
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

        // Single RecordSend booking the whole batch. RecordSend sums expected invocations
        // across calls, so an equivalent loop-per-send would yield the same total — one
        // call is cheaper and keeps the bookkeeping intent obvious. The receiver bus has
        // two handler registrations, so the framework dispatches each delivery twice; the
        // booking is intentionally lower-bound (BatchSize, not 2*BatchSize) so reconcile
        // never flags a partial-batch drain as observed-exceeds-expected.
        accounting.RecordSend(context.FlowId, expectedHandlerInvocations: BatchSize);

        for (var i = 1; i <= BatchSize; i++)
        {
            var item = new WorkItem(context.FlowId) { Sequence = i };
            await sender.SendAsync(item, sendOptions, cancellationToken).ConfigureAwait(false);
        }

        // Bounded poll for the full batch drain. The signal rendezvous only fires on the
        // first arrival; the assertion needs the entire batch to land so all distinct
        // handlers have a chance to bump their counter. Yield with Task.Delay so the
        // broker IO and consumer pumps make progress between samples.
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
                $"competing-consumers {context.Origin.ToHeaderValue()}->{receiverBusTag}: batch did not drain within {context.FlowTimeout}"));
        }
        else
        {
            // Count distinct handlers on the receiver bus that observed at least one
            // delivery. The counter dictionary is keyed `"{busTag}:{handlerTag}"` so a
            // simple prefix scan isolates the receiver-side workers without an extra
            // bookkeeping layer.
            var receiverPrefix = $"{receiverBusTag}:";
            var distinctWorkers = counters.Hits
                .Where(kv => kv.Key.StartsWith(receiverPrefix, StringComparison.Ordinal) && kv.Value > 0)
                .Select(kv => kv.Key)
                .Count();

            if (distinctWorkers < 2)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"competing-consumers {context.Origin.ToHeaderValue()}->{receiverBusTag}: expected ≥2 workers to receive items, observed {distinctWorkers}"));
            }
        }

        sw.Stop();
        var handledForFlow = counters.Hits
            .Where(kv => kv.Key.StartsWith($"{receiverBusTag}:", StringComparison.Ordinal))
            .Sum(kv => kv.Value);
        return failures.Count == 0
            ? FlowResult.Pass(sw.Elapsed, sent: BatchSize, handled: handledForFlow)
            : FlowResult.Fail(sw.Elapsed, sent: BatchSize, handled: handledForFlow, [.. failures]);
    }
}
