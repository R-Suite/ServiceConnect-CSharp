using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Patterns.Aggregators;

/// <summary>
/// Aggregator subclass exercised by <c>AggregatorDriver</c>. Batches incoming
/// <see cref="TelemetrySlice"/> messages by size or by elapsed time and records
/// every dispatched batch into the shared <see cref="AggregatorObservations"/>
/// log so the driver can assert the framework dispatched <c>BatchSize</c>
/// messages once the producer reached that count.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BatchSize"/> and <see cref="Timeout"/> are both required to return
/// strictly positive values — the framework's registry rejects a zero / negative
/// batch size and rejects a zero / infinite timeout at startup. The chosen
/// values (<c>4</c> / <c>3s</c>) are tuned for the smoke harness: the driver
/// sends exactly four items and expects the size-based flush to fire well inside
/// the per-flow timeout; the timeout-based flush is the safety net if a delivery
/// is dropped, in which case the partial batch surfaces in the observation log
/// and the driver's count check fails with diagnostic context.
/// </para>
/// <para>
/// Flow id is taken from <see cref="Message.CorrelationId"/> on the first message
/// in the batch — the aggregator's <see cref="ExecuteAsync"/> does not receive a
/// consume context, so headers are not available here. The driver stamps the
/// same flow id on every item in a single direction's batch, so any item's
/// correlation id is equivalent for identification purposes; the empty-batch
/// branch is defensive only.
/// </para>
/// </remarks>
public sealed class StressTelemetrySliceAggregator(string busTag, AggregatorObservations observations) : Aggregator<TelemetrySlice>
{
    public override int BatchSize() => 4;

    public override TimeSpan Timeout() => TimeSpan.FromSeconds(3);

    public override Task ExecuteAsync(IReadOnlyList<TelemetrySlice> messages, CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0)
        {
            return Task.CompletedTask;
        }

        var flowId = messages[0].CorrelationId;
        observations.Record(new AggregatorBatchObservation(flowId, busTag, messages.Count));
        return Task.CompletedTask;
    }
}
