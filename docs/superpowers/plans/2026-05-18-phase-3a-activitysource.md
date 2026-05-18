# Phase 3a — ServiceConnectActivitySource Split Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split the 797-line `ServiceConnectActivitySource` static class into three cohesive internal-static siblings: keep `ServiceConnectActivitySource` as the public activity-creation surface, move all W3C trace-propagation members to a new `TraceContextPropagation` internal-static class, and move the two `TryEnrich` overloads to a new `TelemetryEnrichment` internal-static class. Behaviour-preserving refactor.

**Architecture:** Three files in `src/ServiceConnect.Telemetry/`. The existing public API (`Publish`, `Consume`, `Send`, `SetError`, `TryGetExistingContext`, `ActivitySourceName`) stays unchanged on `ServiceConnectActivitySource` — public callers see no surface change. The `_inboundTraceFallback` AsyncLocal and its `InboundTraceSnapshot` / `InboundTraceFallbackScope` types move to `TraceContextPropagation` along with the W3C header parsing/injection methods (`ExtractTraceIdAndState`, `InjectTraceContext`, `HasTraceparentHeader`, `InjectHeader`, `TryResolveFallbackParentContext`, `SetInboundTraceFallback`, the `_warnedAboutCarrierShape` once-flag, and the `InvokeInjectHeaderForTest` / `ResetCarrierWarnedFlagForTest` test seams). The two `TryEnrich` overloads move to `TelemetryEnrichment`; the public `Publish`/`Consume`/`Send` callers update their internal call sites to invoke `TelemetryEnrichment.TryEnrich` instead. `Truncate` stays on `ServiceConnectActivitySource` (it's used inline by every tag-set call site in the activity-creation methods).

**Tech Stack:** C# 12/14 (multi-target net8.0/net10.0; tests net10.0), xUnit 2.9, Moq 4.20. No new dependencies.

---

## Phase-wide rules (apply to EVERY task)

Same as Phase 1, 2, and 3c — see [docs/reviews/2026-05-17-refactor-phasing.md](../../reviews/2026-05-17-refactor-phasing.md). Summary:

1. **Validate before implementing.** Step 1 of every task. If a finding is no longer real, append to `docs/reviews/2026-05-17-rejected-findings.md`.
2. **`dotnet` only from implementer subagent**, never main session. **Always `-m:1`** at MSBuild level. **Per-csproj only** — never the solution.
3. **Per-change `superpowers:requesting-code-review`** before commit.
4. **One task = one commit.** Scoped prefix (`refactor(telemetry):`, etc.), present-tense, Claude co-author trailer.
5. **No ticket / phase / "fixes Xxx" framing in source comments** (per project CLAUDE.md).
6. **Behaviour-preserving refactor.** Existing `ServiceConnectActivitySourceTests.cs` (1399 lines, ~80+ `[Fact]` tests) is the regression net.

---

## End-of-phase verification gate

- [ ] Final code review on the full Phase 3a diff
- [ ] Unit-test suite (`-m:1`)
- [ ] E2E suite (`-m:1`, Docker)
- [ ] Serialization-compat tests (`-m:1`)
- [ ] All example apps build (`-m:1` each)
- [ ] Docs/README current (Phase 3a is internal-only — expected NO UPDATE; the `OutgoingFiltersBlockedException` feature-level mention in `observability.mdx` is unrelated)
- [ ] File-size sanity: `ServiceConnectActivitySource.cs` should drop from ~797 LOC to ~350-400 LOC; new `TraceContextPropagation.cs` ~250 LOC; new `TelemetryEnrichment.cs` ~80 LOC; total LOC roughly flat (~700-750)

---

## File structure

| File | Change | Responsibility |
|---|---|---|
| `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs` | Modify | Keep public activity-creation surface (`Publish`/`Consume`/`Send`/`SetError`/`TryGetExistingContext`/`ActivitySourceName`). Keep `StartActivityWithParent` orchestration core, `Truncate` utility, `IsXxxTelemetryEnabled` capability checks, `InvokeTryEnrichForTest` test seams (now forwarders). Remove the W3C and TryEnrich bodies. |
| `src/ServiceConnect.Telemetry/TraceContextPropagation.cs` | Create | `internal static class TraceContextPropagation` — owns W3C header parsing/injection + AsyncLocal inbound-fallback. |
| `src/ServiceConnect.Telemetry/TelemetryEnrichment.cs` | Create | `internal static class TelemetryEnrichment` — owns the two `TryEnrich` overloads. |

No public API change. No new collaborators visible outside the assembly. The existing `ServiceConnectActivitySourceTests.cs`, `TraceContextPropagationTests.cs` (if exists — check), `TelemetryProcessingMiddlewareTests.cs`, etc. are the regression net.

---

## Task 1: Validate the scope

**No code changes — investigation only.**

- [ ] **Step 1: File size and member list unchanged**

```bash
cd /home/tim/source/ServiceConnect-CSharp
wc -l src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs
grep -c "^[[:space:]]*\(public\|internal\|private\)" src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs
```

Expected: ~797 LOC, ~25-30 declarations. If significantly different, STOP — the file has changed since the review.

- [ ] **Step 2: No sibling already exists**

```bash
ls src/ServiceConnect.Telemetry/TraceContextPropagation.cs src/ServiceConnect.Telemetry/TelemetryEnrichment.cs 2>/dev/null
```

Expected: both `No such file or directory`. If either exists, the split has already started — STOP and report.

- [ ] **Step 3: Public surface inventory**

```bash
grep -n "^[[:space:]]*public\b" src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs
```

Expected six public declarations: `public static class ServiceConnectActivitySource`, `public static readonly string ActivitySourceName`, `public static Activity? Publish`, `public static Activity? Consume`, `public static Activity? Send`, `public static void SetError`, `public static bool TryGetExistingContext`. (Plus the `public` modifier on the class declaration.) Any additional public members mean the split needs to widen its preservation scope.

- [ ] **Step 4: Test file size**

```bash
wc -l src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs
grep -c "\[Fact\]\|\[Theory\]" src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs
```

Expected: ~1399 LOC, 80+ test methods. Used as the regression-net baseline.

- [ ] **Step 5: Report**

Report Status: DONE with the actual counts. No commit.

---

## Task 2: Extract `TelemetryEnrichment`

**Files:**
- Create: `src/ServiceConnect.Telemetry/TelemetryEnrichment.cs`
- Modify: `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs` (remove the two `TryEnrich` overloads; update internal call sites + test forwarders to call into `TelemetryEnrichment`)

**Finding context:** The two `TryEnrich` overloads (one for `Message?`, one for `byte[]?`) are pure utility methods — they accept an `Activity`, optionally invoke a user-supplied enrichment callback from options, and silently catch enrichment failures. They have no dependency on `_activitySource` or any other class state.

- [ ] **Step 1: Validate**

Re-read `ServiceConnectActivitySource.cs:735-781` (the two `TryEnrich` overloads) and `:792-796` (the `InvokeTryEnrichForTest` test seams). Confirm shape unchanged.

```bash
grep -n "TryEnrich" src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs
```

Expected: 4 matches — two method declarations, two test-seam forwarders.

Also find every call site of `TryEnrich` in the file:

```bash
grep -n "TryEnrich(" src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs
```

These call sites live inside `Publish`, `Consume`, and `Send`. The expected count is ~5-8 (each method calls `TryEnrich` for both Message? and byte[] overloads on its respective branches).

- [ ] **Step 2: Create `TelemetryEnrichment.cs`**

Create the file:

```csharp
using System.Diagnostics;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Telemetry;

/// <summary>
/// Invokes user-supplied enrichment callbacks against created activities. Catches and
/// records enrichment exceptions as a tag rather than failing the dispatch.
/// </summary>
internal static class TelemetryEnrichment
{
    /// <summary>
    /// Invokes <see cref="ServiceConnectInstrumentationOptions.EnrichWithMessage"/>
    /// against the activity. Swallows non-OCE exceptions and records the type name
    /// on the activity as <c>enrichment.exception</c>. OCE is rethrown so callers can
    /// distinguish cancellation from enrichment failure.
    /// </summary>
    internal static void TryEnrich(Activity activity, Message? message, ServiceConnectInstrumentationOptions options)
    {
        if (message is null)
        {
            return;
        }

        try
        {
            options.EnrichWithMessage?.Invoke(activity, message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Tag the exception type only. Message strings can contain caller-controlled
            // payloads or PII; the type name is sufficient diagnostic.
            activity.SetTag("enrichment.exception", ex.GetType().FullName);
        }
    }

    /// <summary>
    /// Bytes overload of the enrichment helper. Same semantics as the
    /// <see cref="Message"/> overload: OCE rethrown, other exceptions tagged.
    /// </summary>
    internal static void TryEnrich(Activity activity, byte[]? message, ServiceConnectInstrumentationOptions options)
    {
        if (message is null)
        {
            return;
        }

        try
        {
            options.EnrichWithMessageBytes?.Invoke(activity, message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            activity.SetTag("enrichment.exception", ex.GetType().FullName);
        }
    }
}
```

- [ ] **Step 3: Remove the two methods from `ServiceConnectActivitySource.cs`**

Delete the two `TryEnrich` overloads at lines 735-781.

Update the test seams at lines 792-796 to forward to the new class:

```csharp
internal static void InvokeTryEnrichForTest(Activity activity, Message? message, ServiceConnectInstrumentationOptions options) =>
    TelemetryEnrichment.TryEnrich(activity, message, options);

internal static void InvokeTryEnrichForTest(Activity activity, byte[]? bytes, ServiceConnectInstrumentationOptions options) =>
    TelemetryEnrichment.TryEnrich(activity, bytes, options);
```

Find every other call site of `TryEnrich(` in the file (inside `Publish`, `Consume`, `Send`) and update each to `TelemetryEnrichment.TryEnrich(`. Use a find-and-replace; verify each replacement makes sense in context.

- [ ] **Step 4: Build + run telemetry tests**

```bash
dotnet build src/ServiceConnect.Telemetry/ServiceConnect.Telemetry.csproj -m:1
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Telemetry|FullyQualifiedName~ServiceConnectActivitySource" -m:1
```

Expected: 0 errors, 0 warnings; all telemetry tests pass. If any test fails, the most likely culprit is a missed call-site update inside `Publish`/`Consume`/`Send`.

- [ ] **Step 5: Per-change code review**

`superpowers:requesting-code-review`. Focus: did all `TryEnrich(` call sites update? Did the new file correctly preserve OCE rethrow semantics?

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.Telemetry/TelemetryEnrichment.cs \
        src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs
git commit -m "$(cat <<'EOF'
refactor(telemetry): extract TelemetryEnrichment from ServiceConnectActivitySource

Moves the two TryEnrich overloads (Message? and byte[]?) into a new
internal static class TelemetryEnrichment. The orchestration methods
(Publish / Consume / Send) and the InvokeTryEnrichForTest seams now
forward into the new class. Behaviour preserved exactly — OCE rethrow,
exception-type tagging, null-skip — guarded by the existing
ServiceConnectActivitySourceTests suite.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Extract `TraceContextPropagation`

**Files:**
- Create: `src/ServiceConnect.Telemetry/TraceContextPropagation.cs`
- Modify: `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs` (move W3C members; update call sites; keep `TryGetExistingContext` as a public forwarder)

**Finding context:** All W3C trace-propagation members form a cohesive set:
- Header parsing: `ExtractTraceIdAndState`, `HasTraceparentHeader`, `TryGetExistingContext`
- Header injection: `InjectTraceContext`, `InjectHeader`, the `_warnedAboutCarrierShape` once-flag
- Inbound-fallback (AsyncLocal): `_inboundTraceFallback`, `InboundTraceSnapshot`, `InboundTraceFallbackScope`, `SetInboundTraceFallback`, `TryResolveFallbackParentContext`
- W3C field-name constants: `TraceParentHeaderName`, `TraceStateHeaderName`
- Test seams: `InvokeInjectHeaderForTest`, `ResetCarrierWarnedFlagForTest`

Move them all to a new `TraceContextPropagation` internal static class. The public `TryGetExistingContext` on `ServiceConnectActivitySource` stays as a public method (no API change) but becomes a one-line forwarder to `TraceContextPropagation.TryGetExistingContext`.

`StartActivityWithParent` calls `TryResolveFallbackParentContext()` — update that call site to `TraceContextPropagation.TryResolveFallbackParentContext()`. `Publish`/`Consume`/`Send` call `InjectTraceContext` — update to `TraceContextPropagation.InjectTraceContext`. `Consume` calls `ExtractTraceIdAndState` and `HasTraceparentHeader` — same forwarders.

`SetInboundTraceFallback` is `internal` and called from `TelemetryProcessingMiddleware`. After the move, the call becomes `TraceContextPropagation.SetInboundTraceFallback(...)` — update that consumer too.

- [ ] **Step 1: Validate**

Re-read the W3C-propagation methods in `ServiceConnectActivitySource.cs:416-562` (TryGetExistingContext through TryResolveFallbackParentContext) and `:564-583` (InjectHeader + _warnedAboutCarrierShape) and `:610-614` (test seams).

Find every external call site of `SetInboundTraceFallback`:

```bash
grep -rn "SetInboundTraceFallback\|ServiceConnectActivitySource\." src/ServiceConnect.Telemetry --include='*.cs'
```

Note the call site in `TelemetryProcessingMiddleware.cs` — that's the one external consumer of `SetInboundTraceFallback`. The Task 3 commit must update it.

- [ ] **Step 2: Create `TraceContextPropagation.cs`**

Create the file. Members are direct moves from `ServiceConnectActivitySource.cs` with the following visibility changes:

- `TryGetExistingContext` — was `public` on the old class; on the new class it is `internal` (the public surface stays on `ServiceConnectActivitySource` as a forwarder).
- `HasTraceparentHeader`, `ExtractTraceIdAndState`, `InjectTraceContext`, `TryResolveFallbackParentContext`, `InjectHeader` — were `private`; become `internal` on the new class.
- `SetInboundTraceFallback` — already `internal`; stays `internal`.
- `InboundTraceSnapshot` — already `internal readonly record struct`; moves verbatim.
- `InboundTraceFallbackScope` — already `private sealed class`; becomes `private sealed` on the new class.
- `_inboundTraceFallback`, `_warnedAboutCarrierShape` — were `private static`; stay `private static` on the new class.
- `TraceParentHeaderName`, `TraceStateHeaderName` — were `private const`; stay `private const` on the new class.
- `InvokeInjectHeaderForTest`, `ResetCarrierWarnedFlagForTest` — already `internal`; stay `internal` on the new class.

Header skeleton:

```csharp
using System.Diagnostics;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Telemetry;

/// <summary>
/// W3C trace-context propagation: extracts traceparent/tracestate from inbound headers,
/// injects them into outbound headers, and provides an AsyncLocal-backed inbound-fallback
/// so a configuration with publish telemetry enabled but consume telemetry disabled can
/// still continue the trace across the broker.
/// </summary>
internal static class TraceContextPropagation
{
    // W3C field names. DistributedContextPropagator uses these for its W3C propagator,
    // which is the default and effectively standard. Hard-coding here keeps the fallback
    // path independent of the propagator instance — if a user installs a non-W3C
    // propagator, the fallback still emits W3C, which is the dominant on-wire format.
    private const string TraceParentHeaderName = "traceparent";
    private const string TraceStateHeaderName = "tracestate";

    private static readonly AsyncLocal<InboundTraceSnapshot?> _inboundTraceFallback = new();
    private static int _warnedAboutCarrierShape;

    internal readonly record struct InboundTraceSnapshot(string TraceParent, string? TraceState);

    private sealed class InboundTraceFallbackScope(InboundTraceSnapshot? prior) : IDisposable
    {
        private InboundTraceSnapshot? _prior = prior;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            _inboundTraceFallback.Value = _prior;
            _prior = default;
        }
    }

    // ... (all the methods moved from ServiceConnectActivitySource — copy their bodies verbatim,
    //      adjusting only the visibility modifier on those previously `private`)
}
```

Copy each method body verbatim from `ServiceConnectActivitySource.cs`:
- `TryGetExistingContext` (was line 416)
- `HasTraceparentHeader` (was line 437)
- `ExtractTraceIdAndState` (was line 446)
- `InjectTraceContext` (was line 491)
- `SetInboundTraceFallback` (was line 528)
- `TryResolveFallbackParentContext` (was line 551)
- `InjectHeader` (was line 566)
- `InvokeInjectHeaderForTest` (was line 611)
- `ResetCarrierWarnedFlagForTest` (was line 613)

The bodies reference each other internally — those calls now resolve to same-class members on `TraceContextPropagation`, so no rewrite needed.

- [ ] **Step 3: Remove the moved members from `ServiceConnectActivitySource.cs`**

Delete the moved members from `ServiceConnectActivitySource.cs`. After deletion, the file is roughly:
- `ActivitySourceName` (public field) — STAYS
- `Version` (internal) — STAYS
- `_activitySource` (private) — STAYS
- `Shutdown` (internal) — STAYS
- `Publish` (public method) — STAYS
- `Consume` (public method) — STAYS
- `Send` (public method) — STAYS
- `SetError` (public method) — STAYS
- `TryGetExistingContext` (public method) — keep as forwarder (see Step 4)
- `StartActivityWithParent` (private) — STAYS (uses TryResolveFallbackParentContext)
- `Truncate` (internal) — STAYS
- `IsXxxTelemetryEnabled` (internal) — STAYS
- `InvokeTryEnrichForTest` (internal test seams) — STAYS

Lines removed: 416-475 (TryGetExistingContext through ExtractTraceIdAndState), 477-510 (InjectTraceContext), 516-549 (constants, AsyncLocal, snapshot, scope, SetInboundTraceFallback), 551-583 (TryResolveFallbackParentContext, _warnedAboutCarrierShape, InjectHeader), 610-614 (test seams).

- [ ] **Step 4: Re-add `TryGetExistingContext` as a public forwarder**

Where `TryGetExistingContext` lived (around the previous line 416), insert a public forwarder so the public API is preserved:

```csharp
/// <summary>
/// Extracts a W3C trace context from the headers dictionary. Returns true if a valid
/// traceparent header was present and parsed successfully.
/// </summary>
public static bool TryGetExistingContext(IDictionary<string, string> headers, out ActivityContext context)
    => TraceContextPropagation.TryGetExistingContext(headers, out context);
```

- [ ] **Step 5: Update internal call sites in `ServiceConnectActivitySource.cs`**

Find every remaining reference to the now-moved methods/fields inside `ServiceConnectActivitySource.cs`. Each becomes a `TraceContextPropagation.X` reference. Likely sites:

- `Publish` (~line 36): `InjectTraceContext(...)` → `TraceContextPropagation.InjectTraceContext(...)`
- `Consume` (~line 124): `ExtractTraceIdAndState`, `HasTraceparentHeader`, possibly `InjectTraceContext`.
- `Send` (~line 266): `InjectTraceContext(...)` → `TraceContextPropagation.InjectTraceContext(...)`
- `StartActivityWithParent` (~line 616, now shifted): `TryResolveFallbackParentContext()` → `TraceContextPropagation.TryResolveFallbackParentContext()`

Use grep to find all such references:

```bash
grep -n "InjectTraceContext\|ExtractTraceIdAndState\|HasTraceparentHeader\|TryResolveFallbackParentContext\|_inboundTraceFallback\|SetInboundTraceFallback\|InjectHeader" src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs
```

Any match that's NOT the public-forwarder line at the new TryGetExistingContext stub must be rewritten as `TraceContextPropagation.X`.

- [ ] **Step 6: Update the external consumer `TelemetryProcessingMiddleware.cs`**

```bash
grep -n "SetInboundTraceFallback" src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs
```

Rewrite each call from `ServiceConnectActivitySource.SetInboundTraceFallback(...)` to `TraceContextPropagation.SetInboundTraceFallback(...)`.

- [ ] **Step 7: Build + run telemetry tests**

```bash
dotnet build src/ServiceConnect.Telemetry/ServiceConnect.Telemetry.csproj -m:1
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Telemetry|FullyQualifiedName~ServiceConnectActivitySource" -m:1
```

Expected: 0 errors, 0 warnings; all telemetry tests pass.

Common failure modes:
- Missed a call site inside one of Publish/Consume/Send.
- `InvokeInjectHeaderForTest` / `ResetCarrierWarnedFlagForTest` are called from test code as `ServiceConnectActivitySource.InvokeInjectHeaderForTest`. The tests must update to `TraceContextPropagation.InvokeInjectHeaderForTest` — or keep a forwarder on `ServiceConnectActivitySource`. Decide based on test count: if tests only reference a couple of these, update the tests; if many, keep forwarders.

If the test count is high (5+ call sites), keep internal forwarders on `ServiceConnectActivitySource` instead:

```csharp
// On ServiceConnectActivitySource (test-seam forwarders so existing tests don't churn):
internal static void InvokeInjectHeaderForTest(object? carrier, string fieldName, string fieldValue) =>
    TraceContextPropagation.InvokeInjectHeaderForTest(carrier, fieldName, fieldValue);
internal static void ResetCarrierWarnedFlagForTest() =>
    TraceContextPropagation.ResetCarrierWarnedFlagForTest();
```

(Same pattern Task 2 used for InvokeTryEnrichForTest.)

- [ ] **Step 8: Per-change code review**

`superpowers:requesting-code-review`. Focus: did all call sites migrate? Did the public `TryGetExistingContext` API stay intact? Is `TelemetryProcessingMiddleware`'s call site updated?

- [ ] **Step 9: Commit**

```bash
git add src/ServiceConnect.Telemetry/TraceContextPropagation.cs \
        src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs \
        src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs
git commit -m "$(cat <<'EOF'
refactor(telemetry): extract TraceContextPropagation from ServiceConnectActivitySource

Moves W3C trace-context propagation (ExtractTraceIdAndState, InjectTraceContext,
HasTraceparentHeader, InjectHeader, _warnedAboutCarrierShape), the AsyncLocal
inbound-fallback machinery (InboundTraceSnapshot record, InboundTraceFallbackScope,
SetInboundTraceFallback, TryResolveFallbackParentContext, _inboundTraceFallback),
the W3C field-name constants, and the InvokeInjectHeaderForTest / Reset test
seams into a new internal static class TraceContextPropagation. The public
TryGetExistingContext stays on ServiceConnectActivitySource as a one-line
forwarder so callers see no API change. ServiceConnectActivitySource's
Publish/Consume/Send/StartActivityWithParent call sites and
TelemetryProcessingMiddleware's SetInboundTraceFallback call site update
accordingly. Behaviour preserved exactly — guarded by the existing
ServiceConnectActivitySourceTests suite.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: End-of-phase verification gate

Same shape as Phase 1, 2, 3c end-of-phase gates. Each step is a delegated subagent.

- [ ] **Step 1: Final branch-wide code review** on `<base>..HEAD` where base = HEAD immediately before Task 1 (Phase 3c's final commit, if 3a runs after 3c — otherwise Phase 2's `87bf13e0`).
- [ ] **Step 2: Full unit-test suite** — `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -m:1`. Phase 2 baseline 1644 — Phase 3a adds zero new tests.
- [ ] **Step 3: Full end-to-end suite** — `dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj -m:1`. Expected 136/136.
- [ ] **Step 4: Serialization-compat tests** — Expected 48/48.
- [ ] **Step 5: All example apps build** — Expected 53/53 clean.
- [ ] **Step 6: Documentation site + README** — Phase 3a is pure internal refactor; expected NO UPDATE NEEDED. The `Telemetry` example app does not import `TraceContextPropagation` or `TelemetryEnrichment` (both `internal`), so user-facing examples are unaffected.
- [ ] **Step 7: File-size sanity**:

```bash
wc -l src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs \
       src/ServiceConnect.Telemetry/TraceContextPropagation.cs \
       src/ServiceConnect.Telemetry/TelemetryEnrichment.cs
```

Expected:
- `ServiceConnectActivitySource.cs`: ~350-400 LOC (was 797)
- `TraceContextPropagation.cs`: ~250 LOC
- `TelemetryEnrichment.cs`: ~80 LOC
- Combined total: ~700-750 (slightly less than the original 797 because moved methods don't repeat their xmldoc — xmldoc moves with them, but inter-file references shorten some bodies).

- [ ] **Step 8: Close Phase 3a** — mark complete in TodoWrite.

---

## Self-Review Checklist

1. **Spec coverage:**
   - Review's split (1) ServiceConnectActivitySource (activity creation + tag-setting): ✔ kept.
   - Review's split (2) TraceContextPropagation (extraction/injection + W3C + fallback AsyncLocal): ✔ Task 3.
   - Review's split (3) TelemetryEnrichment (TryEnrich overloads + message callbacks): ✔ Task 2.

2. **No public API change:**
   - `ActivitySourceName`, `Publish`, `Consume`, `Send`, `SetError`, `TryGetExistingContext` all stay on `ServiceConnectActivitySource` with unchanged signatures.
   - `TryGetExistingContext` becomes a one-line forwarder to preserve the public method.
   - `TraceContextPropagation` and `TelemetryEnrichment` are `internal static` — no user-facing addition.

3. **No placeholders:** Every step has real code or a real command.

4. **Test patterns:** No new tests. Regression net is the existing `ServiceConnectActivitySourceTests.cs` (1399 lines, 80+ facts) plus `TelemetryProcessingMiddlewareTests.cs` and `TelemetrySendMiddlewareTests.cs`.

5. **Dotnet delegation:** Every `dotnet` step runs in the implementer subagent.

6. **Commit hygiene:** Each task = one commit. Two commits total for Phase 3a (Task 2 + Task 3); Tasks 1 and 4 are investigative / gate-only and produce no commits.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-05-18-phase-3a-activitysource.md`. Subagent-driven execution recommended (same workflow as Phase 1, 2, 3c).
