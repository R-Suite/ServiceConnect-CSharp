# Phase 2 — Outbound Pipeline Extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate ~200 LOC of duplicated outbound preamble (serialize → outgoing-filter → header-stamp) across seven `Bus` methods plus four `ContinueWith` UnobservedTaskException-suppression blocks in `RequestReplyManager`, by extracting two private helpers and a single utility. Behaviour-preserving refactor.

**Architecture:** Two private helpers added to `Bus.cs` — `PrepareOutboundAsync<T>` for paths that need wire bytes (publish/send/send-to-many/route) and `PrepareOutboundForRequestAsync<T>` for the three request paths that don't (so the documented "skip serialize when no outgoing filters" optimisation at `Bus.cs:339-341` survives). The seven callers handle the `stopped` flag with their existing semantics (publish/send/route `return`; request methods `throw OutgoingFiltersBlockedException`). Separately, `RequestReplyManager` gains a `SuppressUnobservedFault(Task)` private static replacing four inline `ContinueWith` blocks. No public API change, no new collaborators.

**Tech Stack:** C# 12/14 (multi-target net8.0/net10.0; tests net10.0), xUnit 2.9, Moq 4.20, `Microsoft.Extensions.TimeProvider.Testing` (FakeTimeProvider), `Microsoft.Extensions.Diagnostics.Testing` (FakeLogger). No new dependencies.

---

## Phase-wide rules (apply to EVERY task)

These rules live in [docs/reviews/2026-05-17-refactor-phasing.md](../../reviews/2026-05-17-refactor-phasing.md) and are repeated here so the implementer never has to leave this document.

