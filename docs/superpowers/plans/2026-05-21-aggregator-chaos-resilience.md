# Aggregator Chaos Resilience Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate the four aggregator-pattern chaos-soak failures by extending the aggregator's flush timeout past the chaos downtime, relaxing the driver's batch-size assertion to the at-least-once contract, and adding a `BeforeConsuming` filter that closes the consume-side ledger instrumentation gap for aggregator items.

**Architecture:** Three discrete changes in the harness only. (1) `StressTelemetrySliceAggregator.Timeout()` returns 60 s (was 3 s) so the timer doesn't fire during a 20 s broker kill. (2) `AggregatorDriver`'s batch-size check accepts `[1, BatchSize]` instead of demanding exact `BatchSize` — at-least-once allows a flow's items to arrive across multiple partial batches. (3) A new `AggregatorLedgerFilter` (BeforeConsuming stage, gated by `StressHeaders.Pattern == "aggregator"`) reads `X-Stress-MessageId` from inbound headers and calls `MessageLedger.RecordConsume`, so the `AckedButLost.aggregator` count reflects real loss instead of framework-mediated noise.

**Tech Stack:** C# 14, .NET 10, xUnit, ServiceConnect's `IFilter` / `Envelope` pipeline surface, the harness's existing `MessageLedger` / `IChaosClock` / `StressHeaders` / `HeaderDecoder` infrastructure.

---

## File map

**Modified:**
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Aggregators/StressTelemetrySliceAggregator.cs` — change `Timeout()` from 3 s to 60 s.
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/AggregatorDriver.cs` — relax the batch-size check.
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Program.cs` — register the new filter (BeforeConsuming wire + DI factory).

**New:**
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Aggregators/AggregatorLedgerFilter.cs` — the filter implementation.
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/Patterns/Aggregators/AggregatorLedgerFilterTests.cs` — three unit tests covering record / pattern-gate / header-gap.

---

## Task 1: Lengthen aggregator flush timeout

**Files:**
- Modify: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Aggregators/StressTelemetrySliceAggregator.cs:37`
- Modify: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Aggregators/StressTelemetrySliceAggregator.cs:13-31` (the `<remarks>` xmldoc explaining the timeout choice — update the rationale).

- [ ] **Step 1: Change the `Timeout()` return value**

Replace line 37 of `StressTelemetrySliceAggregator.cs`:

```csharp
    public override TimeSpan Timeout() => TimeSpan.FromSeconds(60);
```

- [ ] **Step 2: Update the xmldoc rationale**

Replace the `<remarks>`'s first `<para>` (currently lines 14-23) with:

```csharp
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
```

- [ ] **Step 3: Build the harness**

Run: `dotnet build examples/StressHarness/src/ServiceConnect.Examples.StressHarness/ServiceConnect.Examples.StressHarness.csproj -m:1 -nologo`
Expected: `ok dotnet build: 10 projects, 0 errors, 0 warnings`.

- [ ] **Step 4: Run the harness tests**

Run: `dotnet test examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/ServiceConnect.Examples.StressHarness.Tests.csproj -m:1 -nologo`
Expected: all tests pass (no behavioural change in unit tests; the timeout is consumed at runtime).

- [ ] **Step 5: Commit**

```bash
git add examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Aggregators/StressTelemetrySliceAggregator.cs
git commit -m "fix(stress-harness): lengthen aggregator timeout to survive chaos downtime"
```

---

## Task 2: Relax the AggregatorDriver batch-size assertion

**Files:**
- Modify: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/AggregatorDriver.cs:87-91`

- [ ] **Step 1: Replace the batch-size check**

Locate the current check (around line 87 of `AggregatorDriver.cs`). Replace:

```csharp
            if (batch.Count != BatchSize)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"aggregator {context.Origin.ToHeaderValue()}->{receiverBusTag}: expected batch of {BatchSize} but observed {batch.Count}"));
            }
```

with:

