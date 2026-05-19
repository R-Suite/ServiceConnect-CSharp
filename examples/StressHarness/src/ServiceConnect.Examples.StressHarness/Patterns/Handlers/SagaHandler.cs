using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Patterns.Handlers;

/// <summary>
/// Three-stage process-manager handler exercised by <c>ProcessManagerDriver</c>. The
/// same handler class implements <see cref="IProcessHandler{TData, TMessage}"/> for
/// each of the three saga messages so a single registered type spans the full
/// progression; the framework correlates each inbound message to the same
/// <see cref="SagaData"/> instance via the default
/// <see cref="IProcessHandler{TData, TMessage}.ConfigureMapper"/> mapping on
/// <c>CorrelationId</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each <c>HandleAsync</c> overload advances <see cref="SagaData.Stage"/> to its
/// next expected value, captures the post-mutation stage into the shared
/// <see cref="SagaObservations"/> for the driver's assertion, and signals the
/// rendezvous registry on the per-message sub-flow id stamped under
/// <see cref="StressHeaders.FlowId"/>. The saga's own <see cref="Message.CorrelationId"/>
/// is distinct from those sub-flow ids — the framework uses it for state lookup,
/// the driver uses sub-flow ids for one-shot rendezvous because
/// <c>PerHandlerSignal.AwaitAsync</c> completes a single time per key.
/// </para>
/// <para>
/// Idempotency: each stage gates on the current <c>Stage</c> value before mutating
/// so a redelivery of the same message — for example after a broker requeue or an
/// optimistic-concurrency retry — does not advance the saga twice. The
/// observation log still receives a record on the replay (the observation is the
/// post-condition view at handler-exit time), which is fine: the driver asserts
/// the sequence contains <c>[1, 2, 3]</c> as a sub-sequence rather than insisting
/// on exact equality.
/// </para>
/// </remarks>
public sealed class SagaHandler(
    string busTag,
    FlowAccounting accounting,
    PerHandlerSignal signals,
    SagaObservations observations)
    : IProcessHandler<SagaData, SagaStarted>,
      IProcessHandler<SagaData, SagaIntermediate>,
      IProcessHandler<SagaData, SagaCompleted>
{
    public Task HandleAsync(SagaStarted message, SagaData data, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        if (data.Stage < 1)
        {
            data.Stage = 1;
        }
        observations.Record(message.CorrelationId, data.Stage);
        SignalStageArrival(context);
        return Task.CompletedTask;
    }

    public Task HandleAsync(SagaIntermediate message, SagaData data, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        if (data.Stage < 2)
        {
            data.Stage = 2;
        }
        observations.Record(message.CorrelationId, data.Stage);
        SignalStageArrival(context);
        return Task.CompletedTask;
    }

    public Task HandleAsync(SagaCompleted message, SagaData data, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        if (data.Stage < 3)
        {
            data.Stage = 3;
        }
        observations.Record(message.CorrelationId, data.Stage);
        SignalStageArrival(context);
        return Task.CompletedTask;
    }

    private void SignalStageArrival(IConsumeContext context)
    {
        // Sub-flow id (one per stage message) lives on the StressHeaders.FlowId header;
        // the saga's own CorrelationId is on the message body and is what the framework
        // uses for state lookup. Keeping the two distinct lets the driver await each
        // stage independently with the one-shot PerHandlerSignal rendezvous.
        if (context.Headers.TryGetValue(StressHeaders.FlowId, out var raw)
            && HeaderDecoder.Decode(raw) is { } flowIdStr
            && Guid.TryParseExact(flowIdStr, "N", out var subFlowId))
        {
            accounting.RecordHandled(subFlowId);
            signals.Signal(subFlowId, busTag, context);
        }
    }
}
