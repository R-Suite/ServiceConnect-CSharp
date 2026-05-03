# Phase 12 — Interface contracts + health checks + remaining polish (design)

**Author:** brainstorming session 2026-05-03
**Branch:** `v7-clean-architecture`
**Source backlog:** [`consolidated-issues/phases/phase-12-interfaces-healthchecks-polish.md`](../../../consolidated-issues/phases/phase-12-interfaces-healthchecks-polish.md)

---

## 1. Goal

Final cleanup pass before v8 ship. Closes the long tail of interface-contract issues, health-check correctness items, and small interface-shape polish. Three architectural threads: (A) public-API breaking changes — handler signature, DTO contract tightenings, init-only message ID; (B) internal correctness fixes — JSON escape completion, cancellation-token plumbing, lazy-connect health-check semantics; (C) additive API — multi-bus health-check overloads.

This is the final phase of the bug-fix sweep. After Phase 12 ships, v8 is feature-complete.

## 2. Verified scope

The 2026-04-28 audit listed 3 Mediums + 3 Lows + ~12 smaller items for Phase 12. The 2026-05-03 verification audit reclassified each against the current source (Phases 1-11 already shipped). Final scope:

### 2.1 Dropped — already fixed or reclassified

- **L22** — null-`Registration` fallback now explicit (`Registration?.FailureStatus ?? Unhealthy`). Closed by recent health-check commits.
- **M28** — `RequestOptions.Default` static is the documented API; `default(RequestOptions)` is off-spec misuse, not a real defect. The original concern is closed by the existence of `Default`.
- **`OutgoingEventArgs._headers readonly` reassigned by `init`** — valid C# 10+ pattern (init accessors may assign readonly fields). Not a bug.

### 2.2 In scope — 13 items

**Mediums:**