```csharp
            // Under the at-least-once contract, the framework may dispatch a flow's
            // items across multiple partial batches if a broker kill fires the flush
            // timer before all items arrive. Any size in [1, BatchSize] is a valid
            // outcome; sizes above BatchSize would indicate the framework over-collected
            // and remain a failure.
            if (batch.Count < 1 || batch.Count > BatchSize)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"aggregator {context.Origin.ToHeaderValue()}->{receiverBusTag}: expected batch size in [1, {BatchSize}] but observed {batch.Count}"));
            }
```

- [ ] **Step 2: Build**

Run: `dotnet build examples/StressHarness/src/ServiceConnect.Examples.StressHarness/ServiceConnect.Examples.StressHarness.csproj -m:1 -nologo`
Expected: 0 errors, 0 warnings.

- [ ] **Step 3: Run tests**

Run: `dotnet test examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/ServiceConnect.Examples.StressHarness.Tests.csproj -m:1 -nologo`
Expected: all tests pass.

- [ ] **Step 4: Commit**

```bash
git add examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/AggregatorDriver.cs
git commit -m "fix(stress-harness): accept partial aggregator batches under at-least-once contract"
```

---

## Task 3: Implement `AggregatorLedgerFilter` (TDD)

**Files:**
- Create: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Aggregators/AggregatorLedgerFilter.cs`
- Create: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/Patterns/Aggregators/AggregatorLedgerFilterTests.cs`

### Step 3.1: Write the failing tests

Create `AggregatorLedgerFilterTests.cs`:

```csharp
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Chaos;
using ServiceConnect.Examples.StressHarness.Patterns;
using ServiceConnect.Examples.StressHarness.Patterns.Aggregators;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Patterns.Aggregators;

public sealed class AggregatorLedgerFilterTests
{
    [Fact]
    public async Task ProcessAsync_aggregator_envelope_with_stress_headers_records_consume()
    {
        var ledger = new MessageLedger();
        var clock = new FakeChaosClock(ChaosWindow.InRecovery);
        var filter = new AggregatorLedgerFilter("alpha", ledger, clock);

        var messageId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        var envelope = new Envelope
        {
            Headers = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [StressHeaders.Pattern] = "aggregator",
                [StressHeaders.FlowId] = flowId.ToString("N"),
                [StressHeaders.MessageId] = messageId.ToString("N"),
            },
        };

        var action = await filter.ProcessAsync(envelope);

        Assert.Equal(FilterAction.Continue, action);
        var snapshot = ledger.Snapshot();
        var row = Assert.Single(snapshot.Consumes);
        Assert.Equal(messageId, row.MessageId);
        Assert.Equal(flowId, row.FlowId);
        Assert.Equal("aggregator", row.Pattern);
        Assert.Equal("alpha", row.ConsumingBus);
        Assert.Equal(ChaosWindow.InRecovery, row.Window);
    }

    [Fact]
    public async Task ProcessAsync_non_aggregator_envelope_does_not_record_consume()
    {
        var ledger = new MessageLedger();
        var clock = new FakeChaosClock(ChaosWindow.PreChaos);
        var filter = new AggregatorLedgerFilter("alpha", ledger, clock);

        var envelope = new Envelope
        {
            Headers = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [StressHeaders.Pattern] = "p2p",
                [StressHeaders.FlowId] = Guid.NewGuid().ToString("N"),
                [StressHeaders.MessageId] = Guid.NewGuid().ToString("N"),
            },
        };

        var action = await filter.ProcessAsync(envelope);

        Assert.Equal(FilterAction.Continue, action);
        Assert.Empty(ledger.Snapshot().Consumes);
    }

    [Fact]
    public async Task ProcessAsync_aggregator_envelope_missing_MessageId_does_not_record_and_does_not_block()
    {
        var ledger = new MessageLedger();
        var clock = new FakeChaosClock(ChaosWindow.PreChaos);
        var filter = new AggregatorLedgerFilter("alpha", ledger, clock);

        var envelope = new Envelope
        {
            Headers = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [StressHeaders.Pattern] = "aggregator",
                [StressHeaders.FlowId] = Guid.NewGuid().ToString("N"),
                // MessageId absent
            },
        };

        var action = await filter.ProcessAsync(envelope);

        Assert.Equal(FilterAction.Continue, action);
        Assert.Empty(ledger.Snapshot().Consumes);
    }

    private sealed class FakeChaosClock(ChaosWindow window) : IChaosClock
    {
        public ChaosWindow CurrentWindow { get; } = window;
    }
}
```

