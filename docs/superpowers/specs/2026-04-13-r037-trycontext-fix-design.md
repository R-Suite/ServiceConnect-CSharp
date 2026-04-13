# R-037 Fix: `TryGetExistingContext` Carrier-Type Bug — Design

**Status:** Approved 2026-04-13
**Scope:** Fix R-037 (the only outstanding item in [docs/remaining-issues.md](../../remaining-issues.md)). Flip the existing bug-characterization unit test to assert correct behavior. Mark R-037 Done.

## Root Cause

[ServiceConnectActivitySource.cs:184](../../../src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L184) declares:

```csharp
public static bool TryGetExistingContext(Dictionary<string, string> headers, out ActivityContext context)
```

It hands `headers` to `DistributedContextPropagator.Current.ExtractTraceIdAndState(headers, ExtractTraceIdAndState, ...)`. The callback:

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

Generic `Dictionary<K,V>` is invariant, so `Dictionary<string, string>` is never `Dictionary<string, object>`. The pattern match always fails, `traceParent` is `null`, and `ActivityContext.TryParse(null, null, ...)` returns `false`. The method silently returns `false` for every valid traceparent — it cannot succeed for any input.

The other caller, `Consume`, passes `Dictionary<string, object>` (RabbitMQ AMQP headers are byte-array values under string keys) and works correctly.

## Fix

Teach the callback to handle both carrier types:

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

Notes:
- `HeaderDecoder.Decode` is only needed for the `object` branch (RabbitMQ delivers header values as `byte[]`). The `string` branch has already-decoded values.
- No public API change. No new dependencies. No behavioural change for the `Consume` path.

## Tests

Flip the existing placeholder in [ServiceConnectActivitySourceTests.cs:201-219](../../../src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs#L201-L219).

| Before | After |
|---|---|
| `TryGetExistingContext_WithTraceparent_ReturnsFalse_DueToCarrierTypeMismatch` asserts `ok == false` and documents the bug | `TryGetExistingContext_WithTraceparent_ReturnsTrue_AndParsesContext` asserts `ok == true`, `ctx.TraceId.ToString() == traceId`, `ctx.SpanId.ToString() == spanId` |

The new test body:

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

The other two `TryGetExistingContext` tests (null headers, missing trace headers) stay as-is — they don't depend on carrier type.

The existing `Consume_ExtractsParentContext_FromTraceparentHeader` test (which exercises the `Dictionary<string, object>` branch) serves as a regression guard for the unchanged path.

## Out of Scope

- No signature change to `TryGetExistingContext`.
- No rewrite of `Consume`.
- No new public API.
- No speculative handling of other carrier types (e.g., `IDictionary<string, object>`, `ConcurrentDictionary`). The two known call sites are the only ones; broaden if a real need appears.

## Docs

[docs/remaining-issues.md](../../remaining-issues.md):
- Update header paragraph: add "R-037 fixed on 2026-04-13."
- Move R-037 row to a **Done** state (kept in the "Discovered During This Series" section for traceability) with a note: "Fixed — `ExtractTraceIdAndState` now handles `Dictionary<string, string>` carriers."

## Commit Strategy

Two commits:
1. `fix: handle Dictionary<string,string> carrier in ExtractTraceIdAndState (R-037)` — prod change (`ServiceConnectActivitySource.cs`) + test flip (`ServiceConnectActivitySourceTests.cs`).
2. `docs: mark R-037 done` — `docs/remaining-issues.md`.

Each commit leaves the solution green (`dotnet build` + `dotnet test src/ServiceConnect.UnitTests`).

## Success Criteria

1. `dotnet test src/ServiceConnect.UnitTests` reports 329/329 passing (same count — one test is renamed and its assertions flipped, not added).
2. E2E suite stays green (73/73) — prod change is contained to a helper method with no in-repo callers.
3. Zero build warnings.
4. R-037 marked Done in [docs/remaining-issues.md](../../remaining-issues.md).

## Risk

Low. The added branch is reached only from `TryGetExistingContext`, which currently returns `false` unconditionally for every input — no consumer can be relying on broken behaviour. The `Consume` path is untouched.
