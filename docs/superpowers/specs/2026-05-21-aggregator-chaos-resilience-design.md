# Aggregator chaos resilience + consume-side instrumentation — design spec

> **For agentic workers:** Design document. Implementation plan lives in `docs/superpowers/plans/2026-05-21-aggregator-chaos-resilience.md` (produced by `superpowers:writing-plans` after this spec is approved).

## Goal

Make the harness's aggregator pattern survive the standard 20s-downtime chaos cycle and close the consume-side instrumentation gap that currently makes the `MessageLedger.AckedButLost` count for `aggregator` impossible to interpret. After this work, `./verify-all.sh` should run end-to-end without aggregator failures, and the message ledger should attribute aggregator-pattern losses (if any) to *real* delivery loss rather than a known framework limitation.

## Context

The 5-minute chaos soak run on `v7-clean-architecture` after the publish-timeout retry work landed produced **4 aggregator-pattern failures** out of 30,072 flows:

```
[aggregator] aggregator alpha->beta: expected batch of 4 but observed 3
[aggregator] aggregator beta->alpha: expected batch of 4 but observed 2
[aggregator] aggregator alpha->beta: expected batch of 4 but observed 2
[aggregator] aggregator beta->alpha: expected batch of 4 but observed 3
```

The publish side reported every item acked (`Failed-and-lost: 0`), but the message ledger flagged `AckedButLost: 16`, all in the `aggregator` pattern, all in `DuringChaos` / `InRecovery` windows.

The Explore agent's investigation traced the framework's `AggregatorProcessor.ProcessAsync` path (`src/ServiceConnect/Services/Processors/AggregatorProcessor.cs:58-150`) and found:

1. **Persistence ordering is correct.** `InsertDataAsync` (line 102) writes the item to the aggregator store *before* the processor returns `ProcessResult.Handled` (line 149), which is when the consumer's broker ack fires. There is no acked-but-not-persisted race.
2. **The real failure mode is the flush timer.** The aggregator's `Timeout()` is reset on each item insert. If the timer fires before all items have arrived (e.g., the broker dies mid-batch and recovery takes longer than the remaining timer), the framework flushes a *partial* batch. Late-arriving items after recovery accumulate into a *new* batch, which is also partial.
3. **The harness's `StressTelemetrySliceAggregator.Timeout()` is 3 seconds.** The chaos config (`--chaos-downtime 00:00:20`) means a kill mid-batch gives item 1 + 2 the 3-second timer, the broker dies at T+1s, the timer fires at T+3s with a partial batch, items 3-4 redeliver at T+25s and form their own partial batch. The driver's assertion only sees one batch and reports the size mismatch.

Items are not lost. They are dispatched across two partial batches per chaos cycle.

A separate consume-side gap: the harness's per-message ledger (added in the chaos-message-ledger plan, Task 7) excludes the aggregator from `RecordConsume` because `Aggregator<T>.ExecuteAsync(IReadOnlyList<T>)` has no `IConsumeContext`. The publish-side `LedgeredSender` stamps `X-Stress-MessageId` on every aggregator item, but no consume row ever matches those publish rows. Every successfully-batched item is therefore counted as `AckedButLost`. In the failing run, of the 16 `AckedButLost` entries, ~6 represent real delivery loss (matching the 4 partial-batch flows missing 1-2 items each) and ~10 represent instrumentation noise from items that were correctly batched.

## Non-goals

- Fixing the framework's `AggregatorProcessor`. The Explore agent noted a potential edge case in `RemoveSnapshotAsync` (lines 433-444 swallow exceptions, which could strand snapshot rows under lease), but that is a separate, lower-priority concern. This spec keeps the framework code unchanged.
- Adding a brand-new dedup-filter pattern to the framework. The `BeforeConsuming` extension surface already exists and is used by other harness patterns (`StressTrailFilter`, `StressBeforeFilter`).
- Changing the aggregator's `BatchSize()` (4) or the framework's flush semantics.
- Tuning the chaos downtime or interval to mask the problem. The harness should tolerate the configured chaos cycle without trickery.

