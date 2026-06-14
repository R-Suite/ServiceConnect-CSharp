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
/// batch size and rejects a zero / infinite timeout at startup. The driver sends
/// exactly <see cref="BatchSize"/> items per flow so the size-based flush is the
/// load-bearing trigger in normal operation; the timeout is the safety net for a
/// dropped delivery. The chosen <c>60s</c> comfortably exceeds the harness's
/// standard chaos downtime (<c>20s</c>) plus recovery, so a kill mid-batch does
/// not flush a partial batch before redelivery completes. Real applications that
/// rely on prompt partial-batch flush would pick a much shorter value; the harness
/// favours batch completeness over flush latency.
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

    public override TimeSpan Timeout() => TimeSpan.FromSeconds(60);

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
