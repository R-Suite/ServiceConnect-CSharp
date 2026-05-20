using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Examples.StressHarness.Patterns.Handlers;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Examples.StressHarness.Patterns;

/// <summary>
/// Drives a three-message saga (Started → Intermediate → Completed) on the
/// receiver bus, asserting the framework correlates each message to the same
/// persisted <see cref="SagaData"/> instance under one
/// <see cref="Message.CorrelationId"/> and surfaces the post-mutation stage on
/// every invocation. The assertion is that the receiver's observation log
/// contains <c>[1, 2, 3]</c> as an ordered subsequence — exact equality would
/// over-constrain the harness because publisher retry or broker redelivery
/// (both at-least-once-compatible) can re-run a handler. The saga's data layer
/// guards against double-advance via the <c>data.Stage &lt; N</c> check, but the
/// observation list captures every handler invocation regardless, so a duplicate
/// arrives as a repeated entry (e.g. <c>[1, 2, 2, 3]</c>).
/// </summary>
/// <remarks>
/// <para>
/// Routing is endpoint-explicit, matching <see cref="PointToPointDriver"/>:
/// the receiver's queue name is stamped on <see cref="SendOptions.EndPoint"/>
/// so the driver does not depend on per-type queue mapping state. Each message
/// in the saga carries a distinct sub-flow id under
/// <see cref="StressHeaders.FlowId"/> for the per-handler rendezvous, while
/// sharing the saga's own <see cref="Message.CorrelationId"/> for framework
/// state lookup — the two ids serve different purposes and must stay separate
/// because <c>PerHandlerSignal.AwaitAsync</c> is one-shot.
/// </para>
/// <para>
/// Sends are sequential rather than fired in parallel: the saga is a state
/// machine and the framework relies on the dispatch order matching the
/// intended progression. A parallel publish would race the three messages onto
/// the receiver's queue in indeterminate order and the assertion would flap.
/// </para>
/// </remarks>
public sealed class ProcessManagerDriver(FlowAccounting accounting, PerHandlerSignal signals, SagaObservations observations) : IPatternDriver
{
    public string Name => "process-manager";
    public bool RequiresPersistence => true;

    [SuppressMessage("Style", "IDE0060", Justification = "Threaded through to satisfy IPatternDriver contract; saga traffic flows one way.")]
    public async Task<FlowResult> RunFlowAsync(IBus sender, IBus receiver, StressFlowContext context, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var failures = new List<string>();

        var receiverEndpoint = context.ExpectedReceiver == BusIdentity.Alpha ? "stress-a.work" : "stress-b.work";
        var sagaId = context.FlowId;

        // Distinct sub-flow ids per stage so the one-shot rendezvous registry can
        // gate each stage independently. The flow-id header is what handlers signal
        // on; the saga's correlation id is what the framework keys state by.
        var stage1Id = Guid.NewGuid();
        var stage2Id = Guid.NewGuid();
        var stage3Id = Guid.NewGuid();

        accounting.RecordSend(stage1Id, expectedHandlerInvocations: 1);
        accounting.RecordSend(stage2Id, expectedHandlerInvocations: 1);
        accounting.RecordSend(stage3Id, expectedHandlerInvocations: 1);

        try
        {
            await SendStageAsync(sender, new SagaStarted(sagaId) { Token = sagaId.ToString("N") }, stage1Id, receiverEndpoint, context, cancellationToken).ConfigureAwait(false);
            await signals.AwaitAsync(stage1Id, cancellationToken).ConfigureAwait(false);

            await SendStageAsync(sender, new SagaIntermediate(sagaId) { Token = sagaId.ToString("N") }, stage2Id, receiverEndpoint, context, cancellationToken).ConfigureAwait(false);
            await signals.AwaitAsync(stage2Id, cancellationToken).ConfigureAwait(false);

            await SendStageAsync(sender, new SagaCompleted(sagaId) { Token = sagaId.ToString("N") }, stage3Id, receiverEndpoint, context, cancellationToken).ConfigureAwait(false);
            var finalInvocation = await signals.AwaitAsync(stage3Id, cancellationToken).ConfigureAwait(false);

            var crossCheck = CrossTenantAssertions.Check(finalInvocation.Headers, context.ExpectedReceiver, finalInvocation.BusTag);
            if (!crossCheck.Ok)
            {
                failures.Add(crossCheck.Failure);
            }

            // Ordered-subsequence check: the saga must have passed through stages 1, 2, 3
            // in that order at least once. Publisher retry and broker redelivery (both
            // at-least-once-compatible) can re-fire any handler, producing repeated entries
            // such as [1, 2, 2, 3] or [1, 1, 2, 3, 3]. The progression is preserved as long
            // as 1, 2, 3 appear in order somewhere in the list.
            var observed = observations.Snapshot(sagaId);
            if (!ContainsOrderedSubsequence(observed, [1, 2, 3]))
            {
                failures.Add($"process-manager {context.Origin.ToHeaderValue()}->{context.ExpectedReceiver.ToHeaderValue()}: expected stage progression [1, 2, 3] as ordered subsequence but observed [{string.Join(", ", observed)}]");
            }
        }
        catch (OperationCanceledException)
        {
            failures.Add($"process-manager {context.Origin.ToHeaderValue()}->{context.ExpectedReceiver.ToHeaderValue()}: saga did not complete within {context.FlowTimeout}");
        }

        sw.Stop();
        return failures.Count == 0
            ? FlowResult.Pass(sw.Elapsed, sent: 3, handled: 3)
            : FlowResult.Fail(sw.Elapsed, sent: 3, handled: 0, [.. failures]);
    }

    private static bool ContainsOrderedSubsequence(IReadOnlyList<int> observed, ReadOnlySpan<int> expected)
    {
        var idx = 0;
        foreach (var stage in observed)
        {
            if (idx < expected.Length && stage == expected[idx])
            {
                idx++;
                if (idx == expected.Length)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static Task SendStageAsync<TMessage>(
        IBus sender,
        TMessage message,
        Guid subFlowId,
        string receiverEndpoint,
        StressFlowContext context,
        CancellationToken cancellationToken)
        where TMessage : Message
    {
        var sendOptions = new SendOptions
        {
            EndPoint = receiverEndpoint,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [StressHeaders.FlowId] = subFlowId.ToString("N"),
                [StressHeaders.OriginBus] = context.Origin.ToHeaderValue(),
                [StressHeaders.Pattern] = "process-manager",
            },
        };
        return sender.SendAsync(message, sendOptions, cancellationToken);
    }
}
