# Chaos Message Ledger Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `MessageLedger` to the stress harness that records every publish + every consume with `(MessageId, FlowId, Pattern, OriginBus, ChaosWindow, Timestamp, Outcome)`, classifies missing messages into acked-but-lost vs failed-and-lost via a `MessageLedgerAnalyzer`, and renders a new `## Message ledger` section into `report.md` / `report.json` so the next 5-minute chaos soak identifies which loss mechanism is responsible for the 16 unhandled flows per run.

**Architecture:** A new `MessageLedger` flow-keyed singleton (sibling of the seven existing accumulators in `Assertions/`) records publishes via a thin `LedgeredSender` `IBus` wrap installed in `HarnessHost.BuildServices`, and records consumes via a handler-side hook added to every `IMessageHandler` in `Patterns/Handlers/`. A new `MessageLedgerAnalyzer` produces the post-run analysis; new fields on the `Report` model carry it through to both writers. Reclamation uses the existing `IFlowKeyedSingleton.TryRemoveCompleted` contract so soak memory stays bounded.

**Tech Stack:** C# 14, .NET 10, xUnit, FluentAssertions (already used by `MessageLedgerAccumulator` peers). ServiceConnect handlers (`IMessageHandler<T>`) and bus surfaces (`IBus.SendAsync` / `IBus.PublishAsync` / `IBus.SendRequestAsync`).

---

## Spec scope adjustment — read first

The design spec at `docs/superpowers/specs/2026-05-20-chaos-message-ledger-design.md` describes the wrap as covering "every harness publish". Two harness publish surfaces do NOT accept caller-controlled headers and therefore cannot carry an `X-Stress-MessageId` from the harness layer:

1. **`IBus.RouteAsync(message, destinations, ct)`** — used by `RoutingSlipDriver`. No `SendOptions` overload exists, so the harness cannot stamp a per-message id on hop 1 publishes. The hop-2 forward is issued by the framework's `HandlerProcessor.ForwardRoutingSlipAsync` and is wholly opaque to the harness.

2. **`IBus.CreateStream<T>(endpoint)` + `stream.WriteAsync(memory, ct)`** — used by `StreamingDriver`. The chunk-level `BasicPublish` frames are emitted by the framework's `StreamProcessor`; the harness sees only the high-level `CreateStream` / `WriteAsync` calls, not the per-chunk publishes.

The plan implements per-message ledger coverage for the **twelve patterns that pass `SendOptions` / `PublishOptions` / `RequestOptions`** (p2p, pubsub, request-reply, competing-consumers, content-based-routing, polymorphic, filters, process-manager, aggregator, scatter-gather request, custom-filter-middleware, telemetry). For routing-slip and streaming the ledger records one row per harness-issued call (`RouteAsync` / `CreateStream` open) using the message's `CorrelationId` as the message id, so a complete-flow loss is still observable. Per-hop / per-chunk granularity for those two patterns requires framework instrumentation and is **out of scope here** — call it out in the Risks section of the report and add a follow-up plan if the analyser indicates the loss is concentrated in framework-mediated publishes.

This adjustment preserves the spec's diagnostic intent: if `acked-but-lost` lights up in any of the twelve fully-instrumented patterns, hypothesis H1 (publisher-side ack-before-fsync) is confirmed and we have a fix target. If it does not, and the missing flows remain concentrated in streaming + routing-slip forward hops, the next investigation must be at the framework layer.

---

## File map

**New files (production):**

- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Assertions/PublishOutcome.cs` — `enum { Acked, Failed }`.
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Assertions/PublishRecord.cs` — readonly struct holding one publish row.
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Assertions/ConsumeRecord.cs` — readonly struct holding one consume row.
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Assertions/LedgerSnapshot.cs` — record bundling publishes + consumes for analysis.
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Assertions/MessageLedger.cs` — the singleton.
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Assertions/MessageLedgerAnalysis.cs` — post-run analysis result (quadrant counts, breakdowns, forensic rows).
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Assertions/MessageLedgerAnalyzer.cs` — pure function from `LedgerSnapshot` to `MessageLedgerAnalysis`.
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Orchestrator/LedgeredSender.cs` — `IBus` proxy that wraps `SendAsync` / `PublishAsync` / `SendRequestAsync` / `RouteAsync` / `CreateStream` and writes publish rows into the ledger.

**New files (tests):**

- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/Assertions/MessageLedgerTests.cs`
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/Assertions/MessageLedgerAnalyzerTests.cs`
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/Orchestrator/LedgeredSenderTests.cs`
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/Reporting/MessageLedgerReportTests.cs`

**Modified (production):**

- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/StressHeaders.cs` — add `MessageId` constant.
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Orchestrator/HarnessHost.cs` — register `MessageLedger` and decorate the resolved `IBus` with `LedgeredSender`.
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/P2pHandler.cs`
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/PubSubHandler.cs`
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/QuoteRequestHandler.cs` (request-reply)
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/WorkItemHandler.cs` (competing-consumers)
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/PremiumOrderHandler.cs` + `StandardOrderHandler.cs` (content-based)
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/DomainEventHandler.cs` (polymorphic)
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/FilteredMessageHandler.cs` (filters)
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/SagaHandler.cs` (process-manager)
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/SearchRequestHandler.cs` (scatter-gather)
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/SlipOrderHandler.cs` (routing-slip)
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/DocumentUploadedHandler.cs` (streaming) — uses `CorrelationId` because the stream path strips harness headers.
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/DedupedMessageHandler.cs` (custom-filter-middleware)
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/TracedEventHandler.cs` (telemetry)
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Reporting/Report.cs` — add `MessageLedgerAnalysis? MessageLedger` slot.
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Reporting/MarkdownReportWriter.cs` — render new section.
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Reporting/JsonReportWriter.cs` — round-trip the new field.
- `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Orchestrator/ModeDispatcher.cs` (or wherever `Report` is built) — call `MessageLedgerAnalyzer.Analyze(ledger.Snapshot())` and attach.

---

## Task 1: Add the `MessageId` header constant

**Files:**
- Modify: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/StressHeaders.cs`

- [ ] **Step 1: Add the constant**

Replace the existing class body in `StressHeaders.cs` with:

```csharp
public static class StressHeaders
{
    public const string FlowId = "X-Stress-FlowId";
    public const string OriginBus = "X-Stress-Origin-Bus";
    public const string Pattern = "X-Stress-Pattern";
    public const string MessageId = "X-Stress-MessageId";
}
```

- [ ] **Step 2: Build the harness project**

Run: `dotnet build examples/StressHarness/src/ServiceConnect.Examples.StressHarness/ServiceConnect.Examples.StressHarness.csproj -m:1 -nologo`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/StressHeaders.cs
git commit -m "chore(stress-harness): introduce X-Stress-MessageId header constant"
```

---

## Task 2: Add ledger value types

**Files:**
- Create: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Assertions/PublishOutcome.cs`
- Create: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Assertions/PublishRecord.cs`
- Create: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Assertions/ConsumeRecord.cs`
- Create: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Assertions/LedgerSnapshot.cs`

- [ ] **Step 1: Create `PublishOutcome.cs`**

```csharp
namespace ServiceConnect.Examples.StressHarness.Assertions;

public enum PublishOutcome
{
    Acked,
    Failed,
}
```

- [ ] **Step 2: Create `PublishRecord.cs`**

```csharp
using ServiceConnect.Examples.StressHarness.Chaos;

namespace ServiceConnect.Examples.StressHarness.Assertions;

public readonly record struct PublishRecord(
    Guid MessageId,
    Guid FlowId,
    string Pattern,
    string OriginBus,
    DateTimeOffset PublishStarted,
    DateTimeOffset PublishCompleted,
    PublishOutcome Outcome,
    ChaosWindow Window);
```

- [ ] **Step 3: Create `ConsumeRecord.cs`**

```csharp
using ServiceConnect.Examples.StressHarness.Chaos;

namespace ServiceConnect.Examples.StressHarness.Assertions;

public readonly record struct ConsumeRecord(
    Guid MessageId,
    Guid FlowId,
    string Pattern,
    string ConsumingBus,
    DateTimeOffset Consumed,
    ChaosWindow Window);
```

- [ ] **Step 4: Create `LedgerSnapshot.cs`**

```csharp
namespace ServiceConnect.Examples.StressHarness.Assertions;

public sealed record LedgerSnapshot(
    IReadOnlyList<PublishRecord> Publishes,
    IReadOnlyList<ConsumeRecord> Consumes);
```

- [ ] **Step 5: Build**

Run: `dotnet build examples/StressHarness/src/ServiceConnect.Examples.StressHarness/ServiceConnect.Examples.StressHarness.csproj -m:1 -nologo`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Assertions/PublishOutcome.cs \
        examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Assertions/PublishRecord.cs \
        examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Assertions/ConsumeRecord.cs \
        examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Assertions/LedgerSnapshot.cs