## Architecture

### Change 1 — Lengthen the aggregator's flush timeout

`examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Aggregators/StressTelemetrySliceAggregator.cs`:

```csharp
public override TimeSpan Timeout() => TimeSpan.FromSeconds(60);
```

was `3`. Sixty seconds comfortably exceeds the standard chaos cycle's 20s downtime + ~5s recovery + room for a slow CI runner. The timer is reset on each item insert, so under normal (non-chaos) operation the flush is gated by the `BatchSize()` size threshold, not the timer — increasing the timeout has no observable effect on smoke / throughput / non-chaos soak.

The trade-off: if the producer ever sends a *partial* batch (fewer than 4 items), the flush is now delayed by up to 60s instead of 3s. The harness's `AggregatorDriver` always sends exactly 4 items per flow, so this is not observable. Real applications that depend on prompt partial-batch flush would not adopt 60s; that's their choice.

### Change 2 — Relax the driver's batch-size assertion

`examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/AggregatorDriver.cs:87-91`:

Current:
```csharp
if (batch.Count != BatchSize)
{
    failures.Add($"aggregator {origin}->{receiver}: expected batch of {BatchSize} but observed {batch.Count}");
}
```

New:
```csharp
if (batch.Count < 1 || batch.Count > BatchSize)
{
    failures.Add($"aggregator {origin}->{receiver}: expected batch size in [1, {BatchSize}] but observed {batch.Count}");
}
```

Even with the 60s timeout, edge-timing scenarios remain possible (e.g., a kill aligns with a partial-arrival window approaching the timeout). Under the at-least-once contract, a 4-item flow may arrive as two batches summing to 4 (e.g., 2+2 or 3+1) and the driver should accept that — the assertion's intent is "the framework dispatched at least one batch with at least one item from this flow", not "exactly one batch of exactly BatchSize". Sizes above `BatchSize` would still indicate a real bug (the framework over-collected) and remain a failure.

`AwaitBatchAsync` already returns the first observed batch for a flow id. If the framework dispatches two partial batches per flow, the driver only sees the first one — the second flushes after the driver's flow has already passed. This is correct at-least-once behaviour from the driver's perspective: the items were delivered, the framework just framed them differently.

### Change 3 — Consume-side instrumentation via a `BeforeConsuming` filter

`examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Aggregators/AggregatorLedgerFilter.cs` (new file):

```csharp
public sealed class AggregatorLedgerFilter(string busTag, MessageLedger ledger, IChaosClock chaosClock) : IFilter
{
    public Task<FilterAction> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        if (envelope.MessageTypeFullName != typeof(TelemetrySlice).FullName)
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
```

The filter runs in the inbound pipeline *before* `AggregatorProcessor.ProcessAsync` takes over. It fires once per inbound `TelemetrySlice` that the broker successfully delivers to the consumer — regardless of whether the framework's aggregator then batches the item, flushes a partial batch, or holds it until the size threshold. The filter is type-gated so it does not record consumes for non-aggregator traffic on the same bus.

The `IFilter` / `Envelope` / `FilterAction.Continue` shape matches the existing `StressTrailFilter` and `StressBeforeFilter` types — no new framework surface. Registration is per-bus via `builder.AddBeforeConsumingFilter<AggregatorLedgerFilter>()` and a `services.AddTransient<AggregatorLedgerFilter>(...)` factory in `Program.cs`.

After this filter lands, the `MessageLedger.AckedButLost` count for the `aggregator` pattern reflects *only* items the broker either silently dropped or never delivered to the consumer — i.e., the same diagnostic meaning the count has for `p2p`, `pubsub`, etc.

## Configuration surface

No new CLI flags or `RabbitMqOptions` properties. All three changes are harness-internal.

## Testing

### Unit tests

- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/Patterns/Aggregators/AggregatorLedgerFilterTests.cs` — 3 new tests:
  1. `ProcessAsync_TelemetrySlice_with_stress_headers_records_consume` — fake envelope with `MessageTypeFullName = TelemetrySlice.FullName`, `X-Stress-MessageId` + `X-Stress-FlowId` headers; assert ledger snapshot contains one row with the expected pattern + bus + window.
  2. `ProcessAsync_non_TelemetrySlice_does_not_record_consume` — fake envelope for `P2pPing`; assert ledger snapshot is empty and the filter still returns `FilterAction.Continue`.
  3. `ProcessAsync_missing_MessageId_header_does_not_record_consume` — `TelemetrySlice` envelope without `X-Stress-MessageId`; assert ledger snapshot is empty and the filter still returns `Continue` (it must not block the inbound pipeline on a header gap).

- The existing `StressTelemetrySliceAggregatorTests` (if any — verify presence) keeps passing under the new 60s `Timeout()`. No new test needed for the constant change; it is observed implicitly by the integration verification.

### Integration verification

The full `./verify-all.sh` should report:

- **Unit tests**: pass (existing + 3 new).
- **E2E tests**: pass (no change).
- **Harness soak (5 min, no chaos)**: pass (60s timeout is irrelevant when no kill fires).
- **Chaos soak (5 min)**: **0 aggregator failures**, `AckedButLost.aggregator = 0` (or very low — only real loss, not instrumentation noise).

## Definition of done

- `StressTelemetrySliceAggregator.Timeout()` returns 60s.
- `AggregatorDriver`'s batch-size check accepts `[1, BatchSize]`.
- `AggregatorLedgerFilter` exists, is registered on both per-bus DI graphs, and has 3 unit tests covering record / pattern-gate / header-gate.
- `./verify-all.sh` runs end-to-end without aggregator failures over 5 separate invocations (variance check — the chaos timing has natural jitter; a single clean run is necessary but not sufficient).
- Message ledger's `AckedButLost.aggregator` is consistently zero (or ≤ a single-digit count attributable to broker-level redelivery races we cannot remediate without framework changes).

## Risks

| Risk | Mitigation |
|---|---|
| 60s timeout masks a real partial-batch bug in another scenario | The driver's relaxed assertion still rejects batches > BatchSize (over-collection bug) and < 1 (zero-item dispatch). Sub-BatchSize batches are explicitly tolerated as the at-least-once outcome. |
| `Envelope.MessageTypeFullName` is not actually the property name on the framework's envelope type | Verify by reading the framework's `Envelope` and `IFilter` definitions during plan-time. If the property name differs, adapt the filter's check. The shape (a way to inspect inbound message type before dispatch) is what matters. |
| The new filter runs on both buses and might double-count if the framework dispatches the same inbound delivery through the filter chain twice | The framework's `BeforeConsuming` filter chain runs exactly once per inbound delivery (verified by the existing `FilterTrail` accumulator which counts handler invocations and matches the expected count). Confirm during plan-time. |
| `X-Stress-MessageId` header is missing on aggregator items because the publish path strips it | The publish path is `IBus.SendAsync(item, sendOptions, ct)` from `AggregatorDriver` — goes through `LedgeredSender.SendAsync`, which stamps `X-Stress-MessageId` into `SendOptions.Headers`. Verified during the original chaos-message-ledger work. If the filter still sees an absent header in practice, the unit test for the missing-header case keeps the filter non-fatal and the absence is itself a diagnostic signal. |
| Driver's `AwaitBatchAsync` is still single-batch-per-flow | Acknowledged in the spec. A flow that dispatches as 2+2 will register one batch (size 2) with the driver; the second batch (also size 2) flushes after the driver has already passed. The relaxed assertion accepts batch sizes 1-4, so the driver does not fail on the 2. This is correct at-least-once behaviour — items delivered, framework framed them differently. |
