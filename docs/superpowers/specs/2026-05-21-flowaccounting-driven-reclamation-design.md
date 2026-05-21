# FlowAccounting-driven `IFlowKeyedSingleton` reclamation — design spec

> **For agentic workers:** Design document. Implementation plan lives in `docs/superpowers/plans/2026-05-21-flowaccounting-driven-reclamation.md` (produced by `superpowers:writing-plans` after this spec is approved).

## Goal

Bring 30-minute MongoDB-persistence soak memory delta from ~430 MB back under the 256 MB budget by reclaiming `IFlowKeyedSingleton` per-flow rows for **every** flow id `FlowAccounting` knows is complete — not just the direction-level flow ids the dispatcher currently iterates. The dominant contributor is the saga (process-manager) pattern's sub-flow ids: 3 sub-flow ids per saga flow × 2 ledger entries (publish + consume) × ~12,000 saga flows in 30 minutes = ~72,000 unreclaimable rows.

## Context

The 30-minute Mongo soak (commit `27cc6e62`) passed 170,744 / 170,744 flows with a clean message ledger (`AckedButLost: 0`, `FailedAndLost: 0`) but tripped the memory assertion at 431 MB final vs 256 MB budget. The chaos message ledger snapshot showed 60,980 publishes and 60,980 consumes retained at end of run — these are predominantly saga sub-flow id rows that the per-tick reclamation pass never sweeps.

The root cause is the dispatcher's reclamation contract: it passes `directions.Where(d => d.Succeeded).Select(d => d.FlowId)` to `IFlowKeyedSingleton.TryRemoveCompleted` per tick. That set contains only the direction-level flow id (one per direction per driver invocation). Drivers that internally generate sub-flow ids (currently only `ProcessManagerDriver`, which mints `stage1Id`, `stage2Id`, `stage3Id` via `Guid.NewGuid()`) book those into `FlowAccounting.RecordSend` but never communicate them back to the dispatcher. The sub-flow ids appear in:

- `MessageLedger._publishes` (stamped by `LedgeredSender` from the outbound `X-Stress-FlowId` header).
- `MessageLedger._consumes` (recorded by `SagaHandler` via `LedgerHandlerHelpers.RecordLedgerConsume`).
- `PerHandlerSignal` (self-cleans via TCS-continuation — no leak).
- `FlowAccounting` (self-cleans via observed >= expected — no leak).

So the leak is confined to `MessageLedger`. But `FlowAccounting` already has the authoritative knowledge of which sub-flow ids are complete; the dispatcher just isn't asking.

Why this only surfaces under long-duration soaks: at 95 ticks/sec × 14 patterns the saga produces ~880 saga flows/min × 3 stages × 2 entries = ~5,280 leaked rows/min × ~250 bytes = ~1.3 MB/min from saga alone. The 5-minute soaks accumulate ~7 MB and stay well under the 256 MB budget; the 30-minute soak accumulates ~40 MB from saga and tips the budget when combined with everything else.

## Non-goals

- Adding tracking for every per-flow allocation in the harness. The targeted fix is sufficient; future patterns that introduce sub-flow ids should book them in `FlowAccounting` (as `ProcessManagerDriver` already does) and the fix's contract reclaims them automatically.
- Fixing `AggregatorObservations.Batches` (acknowledged deliberate accumulation, ~6 MB over 30 min, low priority).
- Optimising MongoDB driver / serializer caches. Likely contributes <10 MB; not the dominant source.
- Changing `MessageLedger`'s reclamation semantics for orphan rows (rows where the publish or consume side never recorded — those represent real diagnostic signal and should be retained as documented in the chaos-message-ledger spec).
- Changing any driver. Drivers that need sub-flow id tracking already use `FlowAccounting.RecordSend`; no new contract is imposed.

## Architecture

### Change 1 — `FlowAccounting.TryRemoveCompleted()` returns the reclaimed flow ids

`examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Assertions/FlowAccounting.cs`:

Current signature: `public void TryRemoveCompleted()`.

New signature: `public IReadOnlyList<Guid> TryRemoveCompleted()`.

Implementation: collect the flow ids it removes from `_expected` / `_observed` into a `List<Guid>`, return it. Callers that previously discarded the return value (none today — the call is fire-and-forget in the dispatch loops) keep working without changes.

### Change 2 — Dispatcher captures the accounting-reclaimed ids and includes them in the `IFlowKeyedSingleton` sweep

`examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Orchestrator/SoakLoop.cs` (around line 110-130):
`examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Orchestrator/ModeDispatcher.cs` (around line 95-110):
`examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Orchestrator/ThroughputLoop.cs` (around line 90-105):

Existing pattern:
```csharp
var completedIds = directions.Where(d => d.Succeeded).Select(d => d.FlowId).ToList();
if (completedIds.Count > 0)
{
    foreach (var singleton in _flowKeyedSingletons)
    {
        singleton.TryRemoveCompleted(completedIds);
    }
}
accounting.TryRemoveCompleted();
```