git commit -m "feat(stress-harness): add MessageLedger value types"
```

---

## Task 3: Implement `MessageLedger` (TDD)

**Files:**
- Create: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Assertions/MessageLedger.cs`
- Create: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/Assertions/MessageLedgerTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `MessageLedgerTests.cs`:

```csharp
using FluentAssertions;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Chaos;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Assertions;

public sealed class MessageLedgerTests
{
    [Fact]
    public void RecordPublishStart_then_RecordPublishCompleted_appends_one_publish_row()
    {
        var ledger = new MessageLedger();
        var msgId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;

        ledger.RecordPublishStart(msgId, flowId, pattern: "p2p", originBus: "alpha", started: t0, window: ChaosWindow.PreChaos);
        ledger.RecordPublishCompleted(msgId, completed: t0.AddMilliseconds(2), outcome: PublishOutcome.Acked);

        var snapshot = ledger.Snapshot();
        snapshot.Publishes.Should().ContainSingle();
        var row = snapshot.Publishes[0];
        row.MessageId.Should().Be(msgId);
        row.FlowId.Should().Be(flowId);
        row.Pattern.Should().Be("p2p");
        row.OriginBus.Should().Be("alpha");
        row.PublishStarted.Should().Be(t0);
        row.PublishCompleted.Should().Be(t0.AddMilliseconds(2));
        row.Outcome.Should().Be(PublishOutcome.Acked);
        row.Window.Should().Be(ChaosWindow.PreChaos);
    }

    [Fact]
    public void RecordConsume_appends_one_consume_row()
    {
        var ledger = new MessageLedger();
        var msgId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        var ts = DateTimeOffset.UtcNow;

        ledger.RecordConsume(msgId, flowId, pattern: "p2p", consumingBus: "beta", consumed: ts, window: ChaosWindow.InRecovery);

        var snapshot = ledger.Snapshot();
        snapshot.Consumes.Should().ContainSingle();
        var row = snapshot.Consumes[0];
        row.MessageId.Should().Be(msgId);
        row.FlowId.Should().Be(flowId);
        row.Pattern.Should().Be("p2p");
        row.ConsumingBus.Should().Be("beta");
        row.Consumed.Should().Be(ts);
        row.Window.Should().Be(ChaosWindow.InRecovery);
    }

    [Fact]
    public void RecordConsume_supports_multiple_rows_per_message_id()
    {
        var ledger = new MessageLedger();
        var msgId = Guid.NewGuid();
        var flowId = Guid.NewGuid();

        ledger.RecordConsume(msgId, flowId, "p2p", "alpha", DateTimeOffset.UtcNow, ChaosWindow.PreChaos);
        ledger.RecordConsume(msgId, flowId, "p2p", "alpha", DateTimeOffset.UtcNow.AddMilliseconds(50), ChaosWindow.PreChaos);

        var snapshot = ledger.Snapshot();
        snapshot.Consumes.Count.Should().Be(2);
        snapshot.Consumes.Should().AllSatisfy(r => r.MessageId.Should().Be(msgId));
    }

    [Fact]
    public void RecordPublishCompleted_for_unknown_message_id_is_a_noop()
    {
        var ledger = new MessageLedger();
        var act = () => ledger.RecordPublishCompleted(Guid.NewGuid(), DateTimeOffset.UtcNow, PublishOutcome.Acked);
        act.Should().NotThrow();
        ledger.Snapshot().Publishes.Should().BeEmpty();
    }

    [Fact]
    public void TryRemoveCompleted_drops_publish_and_consume_rows_for_listed_flow_ids()
    {
        var ledger = new MessageLedger();
        var keepFlow = Guid.NewGuid();
        var dropFlow = Guid.NewGuid();
        var keepMsg = Guid.NewGuid();
        var dropMsg = Guid.NewGuid();

        ledger.RecordPublishStart(keepMsg, keepFlow, "p2p", "alpha", DateTimeOffset.UtcNow, ChaosWindow.PreChaos);
        ledger.RecordPublishCompleted(keepMsg, DateTimeOffset.UtcNow, PublishOutcome.Acked);
        ledger.RecordConsume(keepMsg, keepFlow, "p2p", "beta", DateTimeOffset.UtcNow, ChaosWindow.PreChaos);

        ledger.RecordPublishStart(dropMsg, dropFlow, "p2p", "alpha", DateTimeOffset.UtcNow, ChaosWindow.PreChaos);
        ledger.RecordPublishCompleted(dropMsg, DateTimeOffset.UtcNow, PublishOutcome.Acked);
        ledger.RecordConsume(dropMsg, dropFlow, "p2p", "beta", DateTimeOffset.UtcNow, ChaosWindow.PreChaos);

        ledger.TryRemoveCompleted([dropFlow]);

        var snapshot = ledger.Snapshot();
        snapshot.Publishes.Should().ContainSingle().Which.FlowId.Should().Be(keepFlow);
        snapshot.Consumes.Should().ContainSingle().Which.FlowId.Should().Be(keepFlow);
    }

    [Fact]
    public void TryRemoveCompleted_for_unseen_flow_ids_is_a_noop()
    {
        var ledger = new MessageLedger();
        var act = () => ledger.TryRemoveCompleted([Guid.NewGuid(), Guid.NewGuid()]);
        act.Should().NotThrow();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/ServiceConnect.Examples.StressHarness.Tests.csproj -m:1 --filter "FullyQualifiedName~MessageLedgerTests" -nologo`
Expected: Compilation fails — `MessageLedger` does not exist.

- [ ] **Step 3: Implement `MessageLedger`**

Create `MessageLedger.cs`:

```csharp
using System.Collections.Concurrent;
using ServiceConnect.Examples.StressHarness.Chaos;

namespace ServiceConnect.Examples.StressHarness.Assertions;

/// <summary>
/// Per-message publish + consume accumulator. The harness wraps every header-bearing
/// publish surface (<see cref="LedgeredSender"/>) and records one publish row;
/// inbound handlers call <see cref="RecordConsume"/> once per dispatch. The post-run
/// <see cref="MessageLedgerAnalyzer"/> cross-references publishes and consumes by
/// <see cref="PublishRecord.MessageId"/> to classify each publish into one of four
/// quadrants — see the spec for the diagnostic intent.
/// </summary>
/// <remarks>
/// Thread-safe; concurrent recorders allowed. Implements
/// <see cref="IFlowKeyedSingleton"/> so the dispatcher can reclaim per-flow rows
/// after each tick — the same memory-bounding pattern used by the other seven
/// flow-keyed singletons. Failed and unmatched publishes are never reclaimed: their
/// detail must survive into the report.
/// </remarks>
public sealed class MessageLedger : IFlowKeyedSingleton
{
    private readonly ConcurrentDictionary<Guid, PublishState> _publishes = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentBag<ConsumeRecord>> _consumes = new();

    private sealed record PublishState(
        Guid FlowId,
        string Pattern,
        string OriginBus,
        DateTimeOffset Started,
        ChaosWindow Window,
        DateTimeOffset? Completed,
        PublishOutcome? Outcome);

    public void RecordPublishStart(
        Guid messageId,
        Guid flowId,
        string pattern,
        string originBus,
        DateTimeOffset started,
        ChaosWindow window)
    {
        _publishes[messageId] = new PublishState(flowId, pattern, originBus, started, window, Completed: null, Outcome: null);
    }

    public void RecordPublishCompleted(Guid messageId, DateTimeOffset completed, PublishOutcome outcome)
    {
        _publishes.AddOrUpdate(
            messageId,
            addValueFactory: _ => throw new InvalidOperationException(
                $"RecordPublishCompleted called for unknown MessageId {messageId:N}; RecordPublishStart must precede it."),
            updateValueFactory: (_, prev) => prev with { Completed = completed, Outcome = outcome });
    }

    public void RecordConsume(
        Guid messageId,
        Guid flowId,
        string pattern,
        string consumingBus,
        DateTimeOffset consumed,
        ChaosWindow window)
    {
        var bag = _consumes.GetOrAdd(messageId, _ => []);
        bag.Add(new ConsumeRecord(messageId, flowId, pattern, consumingBus, consumed, window));
    }

    public LedgerSnapshot Snapshot()
    {
        var publishes = new List<PublishRecord>(_publishes.Count);
        foreach (var (messageId, state) in _publishes)
        {
            if (state.Completed is null || state.Outcome is null)
            {
                continue;
            }
            publishes.Add(new PublishRecord(
                messageId,
                state.FlowId,
                state.Pattern,
                state.OriginBus,
                state.Started,
                state.Completed.Value,
                state.Outcome.Value,
                state.Window));
        }

        var consumes = new List<ConsumeRecord>(_consumes.Count);
        foreach (var (_, bag) in _consumes)
        {
            consumes.AddRange(bag);
        }

        return new LedgerSnapshot(publishes, consumes);
    }

    public void TryRemoveCompleted(IEnumerable<Guid> completedFlowIds)
    {
        var completedSet = new HashSet<Guid>(completedFlowIds);
        if (completedSet.Count == 0) return;

        foreach (var (messageId, state) in _publishes)
        {
            if (completedSet.Contains(state.FlowId))
            {
                _publishes.TryRemove(messageId, out _);
            }
        }

        foreach (var (messageId, bag) in _consumes)
        {
            if (bag.IsEmpty)
            {
                continue;
            }
            var sampleFlowId = bag.First().FlowId;
            if (completedSet.Contains(sampleFlowId))
            {
                _consumes.TryRemove(messageId, out _);
            }
        }
    }
}
```

