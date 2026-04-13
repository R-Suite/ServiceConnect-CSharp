# R-037 TryGetExistingContext Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix `ServiceConnectActivitySource.TryGetExistingContext` so it can extract trace context from `Dictionary<string, string>` carriers (currently always returns `false` due to a carrier-type mismatch in the private callback).

**Architecture:** Extend the private `ExtractTraceIdAndState` callback to pattern-match both `Dictionary<string, object>` (the `Consume` path, which carries `byte[]` RabbitMQ header values) and `Dictionary<string, string>` (the `TryGetExistingContext` path). Flip the existing bug-characterization unit test to assert correct behavior. No public API changes.

**Tech Stack:** C# / .NET 8 & .NET 10, `System.Diagnostics.ActivitySource`, `System.Diagnostics.DistributedContextPropagator`, xUnit.

**Spec:** [docs/superpowers/specs/2026-04-13-r037-trycontext-fix-design.md](../specs/2026-04-13-r037-trycontext-fix-design.md).

---

## File Structure

Two files are touched:

| File | Responsibility | Change |
|---|---|---|
| [src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs](../../../src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs) | Emits OTel activities for publish/consume/send; exposes the `TryGetExistingContext` helper. | Modify the private `ExtractTraceIdAndState` callback (lines 213-224) to handle both carrier types. |
| [src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs](../../../src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs) | Unit tests for `ServiceConnectActivitySource`. | Flip the existing bug-characterization test (lines 201-219) to assert correct behavior. Two other `TryGetExistingContext` tests and all other tests stay as-is. |

One doc file:

| File | Change |
|---|---|
| [docs/remaining-issues.md](../../remaining-issues.md) | Update header paragraph; flip R-037 row in "Discovered During This Series" table to Done. |

---

## Task 1: Fix carrier-type mismatch in ExtractTraceIdAndState

**Files:**
- Modify: `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs:213-224`
- Modify: `src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs:201-219`

This is TDD: flip the test first, watch it fail for the right reason, then apply the one-line fix.

- [ ] **Step 1: Flip the unit test to assert correct behavior**

Replace the existing test (currently documents the bug) with one asserting the method works. In `src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs`, find the block starting at line 201:

```csharp
    [Fact]
    public void TryGetExistingContext_WithTraceparent_ReturnsFalse_DueToCarrierTypeMismatch()
    {
        // TryGetExistingContext accepts Dictionary<string, string> but the internal
        // ExtractTraceIdAndState callback pattern-matches against Dictionary<string, object>.
        // Because Dictionary<string,string> is not Dictionary<string,object> (no variance),
        // the extraction always fails. This test documents that current behavior.
        var traceId = "0af7651916cd43dd8448eb211c80319c";
        var spanId = "b7ad6b7169203331";
        var headers = new Dictionary<string, string>
        {
            ["traceparent"] = $"00-{traceId}-{spanId}-01"
        };

        var ok = ServiceConnectActivitySource.TryGetExistingContext(headers, out var ctx);

        Assert.False(ok);
        Assert.Equal(default, ctx);
    }
```

Replace the entire `[Fact]` block above with:

```csharp
    [Fact]
    public void TryGetExistingContext_WithTraceparent_ReturnsTrue_AndParsesContext()
    {
        var traceId = "0af7651916cd43dd8448eb211c80319c";
        var spanId = "b7ad6b7169203331";
        var headers = new Dictionary<string, string>
        {
            ["traceparent"] = $"00-{traceId}-{spanId}-01"
        };

        var ok = ServiceConnectActivitySource.TryGetExistingContext(headers, out var ctx);

        Assert.True(ok);
        Assert.Equal(traceId, ctx.TraceId.ToString());
        Assert.Equal(spanId, ctx.SpanId.ToString());
    }
```

- [ ] **Step 2: Run the flipped test to confirm it fails for the right reason**

Run:
```bash
dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~TryGetExistingContext_WithTraceparent_ReturnsTrue_AndParsesContext"
```

Expected: **1 test fails** with `Assert.True() Failure: Expected: True, Actual: False`. This confirms the bug — the traceparent is present but the extractor can't read it.

- [ ] **Step 3: Apply the production fix**

In `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`, replace the body of `ExtractTraceIdAndState` (lines 213-224):

```csharp
    private static void ExtractTraceIdAndState(object? eventArgs, string name, out string? value, out IEnumerable<string>? values)
    {
        if (eventArgs is Dictionary<string, object> headers && headers.TryGetValue(name, out object? propsVal))
        {
            value = HeaderDecoder.Decode(propsVal);
            values = default;
            return;
        }

        value = default;
        values = default;
    }
```

…with:

```csharp
    private static void ExtractTraceIdAndState(object? eventArgs, string name, out string? value, out IEnumerable<string>? values)
    {
        values = default;
        switch (eventArgs)
        {
            case Dictionary<string, object> objHeaders when objHeaders.TryGetValue(name, out object? objVal):
                value = HeaderDecoder.Decode(objVal);
                return;
            case Dictionary<string, string> strHeaders when strHeaders.TryGetValue(name, out string? strVal):
                value = strVal;
                return;
            default:
                value = default;
                return;
        }
    }
```

Key properties of the change:
- `Dictionary<string, object>` branch unchanged — `Consume` continues to work identically.
- `Dictionary<string, string>` branch does not call `HeaderDecoder.Decode` — caller already has decoded strings; `byte[]` decoding is not applicable.
- `values` is assigned once at the top so every path leaves it set.

- [ ] **Step 4: Run the flipped test to confirm it now passes**

Run:
```bash
dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~TryGetExistingContext_WithTraceparent_ReturnsTrue_AndParsesContext"
```

