# Chaos message ledger — design spec

> **For agentic workers:** Design document. Implementation plan lives in `docs/superpowers/plans/2026-05-20-chaos-message-ledger.md` (produced by `superpowers:writing-plans` after this spec is approved).

## Goal

Identify the mechanism by which messages are lost during chaos events. The 5-minute chaos soak loses ~16 flows per run with **zero broker redeliveries** — every failing flow belongs to a multi-message pattern (streaming, routing-slip, aggregator, scatter-gather), every single-message pattern is clean. This points at a small but non-zero per-message drop rate amplified by flow length, not a pattern-specific bug. The ledger instruments every publish and every consume so the post-run analysis can classify each missing message into one of two failure modes:

1. **Publisher believed-acked, never delivered.** Broker's in-memory queue accepted and acked the publish, but a SIGKILL between ack and fsync (or between ack and routing-table replication) lost it.
2. **Publisher believed-failed, but no retry occurred.** Publish path returned ok to the caller despite an underlying nack/timeout — i.e. the publish-confirms layer or our use of it has a leak.

The ledger is purely diagnostic. It does not change broker behaviour or attempt to fix the loss. Its output is the evidence we need to choose the next fix.

## Context

After the chaos extension landed (`d9502e65` … `66f82b0d`), three layered defences were applied (`28d6805d`):

- `--chaos-stop-timeout=30s` raised SIGTERM grace for `docker compose stop`.
- `PrefetchCount=1` capped per-consumer unacked depth.
- Post-schedule drain wait polled `FlowAccounting.Reconcile()` after every chaos schedule.

None reduced the loss. The 5-min chaos soak still produced 16 unhandled flows (10 streaming, 2 each of routing-slip, aggregator, scatter-gather; 0 elsewhere), and `DuplicatedFlows` stayed at 0 across all six kill events. The failure shape is consistent with a per-message drop rate of ~1 in a few thousand messages during the kill/recovery window. Per the systematic-debugging skill: three fixes failed, so we stop guessing and instrument.

## Non-goals

- Fixing the message loss. That comes after we know which mechanism is at fault. The spec for the fix follows separately, gated on this ledger's output.
- Instrumenting broker internals. We observe only what the harness publishes and consumes.
- Changing `PublisherAcknowledgements` semantics or the framework's publish path.
- Tracking message *content* — the ledger records only metadata (`Guid` ids, timestamps, the chaos window, the pattern, the ack outcome). Payloads stay out of memory.
- Adding ledger output to non-chaos modes by default. The ledger runs always (cheap), but the report section only renders when at least one chaos event was scheduled OR when `MissingFlows` is non-empty.

## What the ledger records

For every outbound publish/send the harness initiates:

| Field | Source |
|---|---|
| `MessageId` | New `Guid` minted at publish time, stamped into headers as `X-Stress-MessageId` |
| `FlowId` | Existing `X-Stress-FlowId` header value |
| `Pattern` | Existing `X-Stress-Pattern` header value |
| `OriginBus` | Existing `X-Stress-Origin-Bus` header value |
| `PublishStarted` | `DateTimeOffset.UtcNow` immediately before the `sender.PublishAsync` / `sender.SendAsync` call |
| `PublishCompleted` | `DateTimeOffset.UtcNow` immediately after the awaited call returns |
| `PublishOutcome` | enum `Acked` / `Failed` (`Failed` = the awaited task threw) |
| `PublishChaosWindow` | `ChaosClock.CurrentWindow` at `PublishStarted` |

For every inbound consume:

| Field | Source |
|---|---|
| `MessageId` | `X-Stress-MessageId` header on the inbound context |
| `FlowId` | `X-Stress-FlowId` header on the inbound context |
| `Pattern` | `X-Stress-Pattern` header on the inbound context |
| `ConsumingBus` | bus tag at handler dispatch |
| `Consumed` | `DateTimeOffset.UtcNow` at handler entry |
| `ConsumeChaosWindow` | `ChaosClock.CurrentWindow` at `Consumed` |

A consume row is appended *every time a handler is invoked* for a message id (not deduplicated). That's how broker redelivery produces multiple consume rows for one publish row — the same signal `Reconcile()` already surfaces via `DuplicatedFlows`, but now per-message instead of per-flow.

## Architecture

### MessageLedger singleton

New harness type at `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Assertions/MessageLedger.cs`. Implements `IFlowKeyedSingleton` for the existing per-tick reclamation hook.

```csharp
public sealed class MessageLedger : IFlowKeyedSingleton
{
    private readonly ConcurrentDictionary<Guid, PublishRecord> _publishes = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentBag<ConsumeRecord>> _consumes = new();

    public void RecordPublishStart(Guid messageId, Guid flowId, string pattern, string originBus, ChaosWindow window);
    public void RecordPublishCompleted(Guid messageId, DateTimeOffset completed, PublishOutcome outcome);
    public void RecordConsume(Guid messageId, Guid flowId, string pattern, string consumingBus, ChaosWindow window);

    public LedgerSnapshot Snapshot();
    public void TryRemoveCompleted(IEnumerable<Guid> completedFlowIds);
}
```