- [ ] **Step 4: One important amendment — the `RecordPublishCompleted` exception is too strict for the wrap**

The wrapper records start, awaits the publish, then records completion. If an aborted task races the wrap, the wrapper still calls `RecordPublishCompleted` on its own message id which it just recorded. So the `addValueFactory` exception path is unreachable in practice, but defensive against developer mistake. Leave it as-is.

- [ ] **Step 5: Adjust the second test to account for `RecordPublishCompleted` throwing**

Replace the `RecordPublishCompleted_for_unknown_message_id_is_a_noop` test in `MessageLedgerTests.cs` with:

```csharp
[Fact]
public void RecordPublishCompleted_without_RecordPublishStart_throws()
{
    var ledger = new MessageLedger();
    var act = () => ledger.RecordPublishCompleted(Guid.NewGuid(), DateTimeOffset.UtcNow, PublishOutcome.Acked);
    act.Should().Throw<InvalidOperationException>();
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/ServiceConnect.Examples.StressHarness.Tests.csproj -m:1 --filter "FullyQualifiedName~MessageLedgerTests" -nologo`
Expected: 6 tests passed.

- [ ] **Step 7: Commit**

```bash
git add examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Assertions/MessageLedger.cs \
        examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/Assertions/MessageLedgerTests.cs
git commit -m "feat(stress-harness): add MessageLedger flow-keyed singleton"
```

---

## Task 4: Implement `MessageLedgerAnalyzer` (TDD)

**Files:**
- Create: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Assertions/MessageLedgerAnalysis.cs`
- Create: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Assertions/MessageLedgerAnalyzer.cs`
- Create: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/Assertions/MessageLedgerAnalyzerTests.cs`

- [ ] **Step 1: Create the analysis result type**

`MessageLedgerAnalysis.cs`:

```csharp
using ServiceConnect.Examples.StressHarness.Chaos;

namespace ServiceConnect.Examples.StressHarness.Assertions;

public sealed record MessageLedgerAnalysis(
    int TotalPublishes,
    int AckedPublishes,
    int FailedPublishes,
    int TotalConsumes,
    int AckedAndConsumed,
    int AckedButLost,
    int FailedThenConsumed,
    int FailedAndLost,
    int PerMessageRedeliveries,
    IReadOnlyDictionary<ChaosWindow, int> AckedButLostByWindow,
    IReadOnlyDictionary<string, int> AckedButLostByPattern,
    IReadOnlyList<PublishRecord> AckedButLostSample,
    int ConsumesWithoutPublish);
```

- [ ] **Step 2: Write the failing analyzer tests**

`MessageLedgerAnalyzerTests.cs`:

```csharp
using FluentAssertions;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Chaos;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Assertions;

public sealed class MessageLedgerAnalyzerTests
{
    [Fact]
    public void Empty_snapshot_yields_all_zero_counts()
    {
        var analysis = MessageLedgerAnalyzer.Analyze(new LedgerSnapshot([], []));
        analysis.TotalPublishes.Should().Be(0);
        analysis.AckedPublishes.Should().Be(0);
        analysis.FailedPublishes.Should().Be(0);
        analysis.TotalConsumes.Should().Be(0);
        analysis.AckedAndConsumed.Should().Be(0);
        analysis.AckedButLost.Should().Be(0);
        analysis.FailedThenConsumed.Should().Be(0);
        analysis.FailedAndLost.Should().Be(0);
        analysis.PerMessageRedeliveries.Should().Be(0);
        analysis.AckedButLostSample.Should().BeEmpty();
        analysis.ConsumesWithoutPublish.Should().Be(0);
    }

    [Fact]
    public void Single_acked_and_consumed_message_counts_as_normal()
    {
        var msg = Guid.NewGuid();
        var flow = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        var snapshot = new LedgerSnapshot(
            [new PublishRecord(msg, flow, "p2p", "alpha", t, t.AddMilliseconds(2), PublishOutcome.Acked, ChaosWindow.PreChaos)],
            [new ConsumeRecord(msg, flow, "p2p", "beta", t.AddMilliseconds(5), ChaosWindow.PreChaos)]);

        var analysis = MessageLedgerAnalyzer.Analyze(snapshot);
        analysis.TotalPublishes.Should().Be(1);
        analysis.AckedAndConsumed.Should().Be(1);
        analysis.AckedButLost.Should().Be(0);
    }

    [Fact]
    public void Acked_but_no_consume_counts_as_acked_but_lost_and_breaks_down_by_window_and_pattern()
    {
        var msg = Guid.NewGuid();
        var flow = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        var snapshot = new LedgerSnapshot(
            [new PublishRecord(msg, flow, "streaming", "alpha", t, t.AddMilliseconds(2), PublishOutcome.Acked, ChaosWindow.InRecovery)],
            []);

        var analysis = MessageLedgerAnalyzer.Analyze(snapshot);
        analysis.AckedButLost.Should().Be(1);
        analysis.AckedButLostByWindow[ChaosWindow.InRecovery].Should().Be(1);
        analysis.AckedButLostByPattern["streaming"].Should().Be(1);
        analysis.AckedButLostSample.Should().ContainSingle();
    }

    [Fact]
    public void Failed_publish_with_no_consume_counts_as_failed_and_lost()
    {
        var msg = Guid.NewGuid();
        var flow = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        var snapshot = new LedgerSnapshot(
            [new PublishRecord(msg, flow, "p2p", "alpha", t, t.AddMilliseconds(2), PublishOutcome.Failed, ChaosWindow.DuringChaos)],
            []);

        var analysis = MessageLedgerAnalyzer.Analyze(snapshot);
        analysis.FailedAndLost.Should().Be(1);
        analysis.AckedButLost.Should().Be(0);
    }

    [Fact]
    public void Failed_publish_with_consume_counts_as_failed_then_consumed()
    {
        var msg = Guid.NewGuid();
        var flow = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        var snapshot = new LedgerSnapshot(
            [new PublishRecord(msg, flow, "p2p", "alpha", t, t.AddMilliseconds(2), PublishOutcome.Failed, ChaosWindow.DuringChaos)],
            [new ConsumeRecord(msg, flow, "p2p", "beta", t.AddMilliseconds(5), ChaosWindow.InRecovery)]);

        var analysis = MessageLedgerAnalyzer.Analyze(snapshot);
        analysis.FailedThenConsumed.Should().Be(1);
        analysis.AckedButLost.Should().Be(0);
        analysis.FailedAndLost.Should().Be(0);
    }

    [Fact]
    public void Multiple_consumes_for_one_publish_increments_redeliveries_by_extras()
    {
        var msg = Guid.NewGuid();
        var flow = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        var snapshot = new LedgerSnapshot(
            [new PublishRecord(msg, flow, "p2p", "alpha", t, t.AddMilliseconds(2), PublishOutcome.Acked, ChaosWindow.PreChaos)],
            [
                new ConsumeRecord(msg, flow, "p2p", "beta", t.AddMilliseconds(5), ChaosWindow.PreChaos),
                new ConsumeRecord(msg, flow, "p2p", "beta", t.AddMilliseconds(50), ChaosWindow.PreChaos),
                new ConsumeRecord(msg, flow, "p2p", "beta", t.AddMilliseconds(100), ChaosWindow.PreChaos),
            ]);

        var analysis = MessageLedgerAnalyzer.Analyze(snapshot);
        analysis.PerMessageRedeliveries.Should().Be(2);
    }

    [Fact]
    public void Consume_with_no_matching_publish_increments_consumes_without_publish()
    {
        var msg = Guid.NewGuid();
        var flow = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        var snapshot = new LedgerSnapshot(
            [],
            [new ConsumeRecord(msg, flow, "p2p", "beta", t, ChaosWindow.PreChaos)]);

        var analysis = MessageLedgerAnalyzer.Analyze(snapshot);
        analysis.ConsumesWithoutPublish.Should().Be(1);
    }

