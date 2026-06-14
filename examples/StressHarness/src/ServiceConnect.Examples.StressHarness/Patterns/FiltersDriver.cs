using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Examples.StressHarness.Patterns.Filters;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Examples.StressHarness.Patterns;

/// <summary>
/// Drives a single <see cref="FilteredMessage"/> through the receiver bus's
/// inbound pipeline, demonstrating that a registered <c>BeforeConsuming</c>
/// filter runs strictly before the matching handler. The filter and handler
/// each append a marker into a shared <see cref="FilterTrail"/> keyed by flow
/// id; the driver asserts the trail observed on the receiver is
/// <c>["filter", "handler"]</c> in that order.
/// </summary>
/// <remarks>
/// <para>
/// Routing is endpoint-explicit, matching <see cref="PointToPointDriver"/>:
/// the receiver's queue name is stamped on <see cref="SendOptions.EndPoint"/>
/// so the driver does not depend on per-type queue mapping state. The receiver
/// bus is unused — the assertion lives in the shared trail, not on the
/// receiver IBus handle — but the contract requires it.
/// </para>
/// <para>
/// The driver waits for the handler signal first (one-shot rendezvous on the
/// flow id), then reads the trail snapshot. The signal guarantees the handler
/// has committed its <c>"handler"</c> marker; the filter's <c>"filter"</c>
/// marker is committed earlier in the same dispatch (before-consuming runs
/// before the handler is even resolved), so by the time the handler signal
/// fires both markers are present.
/// </para>
/// </remarks>
public sealed class FiltersDriver(FlowAccounting accounting, PerHandlerSignal signals, FilterTrail trail) : IPatternDriver
{
    public string Name => "filters";
    public bool RequiresPersistence => false;

    [SuppressMessage("Style", "IDE0060", Justification = "Threaded through to satisfy IPatternDriver contract; filters traffic flows one way.")]
    public async Task<FlowResult> RunFlowAsync(IBus sender, IBus receiver, StressFlowContext context, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var failures = new List<string>();

        var receiverEndpoint = context.ExpectedReceiver == BusIdentity.Alpha ? "stress-a.work" : "stress-b.work";
        var message = new FilteredMessage(context.FlowId) { Token = context.FlowId.ToString("N") };
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

        accounting.RecordSend(context.FlowId, expectedHandlerInvocations: 1);
        await sender.SendAsync(message, sendOptions, cancellationToken).ConfigureAwait(false);

        try
        {
            var invocation = await signals.AwaitAsync(context.FlowId, cancellationToken).ConfigureAwait(false);
            var crossCheck = CrossTenantAssertions.Check(invocation.Headers, context.ExpectedReceiver, invocation.BusTag);
            if (!crossCheck.Ok)
            {
                failures.Add(crossCheck.Failure);
            }

            // Snapshot the trail under its per-list lock so a concurrently-running
            // direction (the sibling α↔β flow) cannot make this assertion observe a
            // partial append. The expected progression is filter then handler as an
            // ordered subsequence — publisher retry or broker redelivery can re-fire
            // both stages, producing trails like [filter, handler, filter, handler].
            // Reversed order or missing markers remain failures.
            var snapshot = trail.Snapshot(context.FlowId);
            if (!ContainsOrderedSubsequence(snapshot, "filter", "handler"))
            {
                failures.Add($"filters {context.Origin.ToHeaderValue()}->{context.ExpectedReceiver.ToHeaderValue()}: expected trail [filter, handler] as ordered subsequence but observed [{string.Join(", ", snapshot)}]");
            }
        }
        catch (OperationCanceledException)
        {
            failures.Add($"filters {context.Origin.ToHeaderValue()}->{context.ExpectedReceiver.ToHeaderValue()}: handler did not fire within {context.FlowTimeout}");
        }

        sw.Stop();
        return failures.Count == 0
            ? FlowResult.Pass(sw.Elapsed, sent: 1, handled: 1)
            : FlowResult.Fail(sw.Elapsed, sent: 1, handled: 0, [.. failures]);
    }

    private static bool ContainsOrderedSubsequence(IReadOnlyList<string> observed, string first, string second)
    {
        var seenFirst = false;
        foreach (var entry in observed)
        {
            if (!seenFirst)
            {
                if (string.Equals(entry, first, StringComparison.Ordinal))
                {
                    seenFirst = true;
                }
            }
            else if (string.Equals(entry, second, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