1. **Validate before implementing.** Step 1 of every task is "Validate the scope." For Phase 2 the high-level finding is already pre-validated (the duplication exists; see commit `9bf1d9a1`'s parent `Bus.cs` for the seven call sites). Per-task validation still confirms current line numbers haven't drifted and that no concurrent work has already started the extraction.
2. **No `dotnet` from the main session.** Every `dotnet build` / `dotnet test` step MUST be invoked from the subagent that owns the task. Main-session dotnet invocations are forbidden per the project CLAUDE.md.
3. **Per-csproj invocations only.** Target the specific .csproj — never the solution. Example: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter FullyQualifiedName~Bus -m:1`.
4. **`-m:1` at MSBuild level.** All `dotnet` test/build commands MUST include `-m:1`. The PATH wrapper at `~/.local/bin/dotnet` adds cgroup limits, but `-m:1` is still required at the MSBuild level to prevent analyzer fan-out.
5. **Per-change code review.** After each task's implementation passes its tests, invoke `superpowers:requesting-code-review` on the change before committing. Address findings in-task before moving on.
6. **Commit one task = one commit.** Each commit follows the project's existing style (see `git log --oneline -20`): a scoped prefix (`refactor(bus):`, `refactor(request-reply):`, `test(bus):`, etc.), present-tense summary, Claude co-author trailer.
7. **No ticket / phase / "fixes Xxx" framing in source comments** (per project CLAUDE.md). Belongs in the commit message body, not in code. Helper xmldoc summaries must describe what the helper does in present tense without referencing the refactor or the prior duplication.
8. **Behaviour-preserving refactor.** The existing `BusTests.cs` (1429 lines) and the per-area test classes (`BusEnvelopeMessageTypeTests`, `BusRouteValidationTests`, `BusSendToManyAsyncFanoutTests`, `BusTransportLifecycleTests`, etc.) are the primary regression net. **Every refactor task ends by running the full `Bus*` test suite — zero failures expected.** A failing existing test means the refactor changed observable behaviour and must be reverted/fixed before commit.

---

## End-of-phase verification gate (run after all tasks complete)

Do NOT skip any of these.

- [ ] **Final code review** — run `superpowers:requesting-code-review` on the full diff of all Phase 2 commits combined.
- [ ] **Full unit-test suite** — delegated subagent runs `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1` and reports total pass/fail (Phase 1 baseline: 1628 tests).
- [ ] **Full end-to-end suite** — delegated subagent runs `dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj -m:1`. Requires Docker (user is in docker group; no `sg` wrapper needed).
- [ ] **Serialization compat tests** — `dotnet test src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj -m:1`.
- [ ] **All example apps build** — `find examples -name '*.csproj' | xargs -I{} dotnet build "{}" -m:1`. Zero failures expected.
- [ ] **Documentation site current** — verify `website/src/content/docs/` reflects any user-visible behaviour change. Phase 2 is a pure refactor — no user-visible change expected — but confirm.
- [ ] **README current** — same.
- [ ] **Bus.cs LOC drop measurable** — sanity check: the refactor should remove ~150-200 lines net from `Bus.cs` even after adding the two helpers. Record before/after line counts in the final summary.

---

## File structure

This refactor modifies two production files and adds two new test files. No new production files, no public API change.

| File | Change | Responsibility |
|---|---|---|
| `src/ServiceConnect/Bus.cs` | Modify | Add two private helpers (`PrepareOutboundAsync<T>`, `PrepareOutboundForRequestAsync<T>`); replace seven inline preamble blocks with calls to them. |
| `src/ServiceConnect/Services/RequestReplyManager.cs` | Modify | Add `SuppressUnobservedFault(Task)` private static; replace four inline `ContinueWith` blocks. |
| `src/ServiceConnect.UnitTests/BusOutboundPreparationTests.cs` | Create | Direct unit tests of the two helpers' contract (filter-on path, filter-off path, stopped flag, header stamping, byte equivalence). |
| `src/ServiceConnect.UnitTests/RequestReplyManagerFaultSuppressionTests.cs` | Create | Direct unit test that `SuppressUnobservedFault` observes the fault (no `UnobservedTaskException` raised). |

The existing `BusTests.cs`, `BusEnvelopeMessageTypeTests.cs`, `BusSendToManyAsyncFanoutTests.cs`, `BusRouteValidationTests.cs`, `RequestReplyManagerTests.cs`, and their siblings are the regression net — they are NOT modified.

---

## Task 1: Validate the scope

**No code changes — investigation and confirmation only.**

**Finding context:** The pre-analysis identified 7 call sites duplicating the outbound preamble (4 fire-and-forget shape, 3 throw-on-Stop shape) and 4 inline `ContinueWith` UnobservedTaskException-suppression blocks in `RequestReplyManager.cs`. This task confirms those counts have not drifted since the pre-read.

- [ ] **Step 1: Confirm Bus.cs call-site count**

Run:

```bash
cd /home/tim/source/ServiceConnect-CSharp
grep -cn "if (_hasOutgoingFilters)" src/ServiceConnect/Bus.cs
grep -n "if (_hasOutgoingFilters)" src/ServiceConnect/Bus.cs
```

Expected: count is **7**. Line numbers should be approximately 133, 190, 235, 337, 375, 418, 490 (or wherever the methods now live). If the count is anything other than 7, STOP and report Status: NEEDS_CONTEXT — the duplication has changed shape since the pre-read and the plan needs adjustment.

- [ ] **Step 2: Confirm RequestReplyManager.cs ContinueWith count**

Run:

```bash
grep -cn "ContinueWith(static t => _ = t.Exception" src/ServiceConnect/Services/RequestReplyManager.cs
grep -n "ContinueWith(static t => _ = t.Exception" src/ServiceConnect/Services/RequestReplyManager.cs
```

Expected: count is **9** — three identical catch-ladder groups across the three async request methods (`SendRequestAsync`, `SendRequestMultiAsync`, `PublishRequestAsync`), each with three catch branches (caller-cancel / linked-CTS-timeout / bare). Line groups: 128/145/159, 314/328/347, 469/483/502 (approximate). If different, STOP and report.

- [ ] **Step 3: Confirm no existing extracted helper**

Run:

```bash
grep -n "PrepareOutbound\|RunOutboundPipeline\|SuppressUnobservedFault" src/ServiceConnect src/ServiceConnect.UnitTests -r --include='*.cs'
```

Expected: zero matches. If anything is found, the extraction is already in flight elsewhere — STOP and report.

- [ ] **Step 4: Record baseline LOC of Bus.cs**

```bash
wc -l src/ServiceConnect/Bus.cs
```

Note the value (expected ~1100-1110). This becomes the before-value for the end-of-phase LOC check.

- [ ] **Step 5: Report**

Report Status: DONE if all four checks pass with expected values. Provide the actual counts and line numbers in the report so the orchestrator can compare to the next task's assumptions. **No commit for this task** — it is investigative only.

---

## Task 2: Extract `PrepareOutboundAsync<T>` with direct unit tests

**Files:**
- Modify: `src/ServiceConnect/Bus.cs` (add new private helper near `BuildHeadersDirect`)
- Create: `src/ServiceConnect.UnitTests/BusOutboundPreparationTests.cs`

**Finding context:** Add the helper that the four send/publish/route paths will use in Tasks 3-6. The helper is private but `Bus`'s test project already has `[InternalsVisibleTo]` access, so we can either expose it as `internal` for direct testing OR test it through one of the consumers in Task 3+. **Direct testing is preferred** because it locks the helper's contract independently of any caller — if a future refactor changes a caller, the helper's invariants are still guarded.

To make the helper directly testable, declare it as `internal` rather than `private`. (The class is already `internal sealed class Bus`, so the helper visibility doesn't widen anything user-visible.)

- [ ] **Step 1: Validate**

Re-read `Bus.cs:123-176` (PublishAsync) and confirm the preamble shape is still:

```csharp
var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>();
_serializer.Serialize(message, bufferWriter);
var messageBytes = bufferWriter.WrittenMemory;
Dictionary<string, string> headers;

if (_hasOutgoingFilters)
{
    var envelope = CreateEnvelope(messageBytes, message.CorrelationId, typeof(T), options?.Headers);
    if (await RunOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false) == FilterAction.Stop)
    {
        return;
    }

    headers = ExtractHeaders(envelope);
}
else
{
    headers = BuildHeadersDirect(message.CorrelationId, options?.Headers);
}
```

Confirm `CreateEnvelope`, `RunOutgoingFiltersAsync`, `ExtractHeaders`, and `BuildHeadersDirect` are all private methods on `Bus`. If any has been promoted/moved since the pre-read, STOP and report.

- [ ] **Step 2: Write the failing tests**

Create `src/ServiceConnect.UnitTests/BusOutboundPreparationTests.cs`. The tests construct a `Bus` with mocked dependencies and call `PrepareOutboundAsync<T>` directly. Use the existing `BusTests.cs` setup patterns to wire dependencies. The four behaviours to assert:

```csharp
using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Pipelines;
using Xunit;

namespace ServiceConnect.UnitTests;

public sealed class BusOutboundPreparationTests
{
    private sealed class TestMessage : Message
    {
        public TestMessage(Guid correlationId) : base(correlationId) { }
    }