`PublishRecord` and `ConsumeRecord` are immutable readonly structs holding the fields above. Storing publishes keyed by `MessageId` (Guid) and consumes keyed by `MessageId` → bag-of-consume-records lets reconciliation cross-reference in O(publishes).

`TryRemoveCompleted` removes publish + consume records for all message ids whose flow id is in the supplied set, identical pattern to the seven existing flow-keyed singletons (so soak runs stay under the memory budget). Failed flows are NOT reclaimed — their detail must survive the report.

### Hooking publishes

The harness drivers all call `sender.PublishAsync` / `sender.SendAsync` with a `Publish`/`SendOptions` object that already carries the `Headers` dictionary. Hooking happens at the harness layer, not the framework: a small wrapper `LedgeredSender` (implementing the relevant subset of `IBus` used by drivers, or a discrete helper used by each driver) does:

1. Mint `Guid` MessageId.
2. Set `headers["X-Stress-MessageId"] = MessageId.ToString()`.
3. Call `ledger.RecordPublishStart(messageId, flowId, pattern, originBus, ChaosClock.CurrentWindow)`.
4. `try { await inner.PublishAsync(...); ledger.RecordPublishCompleted(messageId, now, Acked); } catch { ledger.RecordPublishCompleted(messageId, now, Failed); throw; }`.

The drivers receive a sender that's already wrapped; the wrap point is the construction site in `HarnessHost` (where the two `IBus` instances are wired into the DI graph). No framework changes; no driver changes beyond accepting the wrapped sender (which they already do via DI).

For multi-message patterns (aggregator's N items, streaming's N chunks, routing-slip's N hops, scatter-gather's request + replies), each `sender.SendAsync` / `PublishAsync` call goes through the wrapper independently, producing N publish rows per flow. This is the source of per-message granularity.

### Hooking consumes

ServiceConnect handlers receive `IConsumeContext` (or equivalent) carrying headers. The seven existing flow-keyed accumulators already read `X-Stress-FlowId` from this context in every handler. The ledger's consume hook follows the same pattern: a tiny `MessageLedgerHandler` or middleware-level interceptor that runs before each handler and calls `ledger.RecordConsume(...)` using the headers.

Two implementation options for the consume hook (the plan picks one; both are viable):

a. **Per-handler call.** Every harness handler adds one line near the top: `_ledger.RecordConsume(GetMessageId(ctx), GetFlowId(ctx), ...)`. Mechanical, no framework knowledge required.

b. **Middleware.** Register a custom middleware on both buses that reads the message id and flow id off the inbound context and records before dispatching to the handler. One change, applies to all 14 patterns.

The plan should prefer (b) if the framework's middleware surface gives clean access to inbound headers; otherwise (a). Either way the recorded data is the same.

### Chaos window resolution

`ChaosClock.CurrentWindow` is already a `ChaosWindow` enum (`PreChaos`, `DuringChaos`, `InRecovery`, `PostChaos`). The ledger captures the window at publish-start and at consume-arrival. The post-run report breaks down loss counts by **publish window**, so we see whether the dropped publishes happened before, during, or after the kill — which is the single most diagnostic axis.

## Reconciliation and the four quadrants

After the soak finishes, `MessageLedger.Snapshot()` returns the full set of publish and consume rows. The harness's existing report builder calls a new `MessageLedgerAnalyzer` that classifies each publish row:

| Publish outcome | Consume rows | Classification | Hypothesis |
|---|---|---|---|
| Acked | ≥ 1 | Normal delivery | — |
| Acked | 0 | **Acked-but-lost** | Publisher-side ack-before-fsync OR routing-state loss |
| Failed | ≥ 1 | Failed-then-delivered | Probably client retry; the publish exception was misleading |
| Failed | 0 | Failed-and-lost | Expected — broker was down at publish; no retry happened |

Two more axes:

- **Duplicates per message id.** Count of consume rows for a single publish row. > 1 means broker redelivery; ledger now gives this per-message resolution (the existing `FlowAccounting.DuplicatedFlows` was per-flow).
- **Cross-window crossings.** Publish window vs. consume window. A publish in `PreChaos` that consumes in `InRecovery` means the message survived the kill in a queue and was redelivered correctly.

## Report output

New section in `report.md`:

```markdown
## Message ledger

**Publishes:** 281,440 (acked 281,438 / failed 2)
**Consumes:** 281,425
**Acked-but-lost:** 13     ← most diagnostic count
**Failed-and-lost:** 2
**Per-message redeliveries:** 0

### Acked-but-lost breakdown by publish window

| Window | Count |
|---|---|
| PreChaos | 0 |
| DuringChaos | 0 |
| InRecovery | 13 |
| PostChaos | 0 |

### Acked-but-lost breakdown by pattern

| Pattern | Count |
|---|---|
| streaming | 8 |
| routing-slip | 2 |
| aggregator | 2 |
| scatter-gather | 1 |

### Acked-but-lost — first 20 forensic rows

| MessageId | FlowId | Pattern | OriginBus | PublishStarted | PublishWindow |
|---|---|---|---|---|---|
| 4e3f… | a7b1… | streaming | alpha | 16:24:44.512 | InRecovery |
| … | | | | | |
```

