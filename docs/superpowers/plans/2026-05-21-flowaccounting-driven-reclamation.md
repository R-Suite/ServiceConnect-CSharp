# FlowAccounting-driven `IFlowKeyedSingleton` reclamation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reclaim saga sub-flow ids (and any future driver-side sub-flow ids tracked by `FlowAccounting`) from every `IFlowKeyedSingleton` so a 30-minute Mongo-persistence soak stays under the 256 MB memory budget.

**Architecture:** `FlowAccounting.TryRemoveCompleted` returns the list of flow ids it reclaimed in that pass. The three dispatch loops (`SoakLoop`, `ModeDispatcher`, `ThroughputLoop`) capture that list and concatenate it with the direction-level completed ids before sweeping the `IFlowKeyedSingleton` set. None of the 8 existing `IFlowKeyedSingleton` implementations need to change — they already tolerate ids they never observed.

**Tech Stack:** C# 14, .NET 10, xUnit. Self-contained to the harness — no framework changes.

---

## File map

**Modified:**
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Assertions/FlowAccounting.cs` — change `TryRemoveCompleted` return type from `void` to `IReadOnlyList<Guid>`.
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Orchestrator/SoakLoop.cs` — capture the return value and concat into the `IFlowKeyedSingleton` sweep.
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Orchestrator/ThroughputLoop.cs` — same pattern.
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Orchestrator/ModeDispatcher.cs` — add the same pattern (smoke mode currently doesn't call `accounting.TryRemoveCompleted` per tick; the addition is consistent with the soak/throughput loops and harmless for a single-tick smoke run).
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/Assertions/FlowAccountingTests.cs` — add one test that pins the return value contract.

**Verification only:**
- `out/report.md` — re-run the 30-min Mongo soak.

---

## Task 1: Change `FlowAccounting.TryRemoveCompleted` to return the reclaimed ids + test

**File:** `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Assertions/FlowAccounting.cs` (lines 41-52).

### Step 1.1: Update the method signature and body

Replace the existing method (currently lines 41-52):

```csharp
    /// <summary>
    /// Drops the bookkeeping for every flow whose observed handler invocations have caught
    /// up with the expected count. Long-running loops call this once per tick so the two
    /// dictionaries stay bounded by the in-flight set rather than the lifetime-cumulative
    /// set. Flows still short of their expected fan-out are left in place so the next
    /// <see cref="Reconcile"/> still reports them as missing.
    /// </summary>
    /// <returns>
    /// The flow ids that were reclaimed in this pass. Callers (the dispatch loops) feed
    /// this list into every <see cref="IFlowKeyedSingleton.TryRemoveCompleted"/> so
    /// per-flow rows recorded against driver-side sub-flow ids (e.g. saga stage ids)
    /// are reclaimed alongside the direction-level ids the dispatcher already passes.
    /// </returns>
    public IReadOnlyList<Guid> TryRemoveCompleted()
    {
        var removed = new List<Guid>();
        foreach (var kv in _expected)
        {
            var observed = _observed.GetValueOrDefault(kv.Key, 0);
            if (observed >= kv.Value)
            {
                if (_expected.TryRemove(kv.Key, out _))
                {
                    _observed.TryRemove(kv.Key, out _);
                    removed.Add(kv.Key);
                }
            }
        }
        return removed;
    }
```

The `if (_expected.TryRemove(...))` guard ensures we only add the id to `removed` when this caller actually performed the removal (not a concurrent racer) — keeps the contract crisp.

### Step 1.2: Build

Run: `dotnet build examples/StressHarness/src/ServiceConnect.Examples.StressHarness/ServiceConnect.Examples.StressHarness.csproj -m:1 -nologo`
Expected: 0 errors, 0 warnings. (Two existing callers — `SoakLoop.cs:134` and `ThroughputLoop.cs:99` — currently discard the void return; the signature change is binary-compatible since C# silently discards unused return values.)

### Step 1.3: Add a test pinning the return-value contract

**File:** `examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/Assertions/FlowAccountingTests.cs`.

Find the existing `TryRemoveCompleted` test (around line 130 — there should be one). Append a new `[Fact]` method after the existing tests in the class:

```csharp
    [Fact]
    public void TryRemoveCompleted_ReturnsIdsOfReclaimedFlows()
    {
        var acct = new FlowAccounting();
        var done = Guid.NewGuid();
        var inflight = Guid.NewGuid();

        acct.RecordSend(done, expectedHandlerInvocations: 1);
        acct.RecordSend(inflight, expectedHandlerInvocations: 1);
        acct.RecordHandled(done);

        var removed = acct.TryRemoveCompleted();

        Assert.Single(removed);
        Assert.Contains(done, removed);
        Assert.DoesNotContain(inflight, removed);
    }
```

The existing test (`acct.TryRemoveCompleted();` with discarded return) keeps working unchanged.

### Step 1.4: Run the test suite

Run: `dotnet test examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/ServiceConnect.Examples.StressHarness.Tests.csproj -m:1 -nologo`
Expected: all tests pass (previous count + 1 new test).

### Step 1.5: Commit

```bash
git add examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Assertions/FlowAccounting.cs \
        examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/Assertions/FlowAccountingTests.cs
git commit -m "feat(stress-harness): FlowAccounting.TryRemoveCompleted returns reclaimed flow ids"
```

---

## Task 2: Wire the reclaimed ids into all three dispatch loops

### Step 2.1: `SoakLoop.cs`

**File:** `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Orchestrator/SoakLoop.cs`.

Find the existing reclamation block (around lines 120-134). It currently looks like:

```csharp
                        var completedIds = directions.Where(d => d.Succeeded).Select(d => d.FlowId).ToList();
                        if (completedIds.Count > 0)
                        {
                            foreach (var singleton in _flowKeyedSingletons)
                            {
                                singleton.TryRemoveCompleted(completedIds);
                            }
                        }
                    }

                    // The end-of-run Reconcile() still sees any under-handled flows because
                    // TryRemoveCompleted only evicts rows where observed >= expected.
                    accounting.TryRemoveCompleted();