### Step 3.2: Run tests to confirm they fail

Run: `dotnet test examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/ServiceConnect.Examples.StressHarness.Tests.csproj -m:1 --filter "FullyQualifiedName~AggregatorLedgerFilterTests" -nologo`

Expected: compilation fails — `AggregatorLedgerFilter` does not exist.

### Step 3.3: Implement `AggregatorLedgerFilter`

Create `AggregatorLedgerFilter.cs`:

```csharp
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Chaos;
using ServiceConnect.Examples.StressHarness.Patterns.Handlers;
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
```

### Step 3.4: Run tests to confirm they pass

Run: `dotnet test examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/ServiceConnect.Examples.StressHarness.Tests.csproj -m:1 --filter "FullyQualifiedName~AggregatorLedgerFilterTests" -nologo`

Expected: 3 tests pass.

### Step 3.5: Commit

```bash
git add examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Aggregators/AggregatorLedgerFilter.cs \
        examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/Patterns/Aggregators/AggregatorLedgerFilterTests.cs
git commit -m "feat(stress-harness): add AggregatorLedgerFilter for consume-side ledger coverage"
```

---

## Task 4: Wire the filter into `Program.cs`

**Files:**
- Modify: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Program.cs`

The harness's `registerPerBus` callback already wires `StressTrailFilter` and `StressBeforeFilter` via `builder.AddBeforeConsumingFilter<T>()` and DI factory registration. Mirror those exact patterns for `AggregatorLedgerFilter`.

### Step 4.1: Add the `AddBeforeConsumingFilter` call

Find the existing `builder.AddBeforeConsumingFilter<StressTrailFilter>();` line in `Program.cs` (around line 157). Add immediately after it:

```csharp
            builder.AddBeforeConsumingFilter<AggregatorLedgerFilter>();
```

The filter must register on BOTH buses; the `registerPerBus` callback is invoked once per bus so a single addition here covers both.

### Step 4.2: Add the DI factory

Find the existing `services.AddTransient<StressTrailFilter>(...)` registration (around line 205-206). Add immediately after it:

```csharp
                services.AddTransient<AggregatorLedgerFilter>(sp => new AggregatorLedgerFilter(
                    busTag,
                    sp.GetRequiredService<MessageLedger>(),
                    sp.GetRequiredService<IChaosClock>()));