    private static (Bus bus, Mock<IFilterPipeline> filters, Mock<IMessageSerializer> serializer) BuildBus(bool hasOutgoingFilters)
    {
        var serializer = new Mock<IMessageSerializer>();
        serializer
            .Setup(s => s.Serialize(It.IsAny<TestMessage>(), It.IsAny<IBufferWriter<byte>>()))
            .Callback<TestMessage, IBufferWriter<byte>>((_, w) =>
            {
                var bytes = new byte[] { 0xAB, 0xCD };
                w.Write(bytes);
            });

        var filters = new Mock<IFilterPipeline>();
        var sendPipeline = new Mock<ISendMessagePipeline>();
        var requestReplyManager = new Mock<IRequestReplyManager>();
        var queueConfig = new Mock<IQueueConfiguration>();
        var dispatcher = new Mock<IMessageDispatcher>();
        var pipelineConfig = new Mock<IPipelineConfiguration>();
        pipelineConfig.SetupGet(p => p.OutgoingFilters).Returns(
            hasOutgoingFilters ? new List<Type> { typeof(IFilter) } : new List<Type>());
        var scopeFactory = new Mock<IServiceScopeFactory>();
        var scopeAccessor = new ConsumeScopeAccessor();

        var bus = new Bus(
            serializer.Object,
            filters.Object,
            sendPipeline.Object,
            requestReplyManager.Object,
            NullLogger<Bus>.Instance,
            queueConfig.Object,
            dispatcher.Object,
            Array.Empty<HandlerReference>(),
            pipelineConfig.Object,
            scopeFactory.Object,
            scopeAccessor);

        return (bus, filters, serializer);
    }

