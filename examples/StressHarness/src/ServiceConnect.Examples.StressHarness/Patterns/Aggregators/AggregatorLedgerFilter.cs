using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Chaos;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Patterns.Aggregators;

/// <summary>
/// BeforeConsuming-stage filter that records a <see cref="MessageLedger"/> consume
/// row for every inbound message tagged with <c>StressHeaders.Pattern = "aggregator"</c>.
/// The framework's <c>AggregatorProcessor</c> dispatches the batch via
/// <see cref="Aggregator{T}.ExecuteAsync"/> which has no <c>IConsumeContext</c>, so
/// the regular per-handler ledger hook used by other patterns cannot record consumes
/// for aggregator items. This filter closes the gap by running before the framework's
/// processor takes over, while the envelope's headers are still intact.
/// </summary>
/// <remarks>
/// <para>
/// The pattern-header gate (<see cref="StressHeaders.Pattern"/>) keeps the filter
/// observational for non-aggregator traffic on the same bus. The filter always
/// returns <see cref="FilterAction.Continue"/>: it is non-blocking and a missing or
/// malformed header simply produces no ledger row rather than rejecting the message.
/// </para>
/// </remarks>
public sealed class AggregatorLedgerFilter(string busTag, MessageLedger ledger, IChaosClock chaosClock) : IFilter
{
    public Task<FilterAction> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        if (!envelope.Headers.TryGetValue(StressHeaders.Pattern, out var rawPattern)
            || HeaderDecoder.Decode(rawPattern) is not "aggregator")
        {
            return Task.FromResult(FilterAction.Continue);
        }

        if (envelope.Headers.TryGetValue(StressHeaders.MessageId, out var rawMsg)
            && HeaderDecoder.Decode(rawMsg) is { } msgIdStr
            && Guid.TryParseExact(msgIdStr, "N", out var messageId)
            && envelope.Headers.TryGetValue(StressHeaders.FlowId, out var rawFlow)
            && HeaderDecoder.Decode(rawFlow) is { } flowIdStr
            && Guid.TryParseExact(flowIdStr, "N", out var flowId))
        {
            ledger.RecordConsume(messageId, flowId, pattern: "aggregator", busTag, DateTimeOffset.UtcNow, chaosClock.CurrentWindow);
        }

        return Task.FromResult(FilterAction.Continue);
    }
}
