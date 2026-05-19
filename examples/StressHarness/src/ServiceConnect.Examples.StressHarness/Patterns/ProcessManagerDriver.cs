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
/// records <c>[1, 2, 3]</c> as a leading subsequence — exact equality would
/// over-constrain the harness because redelivery (legitimately) re-runs a
/// handler, and the observation list is post-condition not pre-condition.
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

            // Leading-subsequence check: the saga must have passed through stages 1, 2, 3
            // in that order at least once. Redelivery may insert extra records (e.g. a
            // stage-3 replay would also add a 3 to the tail) — those tail entries do not
            // invalidate the progression, so the assertion checks the prefix rather than
            // the full list.
            var observed = observations.Snapshot(sagaId);
            if (observed.Count < 3 || observed[0] != 1 || observed[1] != 2 || observed[2] != 3)
            {
                failures.Add($"process-manager {context.Origin.ToHeaderValue()}->{context.ExpectedReceiver.ToHeaderValue()}: expected stage progression [1, 2, 3] but observed [{string.Join(", ", observed)}]");
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