| ID | Subject | File:line (current) |
| --- | --- | --- |
| M29 | `HeaderDecoder.Render` partial JSON escaping (`"` done; `\`, `\n`, `\r`, `\t`, control chars still raw) | `HeaderDecoder.cs:60-101` |
| M30 | `Aggregator<T>.Timeout()` default sentinel is `TimeSpan.Zero`, not unambiguously "disabled" | `Aggregator.cs:15-18` |

**Lows:**

| ID | Subject | File:line (current) |
| --- | --- | --- |
| L21 | Health checks ignore `cancellationToken` | `BusConsumingHealthCheck.cs:24-35` and siblings |
| L23 | `ProducerConnectionHealthCheck` reports Unhealthy for lazy-not-yet-tried state | `ProducerConnectionHealthCheck.cs:33-39` |

**Smaller — Interfaces:**

- `IBus.RequestTimeoutAsync` DIM throws eagerly (should defer via `Task.FromException`) — `IBus.cs:93`.
- `RequestTimeoutException` interpolation uses default culture — `RequestTimeoutException.cs:9`.
- `Message.CorrelationId { private set; }` should be `init` — `Message.cs:16`.
- `IMessageHandler` / `IProcessHandler` / `IStreamHandler.Context { get; set; }` race risk for singleton handlers.
- `TimeoutData.Headers` mutable `IDictionary` — `TimeoutData.cs:31`.
- `ConsumeEventArgs.Headers` mutable dict — `ConsumeEventArgs.cs:21`.
- `HeaderDecoder.Render` recurses without depth limit — `HeaderDecoder.cs:55-119`.

**Smaller — HealthChecks:**

- `ActivatorUtilities.CreateInstance` allocates per probe — `HealthChecksBuilderExtensions.cs:29, 49, 72`.
- No API to register a check for a named/keyed bus — `HealthChecksBuilderExtensions.cs`.

### 2.3 Out of scope (not part of this phase)

- `TimeoutsBatch.DueTimeouts` mutable `IList` — internal-ish DTO produced by `ITimeoutStore` and consumed once; mid-flight mutation isn't a real risk.
- `SendContext.MessageBytes` mutable `byte[]` — intentionally mutable for the filter pipeline (compression, signing, encryption filters rewrite bytes).

## 3. Design decisions

Six decisions taken during brainstorming.

### 3.1 Q1 — M30 `Aggregator<T>.Timeout()` sentinel → **A: `Timeout.InfiniteTimeSpan`**

Default body returns `Timeout.InfiniteTimeSpan` (`-1ms`). Framework dispatcher (in `AggregatorProcessor`) gates scheduling on `> TimeSpan.Zero`. Source-compatible with all existing overrides that return positive durations.

Alternatives rejected:
- **B (`TimeSpan?` return type):** source-breaking for any subclass that overrode `Timeout()`. Cleaner contract but more invasive.
- **C (treat `TimeSpan.Zero` as disabled):** source-compatible but ambiguous to readers (`Zero` is also a legal "fire immediately" Timer call).

### 3.2 Q2 — L23 `ProducerConnectionHealthCheck` lazy-connect → **D: NotYetConnected as Healthy**

`IProducer` exposes a "haven't tried yet" predicate (existing or added in this phase). Check returns `Healthy` for that state. Once the producer attempts a publish and fails, transitions to `Unhealthy`. The diagnostic value of "broker is down" is already covered by `ConsumerConnectionHealthCheck` (which connects eagerly).

Alternatives rejected:
- **A (tags-based liveness/readiness separation):** more conservative but introduces a knob users don't actually need; the lazy-connect IS the design choice.
- **B (tri-state IProducer health enum):** adds API surface for a transient state.
- **C (config switch on registration):** another knob.

### 3.3 Q3 — `IMessageHandler.Context { get; set; }` race → **A: parameter on `HandleAsync`**

`HandleAsync(T message, IConsumeContext context, CancellationToken cancellationToken = default)`. Remove the `Context` property from all three handler interfaces. v8 breaking change; migration is mechanical (append parameter, replace `this.Context` reads with `context`).

Alternatives rejected:
- **B (require transient/scoped lifetime via runtime check):** still source-breaking for singleton-registered handlers; pushes the wrong-singleton problem to startup error rather than fixing the contract.
- **C (AsyncLocal):** preserves API shape but adds invisible state.
- **D (document hazard):** users hit the race in production.

### 3.4 Q4 — Mutability tightening → **B: headers only**

Tighten `TimeoutData.Headers` and `ConsumeEventArgs.Headers` to `IReadOnlyDictionary<string, object>`. Leave `TimeoutsBatch.DueTimeouts` and `SendContext.MessageBytes` alone (justified internal mutability).

Alternatives rejected:
- **A (tighten all six):** `SendContext.MessageBytes` is intentionally mutable for filters; tightening would force a `byte[] → ReadOnlyMemory<byte>` migration on every filter that rewrites bytes.
- **C (XML doc only):** depends on caller discipline; doesn't surface the contract through the type system.

### 3.5 Q5 — `HeaderDecoder.Render` depth limit → **A: throw on limit**

Hard depth limit of 32. On exceed: `throw new InvalidOperationException("Header value exceeds nesting depth 32.")`. Threads `int depth` parameter through `Render` / `RenderDictionary` / `RenderEnumerable` / `RenderNonGenericDictionary`.

Alternatives rejected:
- **B (truncate with placeholder):** corrupts the wire format silently.
- **C (warn + return null):** silent corruption with logging.

### 3.6 Q6/Q6a — Multi-bus health-check API → **C: keyed + factory overloads**

Multi-bus is a real use case in this codebase. Add per existing extension (`AddServiceConnectBus`, `AddServiceConnectConsumer`, `AddServiceConnectProducer`):

1. **Factory overload** — does the actual work:
   ```csharp
   AddServiceConnect{Bus|Consumer|Producer}(name, busFactory, failureStatus, tags);
   ```
2. **Keyed convenience overload** — delegates to the factory overload via `sp.GetRequiredKeyedService<IBus>(serviceKey)`:
   ```csharp
   AddServiceConnect{Bus|Consumer|Producer}(name, serviceKey, failureStatus, tags);
   ```

The existing parameterless overload becomes a wrapper that delegates to the factory overload with `sp => sp.GetRequiredService<IBus>()`.

## 4. Architecture

No cross-cutting redesign. Three threads:

**Thread A — Public-API breaking changes** (largest blast radius; ship early).
- `IMessageHandler.HandleAsync` signature — touches every user handler in the test suite (~30 files).
- DTO contract tightenings: `Message.CorrelationId` `init`, `TimeoutData.Headers` / `ConsumeEventArgs.Headers` `IReadOnlyDictionary`.
- `Aggregator<T>.Timeout()` default body changes; signature stays.

**Thread B — Internal correctness fixes** (no API surface change).
- M29 escape table extension (`\`, `\n`, `\r`, `\t`, control chars).
- L21 cancellation-token plumbing (3 health-check files).
- L23 producer lazy-connect → Healthy.
- `RequestTimeoutException` invariant-culture format.
- `IBus.RequestTimeoutAsync` DIM body: `throw` → `Task.FromException`.
- `HeaderDecoder.Render` depth-limit guard (additive).
- `ActivatorUtilities` per-probe → factory closure (Lazy wrapper).

**Thread C — Additive API** (multi-bus health checks).
- Three new factory overloads + three new keyed-convenience overloads in `HealthChecksBuilderExtensions.cs`.

**No new abstractions, no new files in production code.** All work lands in existing files. New test files per non-trivial test affinity.

## 5. Per-finding fix shapes

### 5.1 M29 — `HeaderDecoder.Render` complete JSON escaping

Add a private `EscapeJsonString(string s)` helper handling `\`, `"`, `\b`, `\f`, `\n`, `\r`, `\t`, and `\u00XX` for control chars (< 0x20). Replace the inline `s.Replace("\"", "\\\"")` calls with `EscapeJsonString(s)`. Test inputs: `\`, `\n`, `\t`, `\r`, `"`, ``. Round-trip via `JsonNode.Parse(rendered)`.