```

Replace with:

```csharp
                        // FlowAccounting tracks every flow id the drivers booked via RecordSend —
                        // including driver-side sub-flow ids (e.g. saga stage ids) that the
                        // direction-level d.FlowId never covers. Concatenating the accounting-
                        // reclaimed ids ensures every IFlowKeyedSingleton sees the full set; the
                        // singletons' TryRemoveCompleted implementations are idempotent and
                        // tolerate ids they never observed, so duplicates are harmless.
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
                    }
```

(Note the existing trailing `accounting.TryRemoveCompleted();` line is removed — its call moved inside the reclamation block. The accounting reclamation now happens BEFORE the singleton sweep so the singleton sweep sees the fresh list.)

### Step 2.2: `ThroughputLoop.cs`

**File:** `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Orchestrator/ThroughputLoop.cs`.

Read the file around line 80-105 to find the analogous reclamation block. It will look similar to SoakLoop's pattern: a `completedIds` list built from `directions.Where(d => d.Succeeded)` followed by a `_flowKeyedSingletons` sweep, and a separate `accounting.TryRemoveCompleted();` call at line 99.

Apply the same transformation as Step 2.1:

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

Remove the standalone `accounting.TryRemoveCompleted();` call (its work is now done inside the new block).

### Step 2.3: `ModeDispatcher.cs`

**File:** `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Orchestrator/ModeDispatcher.cs`.

Find the existing reclamation block (around lines 103-111). Currently:

```csharp
            var completedIds = directions.Where(d => d.Succeeded).Select(d => d.FlowId).ToList();
            if (completedIds.Count > 0)
            {
                foreach (var singleton in _flowKeyedSingletons)
                {
                    singleton.TryRemoveCompleted(completedIds);
                }
            }
```

`ModeDispatcher` (smoke mode) does NOT currently call `accounting.TryRemoveCompleted()`. Add it now, mirroring the pattern:

```csharp
            // Smoke mode runs a single tick across every pattern; reclaim alongside the
            // soak / throughput loops so a smoke run that exercises the saga pattern also
            // sweeps the sub-flow ids before Reconcile inspects what's left.
            var directionCompletedIds = directions.Where(d => d.Succeeded).Select(d => d.FlowId);
            var accountingCompletedIds = _accounting.TryRemoveCompleted();
            var completedIds = directionCompletedIds.Concat(accountingCompletedIds).ToList();
            if (completedIds.Count > 0)
            {
                foreach (var singleton in _flowKeyedSingletons)
                {
                    singleton.TryRemoveCompleted(completedIds);
                }
            }
```

> Note: the field name is `_accounting` in `ModeDispatcher` (verify by reading line ~30 of the file). If the field uses a different name (e.g. `accounting`), adapt.

> Important — `Reconcile()` at line 114 still runs after the reclamation. Verify that `TryRemoveCompleted` only reclaims fully-handled flows (`observed >= expected`); `Reconcile()` only reports under-handled (`observed < expected`) and over-handled (`observed > expected`) flows. The two sets are disjoint, so reclaiming the complete set before reconciling is safe — the missing/duplicate findings are unchanged.

### Step 2.4: Build the harness

Run: `dotnet build examples/StressHarness/src/ServiceConnect.Examples.StressHarness/ServiceConnect.Examples.StressHarness.csproj -m:1 -nologo`
Expected: 0 errors, 0 warnings.

### Step 2.5: Run the full harness test suite

Run: `dotnet test examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/ServiceConnect.Examples.StressHarness.Tests.csproj -m:1 -nologo`
Expected: all tests pass (previous count + 1 from Task 1).

### Step 2.6: Commit

```bash
git add examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Orchestrator/SoakLoop.cs \
        examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Orchestrator/ThroughputLoop.cs \
        examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Orchestrator/ModeDispatcher.cs