```

The `busTag` is the closure variable from the surrounding `registerPerBus` callback (`"alpha"` / `"beta"`); `MessageLedger` and `IChaosClock` are both registered as singletons by `HarnessHost.BuildServices` (Task 6 of the chaos-message-ledger plan, commits `169a6268` + `4e0eb103`).

If the file needs a `using` for `ServiceConnect.Examples.StressHarness.Patterns.Aggregators` (the namespace where the new filter lives), add it at the top of the file. Most likely it's already present because `StressTelemetrySliceAggregator` is registered from the same namespace.

### Step 4.3: Build

Run: `dotnet build examples/StressHarness/src/ServiceConnect.Examples.StressHarness/ServiceConnect.Examples.StressHarness.csproj -m:1 -nologo`
Expected: 0 errors, 0 warnings.

### Step 4.4: Run the full harness test suite

Run: `dotnet test examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/ServiceConnect.Examples.StressHarness.Tests.csproj -m:1 -nologo`
Expected: all tests pass.

### Step 4.5: Commit

```bash
git add examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Program.cs
git commit -m "feat(stress-harness): register AggregatorLedgerFilter on both buses"
```

---

## Task 5: End-to-end verification via `./verify-all.sh`

**Files:** none modified.

### Step 5.1: Run unit tests + E2E tests together

Run from the repo root:
```bash
SKIP_HARNESS=1 SKIP_CHAOS=1 ./verify-all.sh
```
Expected: unit tests pass (1696 + 3 new = 1699 total or similar), E2E tests pass (136 total). The trailing summary should say `All requested verification stages passed.`.

### Step 5.2: Run the harness soak (no chaos)

Run from the repo root:
```bash
SKIP_UNIT=1 SKIP_E2E=1 SKIP_CHAOS=1 ./verify-all.sh
```
Expected: `Stress harness (no chaos) OK: **Flows:** N / N passed` where `N` matches the total number of flows executed in 5 minutes.

### Step 5.3: Run the chaos soak

Run from the repo root:
```bash
SKIP_UNIT=1 SKIP_E2E=1 SKIP_HARNESS=1 ./verify-all.sh
```

Expected: `Chaos harness OK: **Flows:** N / N passed`. The message-ledger section in `out/report.md` should report:
- `Acked-but-lost: 0` (or 0-1 — single-digit residual from broker-level redelivery races).
- `Failed-and-lost: 0`.
- The `Acked-but-lost breakdown by pattern` table should NOT show `aggregator` as the dominant contributor (and ideally should be absent entirely if the breakdown table only renders when AckedButLost > 0).

### Step 5.4: Verdict — variance check

The chaos soak's kill timings have natural jitter. A single clean run is necessary but not sufficient evidence. If you have time, run the chaos soak a second time:

```bash
SKIP_UNIT=1 SKIP_E2E=1 SKIP_HARNESS=1 ./verify-all.sh
```

Expected: also passes 0 aggregator failures. If either run fails, inspect `out/report.md` for the specific assertion + ledger pattern and report back — the work is not complete until two consecutive chaos runs are clean.

### Step 5.5: No commit

This task only verifies; no source change.

---

## Self-review

**1. Spec coverage:**

- "Lengthen aggregator timeout (60 s)" — Task 1 ✓
- "Relax driver batch-size assertion to [1, BatchSize]" — Task 2 ✓
- "Add `AggregatorLedgerFilter` with pattern-header gate" — Task 3 ✓
- "Three unit tests (record / pattern-gate / header-gap)" — Task 3 ✓
- "Wire the filter on both buses via existing `registerPerBus` pattern" — Task 4 ✓
- "End-to-end `./verify-all.sh` passes" — Task 5 ✓
- "Variance check across two chaos runs" — Task 5.4 ✓

**2. Placeholder scan:** No "TBD" / "TODO" entries. Step 4.2 contains a "If the file needs a `using`…" conditional — that's a verification step the implementer can resolve concretely by running the build (the compiler will name any missing namespace).

**3. Type consistency:**

- `AggregatorLedgerFilter` constructor signature `(string busTag, MessageLedger ledger, IChaosClock chaosClock)` matches across Task 3 (definition + tests) and Task 4 (DI factory). ✓
- `IFilter.ProcessAsync(Envelope, CancellationToken = default)` matches the framework's `IFilter` declaration. ✓
- `FilterAction.Continue` is the framework-provided enum value. ✓
- `Envelope.Headers` is `IDictionary<string, object>` — verified by reading `src/ServiceConnect.Interfaces/Messages/Envelope.cs`. ✓
- `HeaderDecoder.Decode(object?) → string?` matches the existing `StressTrailFilter`'s usage. ✓
- `StressHeaders.Pattern` / `.FlowId` / `.MessageId` all exist on the harness's `StressHeaders` static class. ✓
- `MessageLedger.RecordConsume(Guid, Guid, string, string, DateTimeOffset, ChaosWindow)` matches the signature defined in the chaos-message-ledger plan (Task 3 of that plan, commit `50980b1d`). ✓
- `IChaosClock.CurrentWindow` matches the interface defined in the chaos-message-ledger plan (Task 5 of that plan, commit `d279db13`). ✓

**4. Risk acknowledged in spec:**

- The spec flagged `Envelope.MessageTypeFullName` as a potential issue — verified during plan-time that `Envelope` has only `Headers` and `Body`. The filter uses the pattern-header gate (`StressHeaders.Pattern`) instead, which is cleaner anyway since it uses a header the harness owns. ✓