### 5.2 M30 — `Aggregator<T>.Timeout()` default

Body changes from `return default;` to `return Timeout.InfiniteTimeSpan;`. XML doc updated to name the new sentinel. Verify dispatcher's call site in `AggregatorProcessor` checks `> TimeSpan.Zero` (not `>= 0`) to exclude the new sentinel.

### 5.3 L21 — Health-check cancellation tokens

Three files: `BusConsumingHealthCheck.cs`, `ConsumerConnectionHealthCheck.cs`, `ProducerConnectionHealthCheck.cs`. Add `cancellationToken.ThrowIfCancellationRequested()` at the top of each `CheckHealthAsync`. Test: pre-cancelled token → `OperationCanceledException`.

### 5.4 L23 — Producer lazy-connect

`IProducer` needs a "haven't tried yet" predicate. Read `IProducer.cs` first to see if one exists (e.g. `IsConnecting`, `HasAttemptedConnection`, or a state enum). If none exists, add an internal `bool HasAttemptedConnection { get; }` (or equivalent) — small interface addition is in scope. The check then:

```csharp
if (!_producer.HasAttemptedConnection)
    return HealthCheckResult.Healthy("Producer has not yet attempted connection (lazy).");
if (!_producer.IsConnected)
    return new HealthCheckResult(failureStatus, "Producer is not connected.");
return HealthCheckResult.Healthy("Producer is connected.");
```

### 5.5 `IBus.RequestTimeoutAsync` DIM

Change body from `=> throw new NotSupportedException(...)` to `=> Task.FromException<TReply>(new NotSupportedException(...))`. The exception is identical; the timing changes from synchronous throw to deferred-on-await — symmetry with the success path.

### 5.6 `RequestTimeoutException` invariant culture