git commit -m "feat(stress-harness): reclaim sub-flow ids via FlowAccounting-driven sweep"
```

---

## Task 3: 30-minute Mongo soak verification

**Files:** none modified.

### Step 3.1: Pre-build the harness

Run: `dotnet build examples/StressHarness/src/ServiceConnect.Examples.StressHarness/ServiceConnect.Examples.StressHarness.csproj -m:1 -nologo`
Expected: 0 errors, 0 warnings.

### Step 3.2: Bring up the broker and Mongo

Run: `docker compose -f examples/StressHarness/docker-compose.yml -p stress-harness up -d --wait --wait-timeout 120`
Expected: both `stress-harness-rabbit` and `stress-harness-mongo` report Healthy.

### Step 3.3: Run the 30-minute Mongo soak

Run (in foreground or background; the wall-clock is ~30 minutes plus a few seconds of teardown):

```bash
dotnet run --no-build --project examples/StressHarness/src/ServiceConnect.Examples.StressHarness/ServiceConnect.Examples.StressHarness.csproj \
    -- --mode soak --duration 00:30:00 --rate 100 --persistence mongo --chaos none
```

Expected:
- Summary line: `Flows: N / N passed` with N matching the total ticks × patterns × directions (~170,000).
- Summary line: `Memory: baseline ~1 MB → final ≤ 256 MB` (target: well under the budget, ideally < 100 MB).
- Summary line: `All assertions passed.` (no `[process] memory delta ... exceeded budget` failure).

### Step 3.4: Read the message-ledger residual

Run: `awk '/## Message ledger/,0' out/report.md`

Expected:
- `Publishes:` count is small (< 5,000 — mostly in-flight at snapshot time, no long-tail saga sub-flow id residue).
- `Consumes:` count similarly small.
- `Acked-but-lost: 0`, `Failed-and-lost: 0`.

### Step 3.5: Tear down the broker

Run: `docker compose -f examples/StressHarness/docker-compose.yml -p stress-harness down --remove-orphans`

### Step 3.6: Capture verdict (no commit)

Summarise the run in conversation: memory delta vs budget, flow pass count, message-ledger snapshot size. If memory delta is under budget, the fix is verified. If still over budget but the message-ledger snapshot is bounded, the remaining contributor is elsewhere (MongoDB driver, AggregatorObservations.Batches, etc.) and gets a separate spec.

---

## Self-review

**1. Spec coverage:**

- "Change `FlowAccounting.TryRemoveCompleted` to return `IReadOnlyList<Guid>`" — Task 1.1 ✓
- "Dispatcher captures the return value and includes it in the IFlowKeyedSingleton sweep" — Tasks 2.1, 2.2, 2.3 ✓
- "No changes to the 8 IFlowKeyedSingleton implementations" — Confirmed: their contracts are unchanged ✓
- "Unit test pins the return value contract" — Task 1.3 ✓
- "30-min Mongo soak verifies the budget holds" — Task 3 ✓

**2. Placeholder scan:** No "TBD" / "TODO" entries. Step 2.3 contains a "verify the field name" note — the implementer resolves this concretely by reading the file at edit time; not a placeholder.

**3. Type consistency:**

- `IReadOnlyList<Guid>` returned by `TryRemoveCompleted` is the same type used by the dispatcher's concat target. ✓
- `directions.Where(d => d.Succeeded).Select(d => d.FlowId)` is `IEnumerable<Guid>`; `.Concat()` with another `IEnumerable<Guid>` yields `IEnumerable<Guid>`; `.ToList()` yields `List<Guid>` which is `IReadOnlyList<Guid>`. All consistent with the existing `IFlowKeyedSingleton.TryRemoveCompleted(IEnumerable<Guid>)` signature. ✓
- `_accounting` vs `accounting` field/parameter naming — verified during plan-time that `ModeDispatcher` uses `_accounting` (field on the dispatcher class) and `SoakLoop` / `ThroughputLoop` use `accounting` (parameter). The plan instructs the implementer to confirm and adapt. ✓
- `_flowKeyedSingletons` field name — used consistently across all three dispatchers (already established by the chaos-message-ledger plan's Task 6). ✓