The numbers above are illustrative; actuals come from the run. The two sections that matter are the **publish-window breakdown** (discriminates publisher-side-during-kill vs. routing-state-during-recovery) and the **forensic table** (lets us hand-trace 20 examples in broker logs / network captures if needed).

The new section appears in the JSON report too, under `messageLedger`, with the full publish + consume arrays available for offline analysis.

## DI and instance sharing

Identical pattern to the seven existing `IFlowKeyedSingleton` accumulators:

- `services.AddSingleton<MessageLedger>(preConstructedInstance)` in both per-bus DI callbacks.
- `services.AddSingleton<IFlowKeyedSingleton>(sp => sp.GetRequiredService<MessageLedger>())` in both.
- Same pre-constructed instance shared across both `Bus` providers.
- `Program.cs` collects the ledger alongside the existing seven and passes them as the `IReadOnlyList<IFlowKeyedSingleton>` to `ModeDispatcher`.

The `LedgeredSender` wrapper takes the ledger and the inner `IBus` as constructor parameters. The wrap is applied in `HarnessHost.BuildServices` after the inner bus is constructed but before any driver resolves `IBus` from DI — drivers receive the wrap by default.

## Memory budget

Per publish: one `PublishRecord` (~96 bytes incl. dictionary overhead). Per consume: one `ConsumeRecord` (~80 bytes) plus the bag node (~32 bytes).

For a 5-min chaos soak with ~32k flows and ~3 average messages per flow: ~96k publishes + ~96k consumes = ~192k entries × ~100 B = **~19 MB**. Reclamation per tick (via `TryRemoveCompleted`) drops successful flows continuously; only failed/in-flight entries stay. End-of-run heap impact is well under the 50 MB assertion budget; with reclamation, steady-state is ~2-3 MB.

For 1-hour or 24-hour chaos runs the same per-tick reclamation keeps the ledger bounded; only retained failures grow, and those are the rows we want to keep.

## Test impact

- Unit tests for `MessageLedger`: record publish + ack, record publish + fail, record consume, snapshot, classification (each of the 4 quadrants), `TryRemoveCompleted` for successful flow ids, per-message duplicate count.
- Unit test for `MessageLedgerAnalyzer`: synthetic ledger → expected counts in each section.
- Integration test: smoke (28 flows, no chaos) — analyzer should report 0 lost, 0 duplicates, all `Normal`.
- Smoke / throughput / soak modes still green after wiring.
- The deliberate-fail sanity check (the chaos-induced loss we're investigating) should now produce a non-empty `acked-but-lost` section with a clear window/pattern distribution.

## Definition of done

- `MessageLedger` + `LedgeredSender` + `MessageLedgerAnalyzer` exist and have unit tests.
- Every harness publish flows through the wrap; every harness handler records a consume (via middleware or per-handler hook).
- New `## Message ledger` section appears in `report.md`; `messageLedger` block appears in `report.json`.
- 5-minute chaos soak reports a non-zero `acked-but-lost` count broken down by publish window and pattern, identifying which hypothesis to chase next.
- Smoke 28/28 still green; throughput 30s @ rate 50 still green; non-chaos soak still under memory budget.
- All existing tests pass; new tests pass.

## Risks

| Risk | Mitigation |
|---|---|
| `X-Stress-MessageId` header isn't propagated through every pattern (e.g. process-manager spawns its own internal messages without copying headers) | Unit test for each pattern verifies that the consume side sees the expected MessageId. If a pattern strips headers, the spec sees zero consumes for messages we *know* arrived, and we adjust the wrap point or accept that the pattern is opaque to per-message tracking (process-manager's internal messages can be excluded from the analyzer if needed). |
| Ledger memory unbounded if reclamation drifts | `IFlowKeyedSingleton.TryRemoveCompleted` follows the proven pattern; the existing 50 MB assertion catches regression. |
| Wrapping `IBus` breaks code paths that assume the concrete type or expose interfaces we don't implement | The wrapper proxies only the publish/send surface drivers use; everything else is delegated. Drivers consume the small `ISenderClient` shape (or whatever the existing DI registration uses), not the full `IBus`. If anything *does* require the full `IBus`, we register both: the raw bus on its native type, and the ledgered wrap on a separate interface that drivers inject. |
| Per-publish Guid generation is allocation-heavy at throughput=50 sustained for hours | One Guid + one dictionary insert per publish; the existing pattern (`FilterTrail`, `MiddlewareTrail`) does the same with no measurable cost in soak runs. |
| Consume hook misses messages that bypass the middleware (e.g. RPC reply path) | Plan should enumerate each pattern's consume entrypoints and verify they're covered. The `MessageLedgerAnalyzer` includes a sanity check: any flow id with consume rows but no publish rows is logged separately as "consume-without-publish" — that's a sign of a missed wrap point, not a broker bug. |