Replace the interpolation with `FormattableString.Invariant($"...")` or pre-format `elapsed.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)`.

### 5.7 `Message.CorrelationId` `init`

Change `{ get; private set; }` to `{ get; init; }`. Verify the Bus doesn't write `CorrelationId` post-construction (likely safe — value comes from headers at materialization time).

### 5.8 `HandleAsync(message, context, ct)` signature

The largest mechanical change in the phase.

**Interfaces (3 files):**
- `IMessageHandler.cs` — `Task HandleAsync(T message, IConsumeContext context, CancellationToken cancellationToken = default);`. Drop `Context` property.
- `IProcessHandler.cs` — same pattern (with the saga-data parameter preserved).
- `IStreamHandler.cs` — same pattern (with the stream parameter preserved).

**Descriptors (3 files):**
- `*HandlerDescriptor.cs` — drop `SetContext` shim. `InvokeHandleAsync(handler, message, context, ct)`.

**Processors (2 files):**
- `HandlerProcessor.cs` — stop calling `descriptor.SetContext(handler, context)`; pass `context` to invocation.
- `ProcessManagerProcessor.cs` — same.

**Test suite migration:**
Every handler implementation in the test suite (~30 files) gets the parameter appended. Build catches all unmigrated handlers; the migration is mechanical.

### 5.9 `TimeoutData.Headers` → `IReadOnlyDictionary`

`IReadOnlyDictionary<string, object> Headers { get; init; } = ImmutableDictionary<string, object>.Empty;`. Producers (timeout-store implementations) construct via init; consumers read only. Verify no in-tree mutation of `TimeoutData.Headers` post-construction; if any exist, migrate them to construct a new `TimeoutData` with the modified dictionary.

### 5.10 `ConsumeEventArgs.Headers` → `IReadOnlyDictionary`

Same shape: `IReadOnlyDictionary<string, object> Headers { get; init; }`.

### 5.11 `HeaderDecoder.Render` depth limit

Thread `int depth = 0` parameter through `Render` / `RenderDictionary` / `RenderEnumerable` / `RenderNonGenericDictionary`. Guard at the top of `Render`:

```csharp
private const int MaxDepth = 32;

internal static string Render(object? value, int depth = 0)
{
    if (depth >= MaxDepth)
        throw new InvalidOperationException(
            $"Header value exceeds nesting depth {MaxDepth}.");
    // ... existing body, with recursive calls passing `depth + 1`
}
```

### 5.12 `ActivatorUtilities` per-probe → factory closure

In `HealthChecksBuilderExtensions.cs`, replace the per-probe `sp => ActivatorUtilities.CreateInstance<T>(sp)` lambda with a `LazyInitializer.EnsureInitialized`-cached pattern (thread-safe single-allocation, BCL primitive):

```csharp
T? cached = null;
builder.Add(new HealthCheckRegistration(name,
    sp => LazyInitializer.EnsureInitialized(ref cached,
        () => ActivatorUtilities.CreateInstance<T>(sp)),
    failureStatus, tags));
```

`LazyInitializer.EnsureInitialized<T>(ref T, Func<T>) where T : class` atomicizes the initialization — the factory runs exactly once even under concurrent first-probe contention. The factory closes over the FIRST probe's `sp`; subsequent probes pass their own `sp` but the cached instance is reused. Safe because the root provider lifetime is consistent across probes.

Apply to all three sites (lines 29, 49, 72).

### 5.13 Multi-bus health-check API (Q6→C)

Three new factory overloads + three new keyed-convenience overloads:

```csharp
public static IHealthChecksBuilder AddServiceConnectBus(
    this IHealthChecksBuilder builder, string name,
    Func<IServiceProvider, IBus> busFactory,
    HealthStatus failureStatus = HealthStatus.Unhealthy,
    IEnumerable<string>? tags = null)
{
    Lazy<BusConsumingHealthCheck>? lazy = null;
    builder.Add(new HealthCheckRegistration(name,
        sp => (lazy ??= new(() =>
            ActivatorUtilities.CreateInstance<BusConsumingHealthCheck>(sp, busFactory(sp)))).Value,
        failureStatus, tags));
    return builder;
}

public static IHealthChecksBuilder AddServiceConnectBus(
    this IHealthChecksBuilder builder, string name,
    object serviceKey,
    HealthStatus failureStatus = HealthStatus.Unhealthy,
    IEnumerable<string>? tags = null)
    => builder.AddServiceConnectBus(name,
        sp => sp.GetRequiredKeyedService<IBus>(serviceKey),
        failureStatus, tags);
```

The existing parameterless overload delegates to the factory overload with `sp => sp.GetRequiredService<IBus>()`. Same shape for consumer + producer.

The check classes (`BusConsumingHealthCheck` etc.) need a constructor that accepts `IBus` directly (not via `IServiceProvider`) so the factory overload can pre-resolve the bus.

## 6. Testing strategy

Per-csproj unit tests against `ServiceConnect.UnitTests`. No new E2E tests required.

| Finding | Test approach |
| --- | --- |
| M29 | Inputs: `\`, `\n`, `\r`, `\t`, `"`, ``. Round-trip via `JsonNode.Parse(rendered)`. |
| M30 | Default returns `Timeout.InfiniteTimeSpan`; dispatcher does not schedule for that value. |
| L21 | Pre-cancelled token → `OperationCanceledException` (3 health-check files). |
| L23 | Mock `IProducer` not-yet-tried → `Healthy`; tried-and-failed → `Unhealthy`. |
| `IBus.RequestTimeoutAsync` DIM | Custom `IBus` not overriding the DIM; assert `await impl.RequestTimeoutAsync(...)` produces `NotSupportedException` (deferred). |
| `RequestTimeoutException` culture | `de-DE` culture; assert message contains `.` not `,`. |
| `Message.CorrelationId` `init` | Compile-time test; runtime construction works. |
| `HandleAsync` signature | New canonical handler test passes the parameter through; ~30 pre-existing handler tests get migrated. Build is the canonical "did I miss anything." |
| `TimeoutData.Headers` `IReadOnlyDictionary` | Compile-time test confirms `Headers.Add(...)` doesn't compile. Existing roundtrip tests still pass. |
| `ConsumeEventArgs.Headers` | Same. |
| `HeaderDecoder.Render` depth | 33-deep nested dict → `InvalidOperationException` with "nesting depth 32". |
| `ActivatorUtilities` per-probe | Two probes against the same registered check; underlying instance is the same reference. |
| Multi-bus (keyed) | Two `IBus` mocks with distinct keys; each check resolves the right one. |
| Multi-bus (factory) | Factory-shape registration test. |

**Build/test invocation:**
```bash
dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj -m:1
dotnet build src/ServiceConnect.HealthChecks/ServiceConnect.HealthChecks.csproj -m:1
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
  --filter "FullyQualifiedName~HealthCheck|RequestOptions|HeaderDecoder|Aggregator|RequestTimeoutException|MessageHandler|ProcessHandler|StreamHandler|TimeoutData|ConsumeEventArgs" -m:1
```

## 7. Rollout

**Single subagent-driven sweep**, 18 tasks (1 spec, 1 plan, 13 fixes, 3 docs). Per-task commits.

Task ordering by risk + cascade impact:

1. Spec commit
2. Plan commit
3. **`HandleAsync(message, context, ct)` signature** — largest cascade; ship FIRST.
4. **`Message.CorrelationId` `init`** — small breaking change; ship early in v8 release-notes anchor.
5. **`TimeoutData.Headers` + `ConsumeEventArgs.Headers` `IReadOnlyDictionary`**.
6. **M30 `Aggregator<T>.Timeout()` default**.
7. **M29 `HeaderDecoder` JSON escape completion**.
8. **`HeaderDecoder.Render` depth limit**.
9. **L21 health-check cancellation tokens**.
10. **L23 producer lazy-connect**.
11. **`IBus.RequestTimeoutAsync` DIM**.
12. **`RequestTimeoutException` invariant culture**.
13. **`ActivatorUtilities` per-probe**.
14. **Multi-bus health-check API**.
15. Phase 12 release notes (`website/src/content/docs/releases.mdx`).
16. Reference + learn doc updates.
17. Examples README updates.
18. Final verification gate.

Each task = one commit (or one fix-commit + one cleanup-commit if review surfaces issues). TDD where applicable; subagent-driven implementer + two-stage review per the established Phase 7-11 pattern.

## 8. v8 breaking changes shipped

- **`IMessageHandler/IProcessHandler/IStreamHandler.HandleAsync`** signature: adds `IConsumeContext context` parameter; drops `Context` property. Migration shim documented in release notes (mechanical: append parameter, replace `this.Context` with `context`).
- **`Message.CorrelationId`** accessor: `{ get; private set; }` → `{ get; init; }`.
- **`TimeoutData.Headers`** and **`ConsumeEventArgs.Headers`** types: `IDictionary<string, object>` → `IReadOnlyDictionary<string, object>`.

## 9. v8 contract clarifications shipped (non-breaking)

- **`Aggregator<T>.Timeout()` default sentinel** is `Timeout.InfiniteTimeSpan` (was `default(TimeSpan)` = `TimeSpan.Zero`). Dispatcher checks `> TimeSpan.Zero`.
- **`ProducerConnectionHealthCheck`** returns `Healthy` for the lazy-not-yet-tried state. Once a publish is attempted and fails, transitions to `Unhealthy`.
- **`HeaderDecoder.Render`** throws `InvalidOperationException` for header values nested deeper than 32 levels.
- **`IBus.RequestTimeoutAsync`** DIM defers the `NotSupportedException` via `Task.FromException`.

## 10. Documentation deliverables

**Website:**
- `website/src/content/docs/releases.mdx` — Phase 12 entry.
- `website/src/content/docs/reference/handlers/...` — `HandleAsync` signature change + migration sample; `IMessageHandler.Context` removal.
- `website/src/content/docs/reference/messages/...` — `Message.CorrelationId` `init`.
- `website/src/content/docs/reference/configuration/...` — `Aggregator.Timeout()` sentinel; `RequestOptions.Default` documented (existing).
- `website/src/content/docs/reference/extension-points/...` — `TimeoutData.Headers` `IReadOnlyDictionary`.
- `website/src/content/docs/reference/healthchecks/...` — multi-bus overloads; cancellation-token contract; producer lazy-connect semantics.

**READMEs:**
- `examples/RequestReply/README.md` — `RequestOptions.Default` usage (existing pattern; verify wording).
- `examples/Aggregator/README.md` — `Timeout.InfiniteTimeSpan` sentinel for "disabled".
- `examples/Filters/README.md` — handler signature change (filters that wrap handlers see the new param).
- (Producers/consumers in examples already use `IBus`/`IConsumer`/`IProducer` directly; the multi-bus overloads are additive.)

## 11. Out of scope

- `TimeoutsBatch.DueTimeouts` mutability (Q4→B excluded; internal-ish DTO, no real risk).
- `SendContext.MessageBytes` mutability (Q4→B excluded; intentional for filter pipeline).
- Pre-existing flake `ProcessAsync_ConcurrentFinalPacketDeliveries_DispatchesHandlerOnce` (`StreamProcessorTests.cs`) — flagged by Phase 11 final-verification reviewer. Fix is a test-fixture hardening (replace `TimeProvider.System` with monotonic `FakeTimeProvider`); not a Phase 12 scope item but a candidate cleanup commit if time permits.
- Phase 13 — there is no Phase 13. Phase 12 closes the bug-fix sweep.