    [Fact]
    public void Acked_but_lost_sample_caps_at_twenty_rows()
    {
        var t = DateTimeOffset.UtcNow;
        var rows = Enumerable.Range(0, 30)
            .Select(i => new PublishRecord(
                Guid.NewGuid(), Guid.NewGuid(), "p2p", "alpha",
                t.AddMilliseconds(i), t.AddMilliseconds(i + 1),
                PublishOutcome.Acked, ChaosWindow.InRecovery))
            .ToArray();
        var snapshot = new LedgerSnapshot(rows, []);

        var analysis = MessageLedgerAnalyzer.Analyze(snapshot);
        analysis.AckedButLost.Should().Be(30);
        analysis.AckedButLostSample.Count.Should().Be(20);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/ServiceConnect.Examples.StressHarness.Tests.csproj -m:1 --filter "FullyQualifiedName~MessageLedgerAnalyzerTests" -nologo`
Expected: Compilation fails — `MessageLedgerAnalyzer` does not exist.

- [ ] **Step 4: Implement `MessageLedgerAnalyzer`**

`MessageLedgerAnalyzer.cs`:

```csharp
using ServiceConnect.Examples.StressHarness.Chaos;

namespace ServiceConnect.Examples.StressHarness.Assertions;

/// <summary>
/// Pure function from <see cref="LedgerSnapshot"/> to <see cref="MessageLedgerAnalysis"/>.
/// Classifies each publish row into one of four quadrants by joining against the consume
/// rows on <see cref="PublishRecord.MessageId"/>:
/// <list type="bullet">
///   <item>Acked + ≥1 consume → normal delivery</item>
///   <item>Acked + 0 consumes → <c>AckedButLost</c> (hypothesis H1)</item>
///   <item>Failed + ≥1 consume → <c>FailedThenConsumed</c> (probably client retry)</item>
///   <item>Failed + 0 consumes → <c>FailedAndLost</c> (expected when broker is down)</item>
/// </list>
/// Also surfaces per-message redelivery counts (extras beyond the first consume) and a
/// 20-row forensic sample of acked-but-lost publishes for hand-tracing in broker logs.
/// </summary>
public static class MessageLedgerAnalyzer
{
    private const int ForensicSampleSize = 20;

    public static MessageLedgerAnalysis Analyze(LedgerSnapshot snapshot)
    {
        var consumesByMessage = snapshot.Consumes
            .GroupBy(c => c.MessageId)
            .ToDictionary(g => g.Key, g => g.Count());

        var ackedAndConsumed = 0;
        var ackedButLost = 0;
        var failedThenConsumed = 0;
        var failedAndLost = 0;
        var ackedPublishes = 0;
        var failedPublishes = 0;
        var perMessageRedeliveries = 0;

        var byWindow = new Dictionary<ChaosWindow, int>();
        var byPattern = new Dictionary<string, int>(StringComparer.Ordinal);
        var ackedButLostRows = new List<PublishRecord>();

        var publishedMessageIds = new HashSet<Guid>();

        foreach (var publish in snapshot.Publishes)
        {
            publishedMessageIds.Add(publish.MessageId);
            var consumeCount = consumesByMessage.GetValueOrDefault(publish.MessageId, 0);

            if (publish.Outcome == PublishOutcome.Acked)
            {
                ackedPublishes++;
                if (consumeCount > 0)
                {
                    ackedAndConsumed++;
                    if (consumeCount > 1)
                    {
                        perMessageRedeliveries += consumeCount - 1;
                    }
                }
                else
                {
                    ackedButLost++;
                    byWindow[publish.Window] = byWindow.GetValueOrDefault(publish.Window, 0) + 1;
                    byPattern[publish.Pattern] = byPattern.GetValueOrDefault(publish.Pattern, 0) + 1;
                    ackedButLostRows.Add(publish);
                }
            }
            else
            {
                failedPublishes++;
                if (consumeCount > 0)
                {
                    failedThenConsumed++;
                    if (consumeCount > 1)
                    {
                        perMessageRedeliveries += consumeCount - 1;
                    }
                }
                else
                {
                    failedAndLost++;
                }
            }
        }

        var consumesWithoutPublish = 0;
        foreach (var (messageId, count) in consumesByMessage)
        {
            if (!publishedMessageIds.Contains(messageId))
            {
                consumesWithoutPublish += count;
            }
        }

        var sample = ackedButLostRows
            .OrderBy(r => r.PublishStarted)
            .Take(ForensicSampleSize)
            .ToArray();

        return new MessageLedgerAnalysis(
            TotalPublishes: snapshot.Publishes.Count,
            AckedPublishes: ackedPublishes,
            FailedPublishes: failedPublishes,
            TotalConsumes: snapshot.Consumes.Count,
            AckedAndConsumed: ackedAndConsumed,
            AckedButLost: ackedButLost,
            FailedThenConsumed: failedThenConsumed,
            FailedAndLost: failedAndLost,
            PerMessageRedeliveries: perMessageRedeliveries,
            AckedButLostByWindow: byWindow,
            AckedButLostByPattern: byPattern,
            AckedButLostSample: sample,
            ConsumesWithoutPublish: consumesWithoutPublish);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/ServiceConnect.Examples.StressHarness.Tests.csproj -m:1 --filter "FullyQualifiedName~MessageLedgerAnalyzerTests" -nologo`
Expected: 8 tests passed.

- [ ] **Step 6: Commit**

```bash
git add examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Assertions/MessageLedgerAnalysis.cs \
        examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Assertions/MessageLedgerAnalyzer.cs \
        examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/Assertions/MessageLedgerAnalyzerTests.cs
git commit -m "feat(stress-harness): add MessageLedgerAnalyzer with four-quadrant classification"
```

---

## Task 5: Implement `LedgeredSender` (TDD)

**Files:**
- Create: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Orchestrator/LedgeredSender.cs`
- Create: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/Orchestrator/LedgeredSenderTests.cs`

- [ ] **Step 1: Read the `IBus` interface to understand the surface to proxy**

Run: `grep -n "public.*Async\|public.*Stream\|public.*Send\|public.*Publish\|public.*Route\|public.*Request" src/ServiceConnect.Interfaces/IBus.cs`
Expected: shows the full virtual surface. Note the exact signatures of `SendAsync`, `PublishAsync`, `SendRequestAsync`, `RouteAsync`, `CreateStream`. The wrap implements `IBus` directly and forwards every member to the inner bus; the only ones that record ledger entries are the header-bearing overloads.

- [ ] **Step 2: Write the failing wrapper tests**

`LedgeredSenderTests.cs`:

```csharp
using FluentAssertions;
using Moq;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Chaos;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Examples.StressHarness.Orchestrator;
using ServiceConnect.Examples.StressHarness.Patterns;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Orchestrator;

public sealed class LedgeredSenderTests
{
    [Fact]
    public async Task SendAsync_records_publish_start_and_completed_acked_on_success()
    {
        var ledger = new MessageLedger();
        var clock = new FakeChaosClock(ChaosWindow.PreChaos);
        var inner = new Mock<IBus>(MockBehavior.Strict);
        inner.Setup(b => b.SendAsync(It.IsAny<P2pPing>(), It.IsAny<SendOptions>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);

        var wrapped = new LedgeredSender(inner.Object, ledger, clock);
        var flowId = Guid.NewGuid();
        var msg = new P2pPing(flowId);
        var opts = new SendOptions
        {
            EndPoint = "stress-b.work",
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [StressHeaders.FlowId] = flowId.ToString("N"),
                [StressHeaders.OriginBus] = "alpha",
                [StressHeaders.Pattern] = "p2p",
            },
        };

        await wrapped.SendAsync(msg, opts);

        opts.Headers.Should().ContainKey(StressHeaders.MessageId);
        var snapshot = ledger.Snapshot();
        snapshot.Publishes.Should().ContainSingle();
        var row = snapshot.Publishes[0];
        row.FlowId.Should().Be(flowId);
        row.Pattern.Should().Be("p2p");
        row.OriginBus.Should().Be("alpha");
        row.Outcome.Should().Be(PublishOutcome.Acked);
    }

    [Fact]
    public async Task SendAsync_records_publish_completed_failed_when_inner_throws()
    {
        var ledger = new MessageLedger();
        var clock = new FakeChaosClock(ChaosWindow.DuringChaos);
        var inner = new Mock<IBus>(MockBehavior.Strict);
        inner.Setup(b => b.SendAsync(It.IsAny<P2pPing>(), It.IsAny<SendOptions>(), It.IsAny<CancellationToken>()))
             .ThrowsAsync(new InvalidOperationException("broker is down"));

        var wrapped = new LedgeredSender(inner.Object, ledger, clock);
        var flowId = Guid.NewGuid();
        var msg = new P2pPing(flowId);
        var opts = new SendOptions
        {
            EndPoint = "stress-b.work",
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [StressHeaders.FlowId] = flowId.ToString("N"),
                [StressHeaders.OriginBus] = "alpha",
                [StressHeaders.Pattern] = "p2p",
            },
        };

        var act = async () => await wrapped.SendAsync(msg, opts);
        await act.Should().ThrowAsync<InvalidOperationException>();

        var row = ledger.Snapshot().Publishes.Single();
        row.Outcome.Should().Be(PublishOutcome.Failed);
        row.Window.Should().Be(ChaosWindow.DuringChaos);
    }

    [Fact]
    public async Task PublishAsync_stamps_message_id_into_headers()
    {
        var ledger = new MessageLedger();
        var clock = new FakeChaosClock(ChaosWindow.PreChaos);
        var inner = new Mock<IBus>(MockBehavior.Strict);
        PublishOptions? captured = null;
        inner.Setup(b => b.PublishAsync(It.IsAny<TopicAnnounced>(), It.IsAny<PublishOptions>(), It.IsAny<CancellationToken>()))
             .Callback<TopicAnnounced, PublishOptions, CancellationToken>((_, o, _) => captured = o)
             .Returns(Task.CompletedTask);

        var wrapped = new LedgeredSender(inner.Object, ledger, clock);
        var flowId = Guid.NewGuid();
        var opts = new PublishOptions
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [StressHeaders.FlowId] = flowId.ToString("N"),
                [StressHeaders.OriginBus] = "alpha",
                [StressHeaders.Pattern] = "pubsub",
            },
        };

        await wrapped.PublishAsync(new TopicAnnounced(flowId), opts);

        captured.Should().NotBeNull();
        captured!.Headers.Should().ContainKey(StressHeaders.MessageId);
        Guid.TryParseExact(captured.Headers[StressHeaders.MessageId], "N", out _).Should().BeTrue();
    }
}

internal sealed class FakeChaosClock(ChaosWindow window) : IChaosClock
{
    public ChaosWindow CurrentWindow => window;
}
```

> Note: `IChaosClock` must already exist on the harness side. If the current code holds the window on a concrete `ChaosClock`, introduce an `IChaosClock` interface returning `CurrentWindow` in this task — one line in `Chaos/ChaosClock.cs`. The wrapper depends on the interface so tests can supply a fake.

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/ServiceConnect.Examples.StressHarness.Tests.csproj -m:1 --filter "FullyQualifiedName~LedgeredSenderTests" -nologo`
Expected: Compilation fails — `LedgeredSender` does not exist.

- [ ] **Step 4: If `IChaosClock` does not yet exist, add it**

Run: `grep -n "class ChaosClock\|interface IChaosClock\|CurrentWindow" examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Chaos/*.cs | head -10`
If `IChaosClock` is missing: in `examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Chaos/ChaosClock.cs`, add the interface above the class:

```csharp
public interface IChaosClock
{
    ChaosWindow CurrentWindow { get; }
}
```

Then make `ChaosClock` implement it: `public sealed class ChaosClock : IChaosClock`.

- [ ] **Step 5: Implement `LedgeredSender`**

`LedgeredSender.cs`:

```csharp
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Chaos;
using ServiceConnect.Examples.StressHarness.Patterns;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Messages;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Examples.StressHarness.Orchestrator;

/// <summary>
/// Decorates an <see cref="IBus"/> so every header-bearing publish surface writes a
/// publish row into the supplied <see cref="MessageLedger"/>. The decorator stamps a
/// fresh <c>Guid</c> into the <see cref="StressHeaders.MessageId"/> header before each
/// inner call so the receiver handler can echo the same id back via
/// <see cref="MessageLedger.RecordConsume"/>. Non-header surfaces
/// (<see cref="IBus.RouteAsync"/>, <see cref="IBus.CreateStream{T}"/>) cannot carry a
/// caller-controlled id; the wrap records one row keyed by the message's
/// <see cref="Message.CorrelationId"/> so a complete-flow loss is still visible at the
/// per-call level — per-hop / per-chunk granularity for those two paths needs framework
/// instrumentation and is out of scope.
/// </summary>
public sealed class LedgeredSender(IBus inner, MessageLedger ledger, IChaosClock clock) : IBus
{
    public Task SendAsync<T>(T message, SendOptions options, CancellationToken cancellationToken = default)
        where T : Message
    {
        return RecordAndForward(
            options.Headers,
            messageCorrelationId: message.CorrelationId,
            forward: (id, opts) =>
            {
                EnsureHeaderStamped(opts.Headers, StressHeaders.MessageId, id.ToString("N"));
                return inner.SendAsync(message, opts, cancellationToken);
            },
            options);
    }

    public Task PublishAsync<T>(T message, PublishOptions options, CancellationToken cancellationToken = default)
        where T : Message
    {
        return RecordAndForward(
            options.Headers,
            messageCorrelationId: message.CorrelationId,
            forward: (id, opts) =>
            {
                EnsureHeaderStamped(opts.Headers, StressHeaders.MessageId, id.ToString("N"));
                return inner.PublishAsync(message, opts, cancellationToken);
            },
            options);
    }

    public Task<TResponse> SendRequestAsync<TRequest, TResponse>(
        TRequest message,
        RequestOptions options,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TResponse : Message
    {
        // Records a publish row for the request leg. The reply leg is published by the
        // framework's request-reply forwarder; it does not flow through this wrap.
        var task = RecordAndForwardWithReturn<TResponse>(
            options.Headers,
            messageCorrelationId: message.CorrelationId,
            forward: (id, opts) =>
            {
                EnsureHeaderStamped(opts.Headers, StressHeaders.MessageId, id.ToString("N"));
                return inner.SendRequestAsync<TRequest, TResponse>(message, opts, cancellationToken);
            },
            options);
        return task;
    }

    public Task RouteAsync<T>(T message, IList<string> destinations, CancellationToken cancellationToken = default)
        where T : Message
    {
        // No SendOptions overload exists for RouteAsync — the harness cannot stamp the
        // X-Stress-MessageId header on the slip. Use the message's CorrelationId as the
        // ledger key so per-call (not per-hop) coverage is preserved.
        var window = clock.CurrentWindow;
        ledger.RecordPublishStart(
            messageId: message.CorrelationId,
            flowId: message.CorrelationId,
            pattern: "routing-slip",
            originBus: "(routeasync)",
            started: DateTimeOffset.UtcNow,
            window: window);
        return inner.RouteAsync(message, destinations, cancellationToken)
            .ContinueWith(t =>
            {
                ledger.RecordPublishCompleted(
                    messageId: message.CorrelationId,
                    completed: DateTimeOffset.UtcNow,
                    outcome: t.IsFaulted ? PublishOutcome.Failed : PublishOutcome.Acked);
                if (t.IsFaulted)
                {
                    throw t.Exception!.GetBaseException();
                }
            }, TaskScheduler.Default);
    }

    public IMessageStream<T> CreateStream<T>(string endPoint)
        where T : Message
    {
        // Streaming chunks are framework-emitted; the wrap only sees the high-level
        // CreateStream call. We don't record here because the high-level call has no
        // observable outcome from the harness perspective — the chunked writes happen
        // through the framework's StreamProcessor and there is no clean "this stream
        // was acked" event the wrap can observe. Streaming losses surface in
        // FlowAccounting.MissingFlows instead.
        return inner.CreateStream<T>(endPoint);
    }

    public Task StartConsumingAsync(CancellationToken cancellationToken = default) => inner.StartConsumingAsync(cancellationToken);
    public Task StopConsumingAsync(CancellationToken cancellationToken = default) => inner.StopConsumingAsync(cancellationToken);
    public bool IsConsuming => inner.IsConsuming;
    public ValueTask DisposeAsync() => inner.DisposeAsync();

    // Forward any remaining IBus members verbatim. Add similar pass-throughs for each
    // member declared on the IBus surface that this wrap does not override. The wrap
    // is a decorator; only the publish/send surfaces are instrumented.

    private async Task RecordAndForward(
        IDictionary<string, string>? headers,
        Guid messageCorrelationId,
        Func<Guid, IHeaderBearingOptions, Task> forward,
        IHeaderBearingOptions options)
    {
        var messageId = Guid.NewGuid();
        var flowId = ExtractFlowId(headers, fallback: messageCorrelationId);
        var pattern = ExtractHeader(headers, StressHeaders.Pattern, fallback: "(unknown)");
        var originBus = ExtractHeader(headers, StressHeaders.OriginBus, fallback: "(unknown)");
        var window = clock.CurrentWindow;

        ledger.RecordPublishStart(messageId, flowId, pattern, originBus, DateTimeOffset.UtcNow, window);
        try
        {
            await forward(messageId, options).ConfigureAwait(false);
            ledger.RecordPublishCompleted(messageId, DateTimeOffset.UtcNow, PublishOutcome.Acked);
        }
        catch
        {
            ledger.RecordPublishCompleted(messageId, DateTimeOffset.UtcNow, PublishOutcome.Failed);
            throw;
        }
    }

    private async Task<TResult> RecordAndForwardWithReturn<TResult>(
        IDictionary<string, string>? headers,
        Guid messageCorrelationId,
        Func<Guid, IHeaderBearingOptions, Task<TResult>> forward,
        IHeaderBearingOptions options)
    {
        var messageId = Guid.NewGuid();
        var flowId = ExtractFlowId(headers, fallback: messageCorrelationId);
        var pattern = ExtractHeader(headers, StressHeaders.Pattern, fallback: "(unknown)");
        var originBus = ExtractHeader(headers, StressHeaders.OriginBus, fallback: "(unknown)");
        var window = clock.CurrentWindow;

        ledger.RecordPublishStart(messageId, flowId, pattern, originBus, DateTimeOffset.UtcNow, window);
        try
        {
            var result = await forward(messageId, options).ConfigureAwait(false);
            ledger.RecordPublishCompleted(messageId, DateTimeOffset.UtcNow, PublishOutcome.Acked);
            return result;
        }
        catch
        {
            ledger.RecordPublishCompleted(messageId, DateTimeOffset.UtcNow, PublishOutcome.Failed);
            throw;
        }
    }

    private static Guid ExtractFlowId(IDictionary<string, string>? headers, Guid fallback)
    {
        if (headers is not null
            && headers.TryGetValue(StressHeaders.FlowId, out var raw)
            && Guid.TryParseExact(raw, "N", out var parsed))
        {
            return parsed;
        }
        return fallback;
    }

    private static string ExtractHeader(IDictionary<string, string>? headers, string name, string fallback)
    {
        return headers is not null && headers.TryGetValue(name, out var raw) ? raw : fallback;
    }

    private static void EnsureHeaderStamped(IDictionary<string, string>? headers, string name, string value)
    {
        // Drivers always construct a non-null Headers dictionary on the options they pass.
        // The wrap stamps the MessageId; if a future driver passes Headers=null the wrap
        // currently won't be able to set it — the publish still goes through but the
        // consume side cannot correlate. Surface that as an analyzer "consumes without
        // publish" count later rather than throwing here.
        if (headers is null) return;
        headers[name] = value;
    }
}

/// <summary>
/// Minimal shape over <see cref="SendOptions"/>, <see cref="PublishOptions"/>, and
/// <see cref="RequestOptions"/> so <see cref="LedgeredSender"/> can share record-and-
/// forward logic without three near-identical method bodies. Implemented by an
/// internal adapter; not exposed publicly.
/// </summary>
internal interface IHeaderBearingOptions
{
    IDictionary<string, string>? Headers { get; }
}
```

> Note for the implementer: the `IHeaderBearingOptions` abstraction is sketched but the framework's `SendOptions`/`PublishOptions`/`RequestOptions` don't implement it natively. The implementer can either (a) add `internal sealed class SendOptionsAdapter(SendOptions opts) : IHeaderBearingOptions` shims and adapt at each call site, or (b) write three separate near-identical method bodies for `SendAsync`/`PublishAsync`/`SendRequestAsync` and skip the abstraction. Both are acceptable; option (b) is the YAGNI choice if the framework's option types diverge in shape.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/ServiceConnect.Examples.StressHarness.Tests.csproj -m:1 --filter "FullyQualifiedName~LedgeredSenderTests" -nologo`
Expected: 3 tests passed.

- [ ] **Step 7: Commit**

```bash
git add examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Orchestrator/LedgeredSender.cs \
        examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/Orchestrator/LedgeredSenderTests.cs \
        examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Chaos/ChaosClock.cs
git commit -m "feat(stress-harness): add LedgeredSender wrap recording per-publish ledger rows"
```

---

## Task 6: Wire `MessageLedger` + `LedgeredSender` into `HarnessHost`

**Files:**
- Modify: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Orchestrator/HarnessHost.cs`

- [ ] **Step 1: Add MessageLedger to BuildServices**

In `HarnessHost.BuildServices`, before the `registerPerBus(builder, busTag);` call, add the ledger registration. The ledger must be the SAME instance across both buses, so it must be constructed once and passed into both `BuildServices` calls — same shared-instance pattern used by the seven existing flow-keyed singletons.

The cleanest minimal change is to take a `MessageLedger` parameter on `BuildServices` and on `StartAsync`. Inside `StartAsync`, construct `var ledger = new MessageLedger();` once and pass it to both `BuildServices` calls.

Replace the `StartAsync` signature and inner construction:

```csharp
public static async Task<HarnessHost> StartAsync(
    HarnessOptions options,
    Action<ServiceConnectBuilder, string> registerPerBus,
    ILoggerFactory loggerFactory,
    MessageLedger ledger,
    IChaosClock chaosClock,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(registerPerBus);
    ArgumentNullException.ThrowIfNull(loggerFactory);
    ArgumentNullException.ThrowIfNull(ledger);
    ArgumentNullException.ThrowIfNull(chaosClock);

    var alphaServices = BuildServices(options, loggerFactory, busTag: "alpha", queuePrefix: "stress-a", registerPerBus, ledger, chaosClock);
    ServiceProvider? betaServices = null;
    IBus? alpha = null;
    IBus? beta = null;
    try
    {
        betaServices = BuildServices(options, loggerFactory, busTag: "beta", queuePrefix: "stress-b", registerPerBus, ledger, chaosClock);

        alpha = alphaServices.GetRequiredService<IBus>();
        beta = betaServices.GetRequiredService<IBus>();
        // wrap each resolved bus with LedgeredSender before starting consuming.
        alpha = new LedgeredSender(alpha, ledger, chaosClock);
        beta = new LedgeredSender(beta, ledger, chaosClock);

        await alpha.StartConsumingAsync(cancellationToken).ConfigureAwait(false);
        await beta.StartConsumingAsync(cancellationToken).ConfigureAwait(false);

        return new HarnessHost(alpha, alphaServices, beta, betaServices);
    }
    // ... existing catch unchanged
}
```

> Note: `LedgeredSender.StartConsumingAsync` already forwards to the inner bus, so the existing start sequence is preserved verbatim.

Update `BuildServices` to accept the two new parameters and register the ledger as a singleton on each per-bus collection so handlers can resolve it:

```csharp
private static ServiceProvider BuildServices(
    HarnessOptions options,
    ILoggerFactory loggerFactory,
    string busTag,
    string queuePrefix,
    Action<ServiceConnectBuilder, string> registerPerBus,
    MessageLedger ledger,
    IChaosClock chaosClock)
{
    var services = new ServiceCollection();
    services.AddSingleton(loggerFactory);
    services.AddLogging();
    services.AddSingleton<IReadOnlyList<HandlerReference>>([]);
    services.AddSingleton(ledger);
    services.AddSingleton<IFlowKeyedSingleton>(sp => sp.GetRequiredService<MessageLedger>());
    services.AddSingleton(chaosClock);
    // ... rest of body unchanged
}
```

- [ ] **Step 2: Update every call site of `HarnessHost.StartAsync` to pass the new parameters**

Run: `grep -rn "HarnessHost.StartAsync\|HarnessHost\.StartAsync" examples/StressHarness/src --include="*.cs" | head -10`
Expected output identifies the call site(s). For each, construct a single `var ledger = new MessageLedger();` and `var chaosClock = ...;` (already exists) and thread them through.

- [ ] **Step 3: Update `Program.cs` to collect the ledger into the existing `IFlowKeyedSingleton` list passed to `ModeDispatcher`**

In Program.cs locate the existing `IReadOnlyList<IFlowKeyedSingleton>` construction (added during the accumulator-bounding work). Add `ledger` to the list before passing to the dispatcher.

- [ ] **Step 4: Build the harness project**

Run: `dotnet build examples/StressHarness/src/ServiceConnect.Examples.StressHarness/ServiceConnect.Examples.StressHarness.csproj -m:1 -nologo`
Expected: Build succeeded, 0 warnings, 0 errors. If a member of `IBus` was missed in `LedgeredSender` the compiler surfaces it here.

- [ ] **Step 5: Run the full harness test suite**

Run: `dotnet test examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/ServiceConnect.Examples.StressHarness.Tests.csproj -m:1 -nologo`
Expected: all existing tests + the new ledger tests pass.

- [ ] **Step 6: Commit**

```bash
git add examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Orchestrator/HarnessHost.cs \
        examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Program.cs
git commit -m "feat(stress-harness): wire MessageLedger + LedgeredSender into HarnessHost"
```

---

## Task 7: Add consume-side ledger recording in every handler

**Files (one per handler):**
- Modify: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/P2pHandler.cs`
- Modify: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/PubSubHandler.cs`
- Modify: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/QuoteRequestHandler.cs`
- Modify: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/WorkItemHandler.cs`
- Modify: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/PremiumOrderHandler.cs`
- Modify: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/StandardOrderHandler.cs`
- Modify: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/DomainEventHandler.cs`
- Modify: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/FilteredMessageHandler.cs`
- Modify: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/SagaHandler.cs`
- Modify: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/SearchRequestHandler.cs`
- Modify: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/SlipOrderHandler.cs`
- Modify: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/DocumentUploadedHandler.cs`
- Modify: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/DedupedMessageHandler.cs`
- Modify: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/TracedEventHandler.cs`

- [ ] **Step 1: Update `P2pHandler.cs`**

Replace the existing class with:

```csharp
public sealed class P2pHandler(string busTag, FlowAccounting accounting, PerHandlerSignal signals, MessageLedger ledger, IChaosClock chaosClock)
    : IMessageHandler<P2pPing>
{
    public Task HandleAsync(P2pPing message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        if (context.Headers.TryGetValue(StressHeaders.FlowId, out var rawFlow)
            && HeaderDecoder.Decode(rawFlow) is { } flowIdStr
            && Guid.TryParseExact(flowIdStr, "N", out var flowId))
        {
            accounting.RecordHandled(flowId);
            signals.Signal(flowId, busTag, context);
            RecordLedgerConsume(message, context, "p2p", busTag, flowId, ledger, chaosClock);
        }
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Add the shared helper `LedgerHandlerHelpers.cs`**

To avoid repeating the same 5 lines in 14 handlers, create
`examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/LedgerHandlerHelpers.cs`:

```csharp
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Chaos;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Messages;

namespace ServiceConnect.Examples.StressHarness.Patterns.Handlers;

internal static class LedgerHandlerHelpers
{
    public static void RecordLedgerConsume(
        Message message,
        IConsumeContext context,
        string pattern,
        string busTag,
        Guid flowId,
        MessageLedger ledger,
        IChaosClock chaosClock)
    {
        if (context.Headers.TryGetValue(StressHeaders.MessageId, out var rawMsg)
            && HeaderDecoder.Decode(rawMsg) is { } msgIdStr
            && Guid.TryParseExact(msgIdStr, "N", out var messageId))
        {
            ledger.RecordConsume(messageId, flowId, pattern, busTag, DateTimeOffset.UtcNow, chaosClock.CurrentWindow);
        }
        else
        {
            // Header missing — patterns whose publish path doesn't carry caller headers
            // (routing-slip's RouteAsync, streaming's chunk packets). Fall back to the
            // message's CorrelationId so the publish row recorded under that key (see
            // LedgeredSender.RouteAsync) still has a matching consume row.
            ledger.RecordConsume(message.CorrelationId, flowId, pattern, busTag, DateTimeOffset.UtcNow, chaosClock.CurrentWindow);
        }
    }
}
```

- [ ] **Step 3: Update `P2pHandler.cs` to use the helper**

```csharp
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Chaos;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Interfaces;
using static ServiceConnect.Examples.StressHarness.Patterns.Handlers.LedgerHandlerHelpers;

namespace ServiceConnect.Examples.StressHarness.Patterns.Handlers;

public sealed class P2pHandler(string busTag, FlowAccounting accounting, PerHandlerSignal signals, MessageLedger ledger, IChaosClock chaosClock)
    : IMessageHandler<P2pPing>
{
    public Task HandleAsync(P2pPing message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        if (context.Headers.TryGetValue(StressHeaders.FlowId, out var raw)
            && HeaderDecoder.Decode(raw) is { } flowIdStr
            && Guid.TryParseExact(flowIdStr, "N", out var flowId))
        {
            accounting.RecordHandled(flowId);
            signals.Signal(flowId, busTag, context);
            RecordLedgerConsume(message, context, "p2p", busTag, flowId, ledger, chaosClock);
        }
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 4: Update the other 13 handlers identically**

For each remaining handler in the list above:
1. Add `MessageLedger ledger` and `IChaosClock chaosClock` to the primary constructor parameters.
2. Add `using static ServiceConnect.Examples.StressHarness.Patterns.Handlers.LedgerHandlerHelpers;`.
3. Inside the existing successful-FlowId branch, after the existing `signals.Signal(...)` / `accounting.RecordHandled(...)` calls, add `RecordLedgerConsume(message, context, "<pattern-name>", busTag, flowId, ledger, chaosClock);`.

The `<pattern-name>` strings must match the `IPatternDriver.Name` for each pattern (`p2p`, `pubsub`, `request-reply`, `competing-consumers`, `content-based-routing`, `polymorphic`, `filters`, `process-manager`, `aggregator`, `scatter-gather`, `routing-slip`, `streaming`, `custom-filter-middleware`, `telemetry`).

For `DocumentUploadedHandler.cs` (streaming) and `SlipOrderHandler.cs` (routing-slip): the publish path strips harness headers, so the helper's fallback branch keys consumes by `message.CorrelationId`. No special-casing needed at the handler — the helper handles it.

- [ ] **Step 5: Update handler registration sites to inject `MessageLedger` and `IChaosClock`**

Run: `grep -rn "P2pHandler(\|PubSubHandler(\|new .*Handler(" examples/StressHarness/src/ServiceConnect.Examples.StressHarness --include="*.cs" | head -30`
For each `new XxxHandler(busTag, accounting, signals, ...)` registration, append `, sp.GetRequiredService<MessageLedger>(), sp.GetRequiredService<IChaosClock>()`. Most or all live inside the `registerPerBus` callback bodies of the pattern drivers — usually a single line per pattern.

- [ ] **Step 6: Build the harness + run all tests**

Run: `dotnet build examples/StressHarness/src/ServiceConnect.Examples.StressHarness/ServiceConnect.Examples.StressHarness.csproj -m:1 -nologo`
Expected: 0 errors.

Run: `dotnet test examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/ServiceConnect.Examples.StressHarness.Tests.csproj -m:1 -nologo`
Expected: All tests pass.

- [ ] **Step 7: Commit**

```bash
git add examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Patterns/Handlers/
git commit -m "feat(stress-harness): record consumes on every handler via MessageLedger helper"
```

---

## Task 8: Integrate analyzer output into the `Report`

**Files:**
- Modify: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Reporting/Report.cs`
- Modify: the report-building site (search below).
- Modify: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Reporting/MarkdownReportWriter.cs`
- Modify: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Reporting/JsonReportWriter.cs`
- Create: `examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/Reporting/MessageLedgerReportTests.cs`

- [ ] **Step 1: Add the `MessageLedger` slot to `Report`**

In `Report.cs`, append a parameter to the `Report` record:

```csharp
public sealed record Report(
    int ReportVersion,
    string Mode,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    TimeSpan Duration,
    long MemoryBaselineBytes,
    long MemoryFinalBytes,
    int TotalFlows,
    int PassedFlows,
    int FailedFlows,
    IReadOnlyList<PatternStats> Patterns,
    IReadOnlyList<string> ProcessAssertionFailures,
    ReportMetadata Metadata,
    ChaosWindowStats? Chaos,
    MessageLedgerAnalysis? MessageLedger);
```

And update every constructor call of `Report` to pass `MessageLedger: null` or the analyzed result.

- [ ] **Step 2: Compute the analysis at report-build time**

Run: `grep -rn "new Report(" examples/StressHarness/src/ServiceConnect.Examples.StressHarness --include="*.cs" | head -5`
At each report construction site, add `MessageLedgerAnalyzer.Analyze(ledger.Snapshot())` as the new constructor argument. `ledger` is the `MessageLedger` singleton resolved from DI / passed into the dispatcher (same one that's already passed via `IFlowKeyedSingleton`).

- [ ] **Step 3: Write the failing markdown writer test**

In `MessageLedgerReportTests.cs`:

```csharp
using FluentAssertions;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Chaos;
using ServiceConnect.Examples.StressHarness.Reporting;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Reporting;

public sealed class MessageLedgerReportTests
{
    [Fact]
    public void Markdown_renders_message_ledger_section_when_analysis_present()
    {
        var analysis = new MessageLedgerAnalysis(
            TotalPublishes: 100,
            AckedPublishes: 99,
            FailedPublishes: 1,
            TotalConsumes: 95,
            AckedAndConsumed: 95,
            AckedButLost: 4,
            FailedThenConsumed: 0,
            FailedAndLost: 1,
            PerMessageRedeliveries: 0,
            AckedButLostByWindow: new Dictionary<ChaosWindow, int> { [ChaosWindow.InRecovery] = 4 },
            AckedButLostByPattern: new Dictionary<string, int> { ["streaming"] = 3, ["aggregator"] = 1 },
            AckedButLostSample: [],
            ConsumesWithoutPublish: 0);

        var report = BuildReportFixture(messageLedger: analysis);
        var md = MarkdownReportWriter.Render(report);

        md.Should().Contain("## Message ledger");
        md.Should().Contain("**Publishes:** 100 (acked 99 / failed 1)");
        md.Should().Contain("**Acked-but-lost:** 4");
        md.Should().Contain("InRecovery | 4");
        md.Should().Contain("streaming | 3");
    }

    [Fact]
    public void Markdown_skips_message_ledger_section_when_analysis_null()
    {
        var report = BuildReportFixture(messageLedger: null);
        var md = MarkdownReportWriter.Render(report);
        md.Should().NotContain("## Message ledger");
    }

    private static Report BuildReportFixture(MessageLedgerAnalysis? messageLedger)
    {
        // Use the existing test helper if one exists; otherwise inline the minimum
        // Report constructor call for a smoke-style report with zero patterns and
        // no chaos.
        return new Report(
            ReportVersion: 1,
            Mode: "smoke",
            StartedAtUtc: DateTimeOffset.UtcNow,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            Duration: TimeSpan.Zero,
            MemoryBaselineBytes: 0,
            MemoryFinalBytes: 0,
            TotalFlows: 0,
            PassedFlows: 0,
            FailedFlows: 0,
            Patterns: [],
            ProcessAssertionFailures: [],
            Metadata: new ReportMetadata("h", "rt", "amqp://x", "inmemory"),
            Chaos: null,
            MessageLedger: messageLedger);
    }
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/ServiceConnect.Examples.StressHarness.Tests.csproj -m:1 --filter "FullyQualifiedName~MessageLedgerReportTests" -nologo`
Expected: tests fail — the markdown writer does not yet render the new section.

- [ ] **Step 5: Add the markdown rendering**

In `MarkdownReportWriter.cs`, after the Chaos section (search `## Chaos events`), add:

```csharp
if (report.MessageLedger is not null)
{
    var l = report.MessageLedger;
    sb.AppendLine();
    sb.AppendLine("## Message ledger");
    sb.AppendLine();
    sb.AppendLine(CultureInfo.InvariantCulture, $"**Publishes:** {l.TotalPublishes} (acked {l.AckedPublishes} / failed {l.FailedPublishes})");
    sb.AppendLine(CultureInfo.InvariantCulture, $"**Consumes:** {l.TotalConsumes}");
    sb.AppendLine(CultureInfo.InvariantCulture, $"**Acked-but-lost:** {l.AckedButLost}");
    sb.AppendLine(CultureInfo.InvariantCulture, $"**Failed-and-lost:** {l.FailedAndLost}");
    sb.AppendLine(CultureInfo.InvariantCulture, $"**Per-message redeliveries:** {l.PerMessageRedeliveries}");
    if (l.ConsumesWithoutPublish > 0)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Consumes without matching publish:** {l.ConsumesWithoutPublish}  (wrap-point gap — see plan risks)");
    }

    if (l.AckedButLost > 0)
    {
        sb.AppendLine();
        sb.AppendLine("### Acked-but-lost breakdown by publish window");
        sb.AppendLine();
        sb.AppendLine("| Window | Count |");
        sb.AppendLine("|---|---|");
        foreach (var (window, count) in l.AckedButLostByWindow.OrderBy(kv => kv.Key))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {window} | {count} |");
        }

        sb.AppendLine();
        sb.AppendLine("### Acked-but-lost breakdown by pattern");
        sb.AppendLine();
        sb.AppendLine("| Pattern | Count |");
        sb.AppendLine("|---|---|");
        foreach (var (pattern, count) in l.AckedButLostByPattern.OrderByDescending(kv => kv.Value))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {pattern} | {count} |");
        }

        if (l.AckedButLostSample.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"### Acked-but-lost — first {l.AckedButLostSample.Count} forensic rows");
            sb.AppendLine();
            sb.AppendLine("| MessageId | FlowId | Pattern | OriginBus | PublishStarted | PublishWindow |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var row in l.AckedButLostSample)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| {row.MessageId:N} | {row.FlowId:N} | {row.Pattern} | {row.OriginBus} | {row.PublishStarted:O} | {row.Window} |");
            }
        }
    }
}
```

- [ ] **Step 6: Add the JSON writer field**

In `JsonReportWriter.cs`, the writer should serialize the `Report` record; `System.Text.Json` picks up the new `MessageLedger` property automatically. If the writer constructs a custom DTO, add the field to it. Verify by running the existing `JsonReportWriterTests` and updating them if the schema is now expected to include `messageLedger`.

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/ServiceConnect.Examples.StressHarness.Tests.csproj -m:1 -nologo`
Expected: all tests pass, including the new `MessageLedgerReportTests`.

- [ ] **Step 8: Commit**

```bash
git add examples/StressHarness/src/ServiceConnect.Examples.StressHarness/Reporting/ \
        examples/StressHarness/src/ServiceConnect.Examples.StressHarness.Tests/Reporting/MessageLedgerReportTests.cs
git commit -m "feat(stress-harness): render Message ledger section in markdown + JSON reports"
```

---

## Task 9: Verify with smoke and 5-minute chaos soak

**Files:** (no source changes; runs only)

- [ ] **Step 1: Run smoke**

Run: `cd examples/StressHarness && ./run.sh smoke`
Expected: smoke run completes; `examples/StressHarness/out/report.md` contains a `## Message ledger` section with `AckedButLost: 0`, `FailedAndLost: 0`, `PerMessageRedeliveries: 0`, and `ConsumesWithoutPublish: 0`.

- [ ] **Step 2: Run throughput**

Run: `cd examples/StressHarness && ./run.sh throughput`
Expected: 30 seconds at rate 50; ledger section reports 0 lost.

- [ ] **Step 3: Run the 5-minute chaos soak**

Run: `cd examples/StressHarness && ./run.sh chaos-soak-5m` (or the equivalent invocation that produces the chaos report at `out/report.md`).
Expected: completes in ~5 minutes; report renders the new `## Message ledger` section.

- [ ] **Step 4: Read the ledger output and confirm the diagnostic**

Open `examples/StressHarness/out/report.md`. The new section should clearly show:
- `AckedButLost` count > 0 (matches FlowAccounting's MissingFlows count, ideally 1:1 for fully-instrumented patterns).
- Breakdown by `PublishWindow` — which window contains the lost publishes is the load-bearing signal.
- Breakdown by pattern — should align with the failing-pattern distribution we saw earlier (streaming, routing-slip, aggregator, scatter-gather).

If `AckedButLost == 0` and `MissingFlows > 0`, then the loss is entirely in framework-mediated publishes (streaming chunks, routing-slip forward hops) and a follow-up plan needs to instrument the framework.

- [ ] **Step 5: Capture the analysis as a follow-up note**

Append a short note (in the chat / PR description, not a new file unless the user asks) summarising:
- The four quadrant counts.
- Which publish window holds the most acked-but-lost messages.
- Which pattern(s) dominate the acked-but-lost count.
- Whether this supports hypothesis H1 (publisher-side ack-before-fsync) or H2 (routing-state-loss), and propose the next fix accordingly.

- [ ] **Step 6: Commit any non-source artefacts only if requested**

```bash
git status
```
If `examples/StressHarness/out/report.md` is tracked and changed, do NOT commit it unless the user explicitly asks — `out/` is run-output and not under source control by default.

---

## Self-review

**1. Spec coverage:**
- "MessageLedger singleton" — Task 3 ✓
- "PublishRecord / ConsumeRecord / LedgerSnapshot" — Task 2 ✓
- "Records every outbound publish (12 fields)" — Tasks 3 + 5 ✓
- "Records every inbound consume (5 fields)" — Tasks 3 + 7 ✓
- "IFlowKeyedSingleton.TryRemoveCompleted" — Task 3 ✓
- "LedgeredSender wrap at HarnessHost.BuildServices" — Tasks 5 + 6 ✓
- "Consume hook (middleware or per-handler)" — Task 7 chose per-handler via helper, documented why ✓
- "Four-quadrant classification" — Task 4 ✓
- "Per-publish-window breakdown" — Task 4 ✓
- "Per-pattern breakdown" — Task 4 ✓
- "20-row forensic table" — Task 4 ✓
- "Report section in markdown + JSON" — Task 8 ✓
- "Memory budget under 50 MB" — `TryRemoveCompleted` (Task 3) + spec's calculation ✓
- "Test coverage: 4 quadrants + reclamation + per-message redelivery + consumes-without-publish" — Task 4 has 8 tests covering all of these ✓
- "5-min chaos soak verification" — Task 9 ✓

**2. Placeholder scan:** No "TBD"/"TODO" entries in the plan body. The two implementation-flexibility notes (option a vs b for `IHeaderBearingOptions` shim; framework middleware vs per-handler hook for consume side) are explicit recommendations, not placeholders — both have a chosen default with the alternative documented.

**3. Type consistency:**
- `MessageLedger.RecordConsume` signature matches across Task 3 (definition), Task 5 (test fixture), Task 7 (helper call site). ✓
- `MessageLedgerAnalysis` constructor parameters match across Task 4 (definition + test fixtures), Task 8 (test fixture, markdown writer accessors). ✓
- `ChaosWindow` enum (existing) used consistently. ✓
- `IChaosClock` interface introduced in Task 5, used in Tasks 6 + 7. ✓

**4. Limitation explicitly carried into the plan:**
- Streaming chunks and routing-slip forward hops are framework-mediated and out of harness reach. The plan documents this in the scope-adjustment section, in `LedgeredSender.CreateStream` / `RouteAsync`, in `LedgerHandlerHelpers` (fallback to CorrelationId), and in Task 9 step 4 (acceptance: if `AckedButLost == 0` and `MissingFlows > 0` the loss is in framework paths and needs a follow-up plan).