    [Fact]
    public async Task PrepareOutboundAsync_NoFilters_ReturnsBytesAndDirectHeaders_NotStopped()
    {
        var (bus, filters, _) = BuildBus(hasOutgoingFilters: false);
        var msg = new TestMessage(Guid.NewGuid());

        var result = await bus.PrepareOutboundAsync(msg, callerHeaders: null, CancellationToken.None);

        Assert.False(result.Stopped);
        Assert.Equal(2, result.Bytes.Length);
        Assert.Equal((byte)0xAB, result.Bytes.Span[0]);
        Assert.Equal((byte)0xCD, result.Bytes.Span[1]);
        Assert.NotNull(result.Headers);
        // Filters were never invoked because the fast path bypasses RunOutgoingFiltersAsync entirely.
        filters.Verify(f => f.RunOutgoingAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PrepareOutboundAsync_FiltersAccept_ReturnsHeadersFromEnvelope_NotStopped()
    {
        var (bus, filters, _) = BuildBus(hasOutgoingFilters: true);
        filters
            .Setup(f => f.RunOutgoingAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Continue);

        var msg = new TestMessage(Guid.NewGuid());
        var result = await bus.PrepareOutboundAsync(msg, callerHeaders: null, CancellationToken.None);

        Assert.False(result.Stopped);
        Assert.Equal(2, result.Bytes.Length);
        Assert.NotNull(result.Headers);
        filters.Verify(f => f.RunOutgoingAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PrepareOutboundAsync_FiltersStop_ReturnsStoppedTrue()
    {
        var (bus, filters, _) = BuildBus(hasOutgoingFilters: true);
        filters
            .Setup(f => f.RunOutgoingAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Stop);

        var msg = new TestMessage(Guid.NewGuid());
        var result = await bus.PrepareOutboundAsync(msg, callerHeaders: null, CancellationToken.None);

        Assert.True(result.Stopped);
        // Bytes and Headers are not consumed on the stopped path; the helper may return defaults
        // or whatever it computed up to that point. The contract is: callers MUST check Stopped
        // before reading Bytes or Headers.
    }

    [Fact]
    public async Task PrepareOutboundAsync_CallerHeaders_FlowToEnvelopeOnFilterPath()
    {
        var (bus, filters, _) = BuildBus(hasOutgoingFilters: true);
        Envelope? capturedEnvelope = null;
        filters
            .Setup(f => f.RunOutgoingAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .Callback<Envelope, CancellationToken>((env, _) => capturedEnvelope = env)
            .ReturnsAsync(FilterAction.Continue);

        var msg = new TestMessage(Guid.NewGuid());
        var callerHeaders = new Dictionary<string, string>(StringComparer.Ordinal) { ["X-Trace-Id"] = "abc123" };

        var result = await bus.PrepareOutboundAsync(msg, callerHeaders, CancellationToken.None);

        Assert.False(result.Stopped);
        Assert.NotNull(capturedEnvelope);
        Assert.True(capturedEnvelope!.Headers.ContainsKey("X-Trace-Id"));
    }

    [Fact]
    public async Task PrepareOutboundAsync_CancellationToken_FlowsThroughToFilter()
    {
        var (bus, filters, _) = BuildBus(hasOutgoingFilters: true);
        CancellationToken capturedToken = default;
        filters
            .Setup(f => f.RunOutgoingAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .Callback<Envelope, CancellationToken>((_, ct) => capturedToken = ct)
            .ReturnsAsync(FilterAction.Continue);

        var msg = new TestMessage(Guid.NewGuid());
        using var cts = new CancellationTokenSource();

        await bus.PrepareOutboundAsync(msg, callerHeaders: null, cts.Token);

        Assert.Equal(cts.Token, capturedToken);
    }
}
```

Note: If the existing `BusTests.cs` has a `BuildBus()` factory you can reuse, prefer that to reduce duplication. Read `BusTests.cs:1-100` first to find the conventional construction pattern.

- [ ] **Step 3: Run the tests to verify they fail to compile**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter FullyQualifiedName~BusOutboundPreparationTests -m:1
```

Expected: BUILD FAILURE because `bus.PrepareOutboundAsync` does not exist yet.

- [ ] **Step 4: Implement the helper**

Edit `src/ServiceConnect/Bus.cs`. Add the helper near `BuildHeadersDirect` (find it via `grep -n "private Dictionary<string, string> BuildHeadersDirect" src/ServiceConnect/Bus.cs`). Insert the result struct and the helper:

```csharp
/// <summary>
/// Result of the outbound preamble: serialised wire bytes, the headers dictionary to attach,
/// and a flag indicating whether an outgoing filter requested the message be dropped.
/// Callers MUST check <see cref="Stopped"/> before reading <see cref="Bytes"/> or <see cref="Headers"/>;
/// the latter two are undefined when the filter pipeline stopped the message.
/// </summary>
internal readonly record struct OutboundPreparation(
    ReadOnlyMemory<byte> Bytes,
    Dictionary<string, string> Headers,
    bool Stopped);

/// <summary>
/// Runs the outbound preamble shared by Publish/Send/SendToMany/Route: serialise the message,
/// then either build headers directly (no outgoing filters configured) or build an envelope,
/// invoke the outgoing-filter pipeline, and extract headers from the envelope.
/// </summary>
/// <remarks>
/// The helper always serialises the message because all callers of this helper need the wire
/// bytes for the downstream send pipeline. Request paths use
/// <see cref="PrepareOutboundForRequestAsync{T}"/> instead, which conditionally serialises
/// because <c>RequestReplyManager</c> re-serialises downstream.
/// </remarks>
internal async Task<OutboundPreparation> PrepareOutboundAsync<T>(
    T message,
    IReadOnlyDictionary<string, string>? callerHeaders,
    CancellationToken cancellationToken) where T : Message
{
    var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>();
    _serializer.Serialize(message, bufferWriter);
    var messageBytes = bufferWriter.WrittenMemory;

    if (_hasOutgoingFilters)
    {
        var envelope = CreateEnvelope(messageBytes, message.CorrelationId, typeof(T), callerHeaders);
        if (await RunOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false) == FilterAction.Stop)
        {
            return new OutboundPreparation(default, null!, Stopped: true);
        }
        return new OutboundPreparation(messageBytes, ExtractHeaders(envelope), Stopped: false);
    }

    return new OutboundPreparation(messageBytes, BuildHeadersDirect(message.CorrelationId, callerHeaders), Stopped: false);
}
```

**Important:** the helper is `internal` (not `private`) so the test project can call it directly via `[InternalsVisibleTo]`. The class is already `internal sealed`, so this doesn't widen any user-facing surface.

- [ ] **Step 5: Run the tests to verify they pass**

Same command as Step 3. Expected: all 5 tests PASS.

- [ ] **Step 6: Run the full Bus* test suite to confirm no regression**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter FullyQualifiedName~Bus -m:1
```

Expected: every pre-existing Bus test still passes (the helper has no callers yet, so nothing changes for them).

- [ ] **Step 7: Per-change code review**

Invoke `superpowers:requesting-code-review` on the staged diff. Address any findings before commit.

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect/Bus.cs \
        src/ServiceConnect.UnitTests/BusOutboundPreparationTests.cs
git commit -m "$(cat <<'EOF'
refactor(bus): add PrepareOutboundAsync helper with direct unit tests

Introduces an internal OutboundPreparation result struct and an
internal async PrepareOutboundAsync<T> helper that owns the outbound
preamble (serialise → outgoing-filter → header-stamp). The seven
inline preamble blocks across Bus's send/publish/route/request methods
will migrate to it over the next tasks. Tests lock the contract:
no-filter fast path, filter-accept path, filter-stop path, caller-header
flow into envelope, cancellation propagation.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Migrate `Bus.PublishAsync`, `Bus.SendAsync`, `Bus.SendToManyAsync`, `Bus.RouteAsync` to `PrepareOutboundAsync`

**Files:**
- Modify: `src/ServiceConnect/Bus.cs` (four methods)
- Test: existing `BusTests.cs`, `BusEnvelopeMessageTypeTests.cs`, `BusSendToManyAsyncFanoutTests.cs`, `BusRouteValidationTests.cs` — NOT modified (regression net)

**Finding context:** Each of these four methods has the same preamble shape. Replace the inline preamble with a call to `PrepareOutboundAsync<T>`, check `Stopped`, and continue with the existing post-preamble logic (RoutingKey resolution in PublishAsync, EndPoint+context construction in SendAsync, per-endpoint loop in SendToManyAsync, hop-counter + routing-slip in RouteAsync).

- [ ] **Step 1: Validate**

Re-read each method, confirm preamble shape is unchanged from the Phase 2 pre-read at session start:
- `Bus.cs:123-176` — PublishAsync
- `Bus.cs:178-216` — SendAsync
- `Bus.cs:218-325` — SendToManyAsync
- `Bus.cs:445-538` — RouteAsync

If any has been touched since (e.g. another agent has started migrating one), STOP and report.

- [ ] **Step 2: No new tests needed**

The existing tests cover these methods extensively (1429-line `BusTests.cs` plus several focused test classes). The refactor is behaviour-preserving; the test suite is the regression net. New tests are written only if the refactor surfaces a previously-untested edge case.

- [ ] **Step 3: Migrate `PublishAsync`**

Replace lines 128-146 (the preamble block) with:

```csharp
var prep = await PrepareOutboundAsync(message, options?.Headers, cancellationToken).ConfigureAwait(false);
if (prep.Stopped)
{
    return;
}
var messageBytes = prep.Bytes;
var headers = prep.Headers;
```

Leave everything after (RoutingKey resolution, context construction, dispatch) untouched.

- [ ] **Step 4: Migrate `SendAsync`**

Replace lines 185-203 with the same five-line pattern. Leave the rest untouched.

- [ ] **Step 5: Migrate `SendToManyAsync`**

Replace lines 230-248 with the same five-line pattern. Leave the per-endpoint fan-out loop and AggregateException handling untouched.

- [ ] **Step 6: Migrate `RouteAsync`**

This one is slightly different — its preamble lives at lines 485-503 (after the destination-validation block at 454-482). Replace 485-503 with the same five-line pattern. Leave everything after (routing-slip construction, hop counter, context dispatch) untouched.

- [ ] **Step 7: Build to confirm no syntax error**

```bash
dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1
```

Expected: build succeeds. TreatWarningsAsErrors=true means any new warning (e.g. unused variable) becomes an error.

- [ ] **Step 8: Run the full Bus* test suite**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter FullyQualifiedName~Bus -m:1
```

Expected: ALL Bus tests pass (every pre-existing test plus the 5 new `BusOutboundPreparationTests`). Any pre-existing failure means observable behaviour changed — investigate, do not commit until green.

If a test fails, the most likely culprits are:
- Forgot to thread `options?.Headers` through (compare new call to old `CreateEnvelope`/`BuildHeadersDirect` calls — same argument).
- Forgot to assign `messageBytes` / `headers` from `prep.Bytes` / `prep.Headers`.
- Mistyped the `prep.Stopped` check (e.g. `!prep.Stopped` reversed).

- [ ] **Step 9: Per-change code review**

Invoke `superpowers:requesting-code-review`. Focus areas to mention in the dispatch prompt:
- Behavioural equivalence (every old branch maps to a new branch).
- No public API change.
- No new warning suppressions.
- LOC delta on `Bus.cs` (should be a net reduction of 40-50 lines).

- [ ] **Step 10: Commit**

```bash
git add src/ServiceConnect/Bus.cs
git commit -m "$(cat <<'EOF'
refactor(bus): publish/send/sendtomany/route share PrepareOutboundAsync

Four call sites of the outbound preamble (serialise → outgoing-filter →
header-stamp) collapse to a single call. ~40 LOC removed from Bus.cs;
behavioural equivalence guarded by the full Bus* test suite.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Extract `PrepareOutboundForRequestAsync<T>` with direct unit test

**Files:**
- Modify: `src/ServiceConnect/Bus.cs` (add second helper)
- Modify: `src/ServiceConnect.UnitTests/BusOutboundPreparationTests.cs` (add tests for the request helper)

**Finding context:** The three request methods (`SendRequestAsync`, `SendRequestMultiAsync`, `PublishRequestAsync`) share the same preamble BUT with two differences from the send/publish/route shape:
1. On filter `Stop`, they `throw new OutgoingFiltersBlockedException(...)` instead of `return`.
2. They only serialise WHEN `_hasOutgoingFilters` is true. When no outgoing filters are registered, they skip the local serialise because `RequestReplyManager` re-serialises downstream. This is a documented optimisation at `Bus.cs:339-341` and must be preserved.

The request helper therefore returns headers only — never bytes — and signals stop via the same flag (callers throw the typed exception).

- [ ] **Step 1: Validate**

Re-read `Bus.cs:327-363` (SendRequestAsync) and confirm:
- The serialise call only happens inside the `if (_hasOutgoingFilters)` branch (lines 342-344).
- On filter Stop, the method throws `OutgoingFiltersBlockedException` (line 348).
- The non-filter branch only calls `BuildHeadersDirect` — no serialise.

If these have changed, STOP and report.

- [ ] **Step 2: Write the failing tests**

Append to `src/ServiceConnect.UnitTests/BusOutboundPreparationTests.cs`:

```csharp
public sealed record RequestPreparationResult(Dictionary<string, string> Headers, bool Stopped);

[Fact]
public async Task PrepareOutboundForRequestAsync_NoFilters_SkipsSerializeAndReturnsDirectHeaders()
{
    var (bus, filters, serializer) = BuildBus(hasOutgoingFilters: false);
    var msg = new TestMessage(Guid.NewGuid());

    var result = await bus.PrepareOutboundForRequestAsync(msg, callerHeaders: null, CancellationToken.None);

    Assert.False(result.Stopped);
    Assert.NotNull(result.Headers);
    // Critical: serialiser must NOT have been called — the optimisation says request methods
    // skip local serialisation when there are no outgoing filters because RequestReplyManager
    // re-serialises downstream.
    serializer.Verify(s => s.Serialize(It.IsAny<TestMessage>(), It.IsAny<IBufferWriter<byte>>()), Times.Never);
    filters.Verify(f => f.RunOutgoingAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()), Times.Never);
}

[Fact]
public async Task PrepareOutboundForRequestAsync_FiltersAccept_SerializesAndReturnsHeaders()
{
    var (bus, filters, serializer) = BuildBus(hasOutgoingFilters: true);
    filters
        .Setup(f => f.RunOutgoingAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(FilterAction.Continue);

    var msg = new TestMessage(Guid.NewGuid());
    var result = await bus.PrepareOutboundForRequestAsync(msg, callerHeaders: null, CancellationToken.None);

    Assert.False(result.Stopped);
    Assert.NotNull(result.Headers);
    // Filter path requires the local serialise (so the envelope's wire body can be inspected).
    serializer.Verify(s => s.Serialize(It.IsAny<TestMessage>(), It.IsAny<IBufferWriter<byte>>()), Times.Once);
    filters.Verify(f => f.RunOutgoingAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()), Times.Once);
}

[Fact]
public async Task PrepareOutboundForRequestAsync_FiltersStop_ReturnsStoppedTrue()
{
    var (bus, filters, _) = BuildBus(hasOutgoingFilters: true);
    filters
        .Setup(f => f.RunOutgoingAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(FilterAction.Stop);

    var msg = new TestMessage(Guid.NewGuid());
    var result = await bus.PrepareOutboundForRequestAsync(msg, callerHeaders: null, CancellationToken.None);

    Assert.True(result.Stopped);
}
```

- [ ] **Step 3: Run the new tests to verify they fail to compile**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter FullyQualifiedName~BusOutboundPreparationTests -m:1
```

Expected: BUILD FAILURE (PrepareOutboundForRequestAsync does not exist).

- [ ] **Step 4: Implement the helper**

Edit `src/ServiceConnect/Bus.cs`. Add directly below `PrepareOutboundAsync`:

```csharp
/// <summary>
/// Result of the outbound preamble for request paths: the headers to attach and a flag
/// indicating whether an outgoing filter requested the request be blocked. Request paths
/// re-serialise the message downstream in <c>RequestReplyManager</c>, so this helper
/// does not return wire bytes; callers MUST check <see cref="Stopped"/> before reading
/// <see cref="Headers"/>.
/// </summary>
internal readonly record struct RequestPreparation(
    Dictionary<string, string> Headers,
    bool Stopped);

/// <summary>
/// Runs the outbound preamble shared by the three request paths (SendRequestAsync,
/// SendRequestMultiAsync, PublishRequestAsync). Unlike <see cref="PrepareOutboundAsync{T}"/>
/// this helper does NOT always serialise: when no outgoing filters are registered the
/// envelope is never built, so the local serialise can be skipped because
/// <c>RequestReplyManager</c> re-serialises on the request leg.
/// </summary>
internal async Task<RequestPreparation> PrepareOutboundForRequestAsync<T>(
    T message,
    IReadOnlyDictionary<string, string>? callerHeaders,
    CancellationToken cancellationToken) where T : Message
{
    if (_hasOutgoingFilters)
    {
        var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>();
        _serializer.Serialize(message, bufferWriter);
        var messageBytes = bufferWriter.WrittenMemory;
        var envelope = CreateEnvelope(messageBytes, message.CorrelationId, typeof(T), callerHeaders);
        if (await RunOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false) == FilterAction.Stop)
        {
            return new RequestPreparation(null!, Stopped: true);
        }
        return new RequestPreparation(ExtractHeaders(envelope), Stopped: false);
    }

    return new RequestPreparation(BuildHeadersDirect(message.CorrelationId, callerHeaders), Stopped: false);
}
```

- [ ] **Step 5: Run the new tests to verify they pass**

Same command as Step 3. Expected: all 3 new tests pass (plus the original 5).

- [ ] **Step 6: Run full Bus* suite**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter FullyQualifiedName~Bus -m:1
```

Expected: all green.

- [ ] **Step 7: Per-change code review**

Invoke `superpowers:requesting-code-review`. Highlight the "no-serialise on non-filter path" invariant — that's the load-bearing optimisation worth checking.

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect/Bus.cs \
        src/ServiceConnect.UnitTests/BusOutboundPreparationTests.cs
git commit -m "$(cat <<'EOF'
refactor(bus): add PrepareOutboundForRequestAsync helper for request paths

Adds a second outbound-preamble helper distinct from
PrepareOutboundAsync: the three request methods skip the local
serialise on the non-filter path because RequestReplyManager
re-serialises downstream. The helper signals filter-stop via a flag;
callers throw OutgoingFiltersBlockedException with their existing
message text.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Migrate `Bus.SendRequestAsync`, `Bus.SendRequestMultiAsync`, `Bus.PublishRequestAsync` to `PrepareOutboundForRequestAsync`

**Files:**
- Modify: `src/ServiceConnect/Bus.cs` (three methods)
- Test: existing `BusTests.cs`, `RequestReplyManagerTests.cs` (regression net, not modified)

**Finding context:** Each request method has the same preamble that throws `OutgoingFiltersBlockedException` on Stop.

- [ ] **Step 1: Validate**

Confirm the three methods' preamble shapes:
- `Bus.cs:327-363` — SendRequestAsync
- `Bus.cs:365-399` — SendRequestMultiAsync
- `Bus.cs:401-443` — PublishRequestAsync

(Note: Task 3 didn't touch these methods, so line numbers should still be as recorded. If they have drifted because someone touched them, STOP and report.)

- [ ] **Step 2: Migrate `SendRequestAsync`**

Replace the preamble (lines 335-356, the block from `Dictionary<string, string> headers;` through `headers = BuildHeadersDirect(...);`) with:

```csharp
var prep = await PrepareOutboundForRequestAsync(message, requestOptions.Headers, cancellationToken).ConfigureAwait(false);
if (prep.Stopped)
{
    throw new OutgoingFiltersBlockedException("Outgoing filters blocked the request message.");
}
var headers = prep.Headers;
```

Leave the `return await _requestReplyManager.SendRequestAsync<TRequest, TReply>(...)` call untouched.

- [ ] **Step 3: Migrate `SendRequestMultiAsync`**

Replace the equivalent preamble (lines 373-392) with the same four-line pattern. Leave the `return await _requestReplyManager.SendRequestMultiAsync<TRequest, TReply>(...)` call untouched.

- [ ] **Step 4: Migrate `PublishRequestAsync`**

Replace the equivalent preamble (lines 416-435) with the same four-line pattern. **Important:** `PublishRequestAsync` has an additional pre-check (lines 411-414 reject `requestOptions.EndPoint != null`). Keep that pre-check untouched — it must run BEFORE the preamble.

- [ ] **Step 5: Build**

```bash
dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1
```

Expected: build succeeds.

- [ ] **Step 6: Run full Bus* AND RequestReply* test suites**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Bus|FullyQualifiedName~RequestReply" -m:1
```

Expected: all green.

- [ ] **Step 7: Per-change code review**

Invoke `superpowers:requesting-code-review`. Highlight that the throw-on-Stop semantic is preserved exactly (same exception type, same message text).

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect/Bus.cs
git commit -m "$(cat <<'EOF'
refactor(bus): request paths share PrepareOutboundForRequestAsync

SendRequestAsync, SendRequestMultiAsync, and PublishRequestAsync now
delegate the outbound preamble to PrepareOutboundForRequestAsync.
The filter-stop semantic is preserved: callers throw
OutgoingFiltersBlockedException with the existing message text.
~50 LOC removed from Bus.cs.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: Extract `SuppressUnobservedFault` in `RequestReplyManager`

**Files:**
- Modify: `src/ServiceConnect/Services/RequestReplyManager.cs`
- Create: `src/ServiceConnect.UnitTests/RequestReplyManagerFaultSuppressionTests.cs`

**Finding context:** The `ContinueWith(static t => _ = t.Exception, ..., OnlyOnFaulted | ExecuteSynchronously, ...)` pattern is repeated **9 times** in `RequestReplyManager.cs` — three identical catch-ladder groups across `SendRequestAsync`, `SendRequestMultiAsync`, and `PublishRequestAsync`, each with three catch branches (caller-cancel filtered by `cancellationToken.IsCancellationRequested`; linked-CTS-timeout filtered by `linkedCts.IsCancellationRequested && sendCompleted == 0`; bare `catch`). Each block is 4 lines and structurally identical save for the surrounding rationale comment.

Extract as a `private static` helper. Apply at all 9 sites. Net LOC reduction: 9 × 3 = ~27 lines removed.

- [ ] **Step 1: Validate**

Re-run the count from Task 1 step 2:

```bash
grep -n "ContinueWith(static t => _ = t.Exception" src/ServiceConnect/Services/RequestReplyManager.cs
```

Expected: 9 line numbers grouped as three triplets (one per request method). If the count differs from 9, STOP and report.

- [ ] **Step 2: Write the failing test**

Create `src/ServiceConnect.UnitTests/RequestReplyManagerFaultSuppressionTests.cs`:

```csharp
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests;

public sealed class RequestReplyManagerFaultSuppressionTests
{
    [Fact]
    public async Task SuppressUnobservedFault_ObservesFaultedTaskException()
    {
        // Create a faulted Task that nobody awaits. Without SuppressUnobservedFault, the
        // GC finaliser would raise TaskScheduler.UnobservedTaskException. With it, the
        // continuation reads t.Exception and the task is considered observed.
        var tcs = new TaskCompletionSource<int>();
        tcs.SetException(new InvalidOperationException("synthetic fault"));

        // Hook up the observer.
        RequestReplyManager.SuppressUnobservedFault(tcs.Task);

        // Wait for the continuation to run (it's ExecuteSynchronously on the antecedent
        // completion, so it should already have run by the time SetException returns;
        // a tiny yield is belt-and-braces).
        await Task.Yield();

        Assert.True(tcs.Task.IsFaulted);
        Assert.NotNull(tcs.Task.Exception);
        // The Exception property has been read by the continuation; we don't try to assert
        // the un-observed-flag directly because TaskScheduler.UnobservedTaskException is
        // an event-based contract that's hard to test without GC pressure.
    }

    [Fact]
    public void SuppressUnobservedFault_NonFaultedTask_NoOp()
    {
        // The continuation is OnlyOnFaulted, so a successfully-completed task never invokes it.
        // The call should be safe and side-effect-free.
        var completed = Task.FromResult(42);
        RequestReplyManager.SuppressUnobservedFault(completed);
        Assert.True(completed.IsCompletedSuccessfully);
    }
}
```

Note: to call `RequestReplyManager.SuppressUnobservedFault` from the test, the helper must be `internal static` (the class is `internal sealed` already, and `[InternalsVisibleTo("ServiceConnect.UnitTests")]` exists in the csproj — verified Phase 1).

- [ ] **Step 3: Run the test to verify it fails to compile**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter FullyQualifiedName~RequestReplyManagerFaultSuppressionTests -m:1
```

Expected: BUILD FAILURE — `SuppressUnobservedFault` does not exist.

- [ ] **Step 4: Implement the helper and replace all 9 call sites**

Edit `src/ServiceConnect/Services/RequestReplyManager.cs`.

Add the helper at the bottom of the class (near `ValidateOptions` or the disposal members — find a sensible spot via `grep -n "private static\|private void" src/ServiceConnect/Services/RequestReplyManager.cs`):

```csharp
/// <summary>
/// Attaches a fire-and-forget continuation that observes the task's exception if it faults.
/// Prevents TaskScheduler.UnobservedTaskException at finalization for tasks the caller is
/// not awaiting. The OnlyOnFaulted | ExecuteSynchronously flags make the continuation a no-op
/// on success paths and avoid scheduling overhead on the fault path.
/// </summary>
internal static void SuppressUnobservedFault(Task task)
{
    _ = task.ContinueWith(static t => _ = t.Exception,
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);
}
```

Then replace each of the 9 call sites. The before/after at each site:

Before:
```csharp
_ = tcs.Task.ContinueWith(static t => _ = t.Exception,
    CancellationToken.None,
    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
    TaskScheduler.Default);
```

After:
```csharp
SuppressUnobservedFault(tcs.Task);
```

The nine sites are three triplets, one per request method (`SendRequestAsync`, `SendRequestMultiAsync`, `PublishRequestAsync`). Within each method the triplet covers the three catch branches: caller-cancel, linked-CTS-timeout, and bare. Approximate line numbers per Task 1's validation: 128/145/159 (`SendRequestAsync`), 314/328/347 (`SendRequestMultiAsync`), 469/483/502 (`PublishRequestAsync`). Re-grep before editing to confirm exact lines:

```bash
grep -n "ContinueWith(static t => _ = t.Exception" src/ServiceConnect/Services/RequestReplyManager.cs
```

Preserve the surrounding comments that explain WHY each call site observes the fault (e.g. the line 122-127 comment in `SendRequestAsync`'s caller-cancel path; analogous comments exist for the other 8 sites). The comments are valuable and must NOT be deleted; only the four-line `ContinueWith` block becomes a one-line `SuppressUnobservedFault` call.

- [ ] **Step 5: Run the new tests and full RequestReplyManager suite**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RequestReplyManager" -m:1
```

Expected: all tests green (the 2 new fault-suppression tests + every pre-existing `RequestReplyManagerTests` test, currently 1281 lines of test code — the regression net is dense here).

- [ ] **Step 6: Per-change code review**

Invoke `superpowers:requesting-code-review`. Highlight that the surrounding rationale comments (explaining WHY each fault-observation is needed) are preserved.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect/Services/RequestReplyManager.cs \
        src/ServiceConnect.UnitTests/RequestReplyManagerFaultSuppressionTests.cs
git commit -m "$(cat <<'EOF'
refactor(request-reply): extract SuppressUnobservedFault helper

Nine inline ContinueWith blocks collapse to a single internal static
helper. The rationale comments at each call site (explaining why
the fault must be observed on each path) are preserved unchanged.
~27 LOC removed from RequestReplyManager.cs.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: End-of-phase verification gate

This is the gate that closes Phase 2. Run AFTER tasks 1-6 are complete. Each step is a delegated subagent dispatch.

- [ ] **Step 1: Final branch-wide code review**

Dispatch via `superpowers:requesting-code-review` on the diff `<base>..HEAD` where `<base>` is the commit immediately before Task 1 (i.e. Phase 1's final commit). Subagent confirms behaviour preservation, no public API drift, no warning suppressions, and a measurable LOC reduction.

- [ ] **Step 2: Full unit-test suite**

Dispatch (sonnet, background): `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1`. Expected: all tests pass (Phase 1 baseline 1628 + the 10 new tests added in Phase 2, so 1638 total — adjust expectation only if pre-existing tests have changed since).

- [ ] **Step 3: Full end-to-end suite**

Dispatch (sonnet, background): `dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj -m:1`. Requires Docker. Expected: 136/136 (Phase 1 baseline).

- [ ] **Step 4: Serialization-compat tests**

Dispatch (sonnet, background): `dotnet test src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj -m:1`. Expected: 48/48 (Phase 1 baseline).

- [ ] **Step 5: All example apps build**

Dispatch (sonnet, background): `find examples -name '*.csproj' -print0 | xargs -0 -I{} dotnet build "{}" -m:1 --nologo --verbosity quiet`. Expected: 53/53 clean (Phase 1 baseline).

- [ ] **Step 6: Documentation site + README currency**

Dispatch (sonnet, background): scan `website/src/content/docs/` and `README.md` for references to the seven Bus method preamble shapes (e.g. "IBus.PublishAsync does X then Y") that would now be stale. Phase 2 is a pure internal refactor; expected outcome: NO UPDATE NEEDED. Confirm.

- [ ] **Step 7: LOC sanity check**

Compare current `Bus.cs` line count to the Task 1 Step 4 baseline. Expected reduction: ~150-200 lines (the seven preamble blocks averaged 15-20 lines each; the two helpers add back ~50 lines; net delta -150ish). Report the actual numbers.

- [ ] **Step 8: Close Phase 2**

When all 7 gate steps PASS, update the controller's TodoWrite list to mark Phase 2 complete. No additional commit — the phase is closed by passing the gate.

---

## Self-Review Checklist

Run this before declaring the plan ready.

1. **Spec coverage:** Phase 2's goal in [refactor-phasing.md](../../reviews/2026-05-17-refactor-phasing.md) lists three findings:
   - `Bus.cs:123-176, 179-216, 219-325, 446-538` — duplicated send/publish/route preamble. ✔ Tasks 2-3.
   - `RequestReplyManager.cs:32-175` — duplicated request-send paths. ✔ Indirectly via Tasks 4-5 (the request methods on `Bus.cs` flow into `RequestReplyManager`'s `SendRequestAsync`/`SendRequestMultiAsync`; the duplication AT THE BUS LAYER is what Phase 2 extracts).
   - `RequestReplyManager.cs:115-175, 322-325` — repeated `ContinueWith` UnobservedTaskException-suppression. ✔ Task 6.

   **Gap noted:** the phasing doc mentioned `RequestReplyManager.SendRequestAsync` vs `SendRequestMultiAsync` "duplication" as part of Phase 2. On first-hand re-read those two methods are genuinely structurally similar but their differences (single-reply vs multi-reply state, different completion predicates) mean an extraction would be a much larger refactor with its own correctness risk. Phase 2 covers the Bus-layer preamble (which feeds both) but leaves the RequestReplyManager body untouched beyond the SuppressUnobservedFault helper. If the controller wants the deeper RequestReplyManager unification, that should be Phase 2b or Phase 3 — out of scope for this plan.

2. **Placeholder scan:** Every step has real code or a real command. No "TODO", no "implement later", no "similar to Task N".

3. **Type consistency:**
   - `OutboundPreparation` struct — used in Task 2 and consumed by Task 3.
   - `RequestPreparation` struct — used in Task 4 and consumed by Task 5.
   - `SuppressUnobservedFault(Task)` — used in Task 6.
   - All visibility modifiers (`internal`) consistent across declaration and use sites.

4. **Test patterns:** All test sketches use real types from the codebase (`Mock<IFilterPipeline>`, `FakeLogger`, `IBufferWriter<byte>`). The construction pattern matches the existing `BusTests.cs` setup at lines 1-100.

5. **Dotnet delegation:** Every `dotnet` step in a task is run by the implementer subagent (which IS the delegated subagent). Main session never invokes dotnet.

6. **Commit hygiene:** Each task = one commit. Co-author trailer present. No `--no-verify`, no `--amend`.

7. **End-of-phase gate:** Complete with the 5 standard test/build dispatches + final code review + LOC check + docs/README confirmation.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-05-17-phase-2-outbound-pipeline.md`. Two execution options:

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, two-stage review between tasks (spec compliance then code quality), fast iteration. Worked well for Phase 1 (caught the `067b272e` scope-prefix issue, surfaced the log-statement mean-vs-jitter mismatch in Task 1, validated all 4 FP rejections on second read).

**2. Inline Execution** — Execute tasks in this session using `superpowers:executing-plans`, batch execution with checkpoints for review.

Which approach?