Expected: **1 test passes**.

- [ ] **Step 5: Run the full ServiceConnectActivitySource test class to confirm no regression**

Run:
```bash
dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~ServiceConnectActivitySource"
```

Expected: **13 tests pass** (the 10 listener-fixture tests + 3 no-listener tests). In particular `Consume_ExtractsParentContext_FromTraceparentHeader` must still pass — it covers the unchanged `Dictionary<string, object>` branch.

- [ ] **Step 6: Run the full unit test suite to confirm zero regressions**

Run:
```bash
dotnet test src/ServiceConnect.UnitTests
```

Expected: **329/329 passing**, zero warnings.

- [ ] **Step 7: Verify impact analysis (per project CLAUDE.md)**

Before committing, run impact analysis on the modified symbols so we don't miss any callers:

```
gitnexus_impact({target: "ExtractTraceIdAndState", direction: "upstream"})
gitnexus_impact({target: "TryGetExistingContext", direction: "upstream"})
gitnexus_detect_changes({scope: "staged"})
```

Expected: `ExtractTraceIdAndState` has two in-repo callers (`Consume`, `TryGetExistingContext`) — both handled. `TryGetExistingContext` has no in-repo callers (verified during R-028). `detect_changes` shows only the two files in this task.

If any HIGH/CRITICAL risk is surfaced, STOP and surface to the controller.

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs
git commit -m "$(cat <<'EOF'
fix: handle Dictionary<string,string> carrier in ExtractTraceIdAndState (R-037)

TryGetExistingContext accepts Dictionary<string,string> but the internal
ExtractTraceIdAndState callback only pattern-matched Dictionary<string,object>.
Because generic Dictionary<K,V> is invariant, every call from
TryGetExistingContext silently returned false. Add a Dictionary<string,string>
branch to the callback; the Consume path (Dictionary<string,object> with
byte[] values) is unchanged. Flip the bug-characterization unit test to
assert correct behaviour.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Mark R-037 Done in remaining-issues.md

**Files:**
- Modify: `docs/remaining-issues.md` (header paragraph + R-037 row in "Discovered During This Series" table)

- [ ] **Step 1: Update the header paragraph**

In `docs/remaining-issues.md`, replace line 3:

```markdown
Issues verified against source code on 2026-04-12. R-016/B-01 (CancellationToken) and R-034 (race condition) completed in Group C-1 on 2026-04-13. R-017/R-018 (async filter pipeline + fail-closed dedup) and R-032 (settings DI) completed in Group C-2 on 2026-04-13. R-020/R-021 (Client + ProcessManagerProcessor SRP refactor) completed in Group C-3 on 2026-04-13. R-009 (service locator + reflection in remaining three processors) completed in Group C-4 on 2026-04-13. R-028 (unit test gap-fill) completed in Group C-5 on 2026-04-13. All verified issues now resolved. R-037 discovered during C-5 and recorded below for follow-up.
```

…with:

```markdown
Issues verified against source code on 2026-04-12. R-016/B-01 (CancellationToken) and R-034 (race condition) completed in Group C-1 on 2026-04-13. R-017/R-018 (async filter pipeline + fail-closed dedup) and R-032 (settings DI) completed in Group C-2 on 2026-04-13. R-020/R-021 (Client + ProcessManagerProcessor SRP refactor) completed in Group C-3 on 2026-04-13. R-009 (service locator + reflection in remaining three processors) completed in Group C-4 on 2026-04-13. R-028 (unit test gap-fill) completed in Group C-5 on 2026-04-13. R-037 (TryGetExistingContext carrier-type bug) fixed on 2026-04-13. All known issues now resolved.
```

- [ ] **Step 2: Update the R-037 row**

In `docs/remaining-issues.md`, in the "Discovered During This Series" table, replace the R-037 row:

```markdown
| R-037 | Bug | `ServiceConnectActivitySource.TryGetExistingContext` cannot extract trace context. The method accepts `Dictionary<string, string>` but the internal `ExtractTraceIdAndState` callback pattern-matches against `Dictionary<string, object>`. Because generic `Dictionary<K,V>` is invariant, the cast always fails and the method silently returns `false`. Discovered via unit test in Group C-5. No in-repo callers — impact limited to external instrumentation code that calls this public helper. Fix would be either (a) adding a `Dictionary<string, string>` branch to the callback, or (b) rewriting `TryGetExistingContext` to iterate headers directly. |
```

…with:

```markdown
| R-037 | Bug | **Done** (2026-04-13) — `ExtractTraceIdAndState` now pattern-matches both `Dictionary<string, object>` (Consume / AMQP header path) and `Dictionary<string, string>` (TryGetExistingContext path). Public API unchanged. Unit test flipped from bug-characterization to correctness. |
```

- [ ] **Step 3: Verify the docs render as expected**

Run:
```bash
git diff docs/remaining-issues.md
```

Expected diff: header paragraph updated; single R-037 table cell updated. Nothing else touched.

- [ ] **Step 4: Commit**

```bash
git add docs/remaining-issues.md
git commit -m "$(cat <<'EOF'
docs: mark R-037 done

Fix shipped in the preceding commit. All verified code-review items are
now resolved.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Final Verification (after both tasks)

- [ ] **Full build + test sweep**

```bash
dotnet build
dotnet test src/ServiceConnect.UnitTests
```

Expected: build succeeds with zero warnings; **329/329 unit tests pass**.

- [ ] **Git log sanity check**

```bash
git log --oneline -5
```

Expected: top two commits are the R-037 fix and the docs update, preceded by the spec-add commit (`e53e10c`).