New pattern:
```csharp
var directionCompletedIds = directions.Where(d => d.Succeeded).Select(d => d.FlowId);
var accountingCompletedIds = accounting.TryRemoveCompleted();
var completedIds = directionCompletedIds.Concat(accountingCompletedIds).ToList();
if (completedIds.Count > 0)
{
    foreach (var singleton in _flowKeyedSingletons)
    {
        singleton.TryRemoveCompleted(completedIds);
    }
}
```

Notes:
- `accounting.TryRemoveCompleted()` runs FIRST so its returned ids are based on the latest observation counts.
- `Concat` preserves both sets — direction-level ids are still passed (for any singleton that records under the direction-level id and not the accounting key), accounting-completed ids cover the sub-flow case.
- Duplicates between the two sets are harmless: every `IFlowKeyedSingleton.TryRemoveCompleted` implementation is idempotent and tolerates unseen ids (per `Assertions/IFlowKeyedSingleton.cs` xmldoc).
- `ToList()` materialises once; the `foreach` iterates many times.

### Change 3 — None of the 8 existing `IFlowKeyedSingleton` implementations need to change

The contract is unchanged: receive `IEnumerable<Guid> completedFlowIds`, drop matching rows, no-op for ids the implementation never observed. All 8 implementations already satisfy this. The fix simply broadens the ids passed in.

## Testing

### Unit tests

Two changes:

1. **`FlowAccounting.TryRemoveCompleted()` returns the reclaimed ids.** Update existing `FlowAccountingTests` to assert the return value's contents. Existing tests that ignored the return value still work without modification (C# discards unused return values).

   Specific new assertion (add to an existing test or create one):
   ```csharp
   [Fact]
   public void TryRemoveCompleted_returns_ids_that_were_removed()
   {
       var accounting = new FlowAccounting();
       var done = Guid.NewGuid();
       var inflight = Guid.NewGuid();

       accounting.RecordSend(done, expectedHandlerInvocations: 1);
       accounting.RecordSend(inflight, expectedHandlerInvocations: 1);
       accounting.RecordHandled(done);

       var removed = accounting.TryRemoveCompleted();

       Assert.Single(removed);
       Assert.Contains(done, removed);
       Assert.DoesNotContain(inflight, removed);
   }
   ```

2. **No new tests for the dispatcher**. The merge logic is mechanical and the existing 5-min soaks (which already exercise the saga pattern) implicitly cover the integration via the verification step.

### Integration verification

After the fix, re-run the 30-minute Mongo soak:

```bash
dotnet run --no-build --project examples/StressHarness/src/ServiceConnect.Examples.StressHarness/ServiceConnect.Examples.StressHarness.csproj \
    -- --mode soak --duration 00:30:00 --rate 100 --persistence mongo --chaos none
```

Expected outcome:
- Flows: same as before (~170,000 / 170,000 passed).
- Memory delta: under the 256 MB budget (target: under 100 MB; comfortably bounded).
- Message ledger publishes + consumes count at end of run: under 5,000 each (mostly in-flight at snapshot time, no long-tail saga residue).

If memory delta remains above 256 MB after this fix, the remaining contributor is something other than saga sub-flow ids — likely `AggregatorObservations.Batches` or MongoDB driver overhead — and is addressed by a separate follow-up.

## Definition of done

- `FlowAccounting.TryRemoveCompleted()` returns `IReadOnlyList<Guid>`.
- `SoakLoop`, `ModeDispatcher`, `ThroughputLoop` capture the return value and concatenate it into the `IFlowKeyedSingleton` sweep.
- Unit test pins the return-value contract.
- All existing unit tests still pass (1700+ total).
- 30-minute Mongo soak completes under the 256 MB memory budget.
- Message-ledger snapshot at end of 30-min soak is bounded (no long-tail saga rows).

## Risks

| Risk | Mitigation |
|---|---|
| Changing `FlowAccounting.TryRemoveCompleted`'s return type breaks an unanticipated caller | The method is called from 3 sites (SoakLoop, ModeDispatcher, ThroughputLoop). All currently discard the return value (it was `void`). Changing to `IReadOnlyList<Guid>` is binary-compatible at the call site — C# silently ignores unused return values. Verify with a clean build. |
| The `Concat`-based merge allocates extra collections per tick | Each tick already allocates `directions`, `_flowKeyedSingletons` iteration, and the `completedIds` list. Adding one more `ToList()` call per tick is negligible (~50-100 bytes per tick, reclaimed by GC). |
| Concatenated duplicate ids cause extra work in each singleton's `TryRemove` | Each `IFlowKeyedSingleton.TryRemoveCompleted` already builds a `HashSet<Guid>` internally to dedupe its iteration. Duplicates in the input set collapse to one set entry. Net work: O(N) where N is the union. |
| The fix only covers sub-flow ids `FlowAccounting` knows about; a future driver that allocates sub-flow ids without calling `RecordSend` would still leak | Documented in the spec; the saga pattern already does the right thing. New patterns should mirror that contract. The fix is correct for every pattern in the current harness. |
| The 30-min soak verification is slow and chaos-jitter-sensitive | Memory growth is deterministic with no chaos enabled (this run is `--chaos none`). One clean run is sufficient evidence; the chaos resilience work has separate verification. |
