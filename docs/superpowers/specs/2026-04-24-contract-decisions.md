# Phase 0 — Contract decisions

**Date:** 2026-04-24
**Strategy spec:** [`2026-04-24-consolidated-issues-remediation-strategy.md`](./2026-04-24-consolidated-issues-remediation-strategy.md)
**Source list:** [`consolodated-issues/2026-04-24-consolidated-issues.md`](../../../consolodated-issues/2026-04-24-consolidated-issues.md)
**Branch:** `v7-clean-architecture`

## Summary

Phase 0 of the consolidated-issues remediation locks five contract-level decisions before any interface-touching code is written. Subsequent phases (notably 5, 6b, and parts of 7b) consume these decisions when their plans are written.

The five items addressed here all embed a design choice rather than a single mechanical fix; agreeing the surface up-front prevents churn in the affected interfaces and their downstream call sites.

| Decision | Source items | Consuming phases |
|---|---|---|
| 1 — `IFilter` semantics | H-22 | Phase 5 |
| 2 — `ITimeoutStore` contract | C-08, H-20 (+ L-73 side-effect) | Phase 4 |
| 3 — `CancellationToken` on handler / write-stream interfaces | M-28, M-29 | Phase 6b |
| 4 — Options-type mutability | M-30, M-31 (+ L-58 side-effect) | Phase 6b |
| 5 — `HeaderDecoder` nested-table fallback | M-32 | Phase 6b |

## Decision 1 — `IFilter` / `IFilterPipeline` return semantics (H-22)

### Decision

Replace the inverted boolean returns on both interfaces with an enum:

```csharp
public enum FilterAction
{
    Continue,
    Stop,
}

public interface IFilter
{
    Task<FilterAction> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default);
}

public interface IFilterPipeline
{
    Task<FilterAction> ExecuteOutgoingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default);
    Task<FilterAction> ExecuteBeforeConsumingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default);
    Task<FilterAction> ExecuteAfterConsumingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default);
}
```

`FilterAction` lives next to the interfaces in `ServiceConnect.Interfaces/Pipelines/`.

### Rationale

`IFilter.ProcessAsync` returned `true = continue`; `IFilterPipeline.Execute*` returned `true = blocked`. The XML docs were accurate but the inversion still caused real confusion — the test name `BlockedAsync_returns_true_when_filter_returns_false` is direct evidence. An enum makes the inversion mistake unrepresentable; v7 is a breaking-change rewrite, so the migration cost on existing filter implementations is paid once and is essentially zero (no shipped consumers).

### Consuming items / phases

H-22 — Phase 5.

## Decision 2 — `ITimeoutStore` id-only Remove/Release contract (C-08, H-20)

### Decision

Collapse `ITimeoutStore` and `ILeaseAwareTimeoutStore` into a single interface with an optional `Guid? lockOwner` parameter:

```csharp
public interface ITimeoutStore
{
    Task InsertTimeoutAsync(TimeoutData timeoutData, CancellationToken cancellationToken = default);
    Task<TimeoutsBatch> GetTimeoutsBatchAsync(CancellationToken cancellationToken = default);

    Task RemoveDispatchedTimeoutAsync(
        Guid id,
        Guid? lockOwner = null,
        CancellationToken cancellationToken = default);

    Task ReleaseDispatchedTimeoutAsync(
        Guid id,
        Guid? lockOwner = null,
        CancellationToken cancellationToken = default);
}
```

Semantics:
- `lockOwner == null` — unconditional remove/release (the legacy id-only behaviour, now uniform across stores).
- `lockOwner == Guid` — lease-checked; throws `ConcurrencyException` when the row's current lock owner does not match.

Both `InMemoryTimeoutStore` and `MongoDbTimeoutStore` implement this single interface. `ILeaseAwareTimeoutStore` is removed.

### Rationale

Pre-decision, the two stores diverged on the id-only path: InMemory mutated unconditionally (could stomp an active peer's lease); MongoDb silently no-oped on leased rows. The lease-aware path was already aligned (both throw `ConcurrencyException`). Treating "lease check" as a runtime parameter rather than as a separate interface eliminates the divergent surface and removes the inverted-relationship issue between the two interfaces (`ILeaseAwareTimeoutStore` did not extend `ITimeoutStore` — L-73). The dispatcher in [`ProcessManagerTimeoutService.cs:120-134`](../../../src/ServiceConnect/Services/ProcessManagerTimeoutService.cs#L120-L134) collapses to a single call passing `timeout.LockedBy != Guid.Empty ? timeout.LockedBy : (Guid?)null`.

### Consuming items / phases

- C-08, H-20 — Phase 4 (paired remediation).
- L-73 (interface relationship) — resolved by this decision; remove from Phase 7b's slate.

## Decision 3 — `CancellationToken` on handler and write-stream interfaces (M-28, M-29)

### Decision

Add `CancellationToken cancellationToken = default` to four methods on three interfaces:

```csharp
public interface IMessageHandler<in TMessage> where TMessage : Message
{
    IConsumeContext Context { get; set; }
    Task HandleAsync(TMessage message, CancellationToken cancellationToken = default);
}

public interface IProcessHandler<TData, TMessage>
    where TData : class, IProcessManagerData, new()
    where TMessage : Message
{
    IConsumeContext Context { get; set; }
    Task HandleAsync(TMessage message, TData data, CancellationToken cancellationToken = default);
    void ConfigureMapper(IProcessManagerPropertyMapper mapper) { /* default impl unchanged */ }
}

public interface IMessageBusWriteStream : IAsyncDisposable
{
    Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default);
    Task CloseAsync(CancellationToken cancellationToken = default);
}
```

The dispatch pipeline passes `Context.CancellationToken` as the argument to handler `HandleAsync` calls. `Context` stays for what it is actually for: bus access, correlation id, reply helper.

### Rationale

The Context-based cancellation channel covers handlers but not `IMessageBusWriteStream` (no `IConsumeContext` is in scope at write-stream sites), and the source list specifically called out that "external handler implementations" find the missing parameter awkward. v7 is breaking; the migration cost on existing handlers in `examples/`, tests, and `website/` docs is paid once. The resulting surface is idiomatic .NET (CA2016 / FxCop guideline that every async method takes a `CancellationToken`).

### Naming note

The strategy spec referenced `CompleteAsync`; the actual interface uses `CloseAsync`. The decision applies to `CloseAsync` as it stands — no renaming bundled here.

### Consuming items / phases

M-28, M-29 — Phase 6b.

## Decision 4 — Options-type mutability (M-30, M-31)

### Decision

Convert `RequestOptions` to a `readonly record struct` matching the existing `PublishOptions` / `SendOptions` pattern, and change `SendOptions.EndPoints` from `IList<string>?` to `IReadOnlyList<string>?`:

```csharp
public readonly record struct RequestOptions
{
    public const int DefaultTimeoutMs = 10_000;
    public static RequestOptions Default => new() { Timeout = DefaultTimeoutMs };

    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public string? EndPoint { get; init; }
    public IReadOnlyList<string>? EndPoints { get; init; }
    public int Timeout { get; init; } = DefaultTimeoutMs;
    public int? ExpectedReplyCount { get; init; }
}

public readonly record struct SendOptions
{
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public string? EndPoint { get; init; }
    public IReadOnlyList<string>? EndPoints { get; init; }   // was IList<string>?
}
```

`PublishOptions` is unchanged.

### Rationale

The `PublishOptions` XML doc already states the rationale that motivated the struct + init-only + read-only-collection pattern: *"the mutable sealed-class form allowed concurrent `PublishAsync` callers sharing one instance to clobber each other between construction and the async pipeline's header read."* The same hazard applies to `RequestOptions` for `SendRequestMultiAsync`. Converting `RequestOptions` brings all three options types onto one consistent surface; `SendOptions.EndPoints`'s `IList` was a stale residual that the pattern had already moved past for `Headers`. The struct conversion also makes `RequestOptions.Default` a cheap stack allocation rather than the heap allocation flagged by L-58.

### Consuming items / phases

- M-30, M-31 — Phase 6b.
- L-58 (Default-on-every-access alloc) — resolved by the struct conversion; remove from Phase 7b's slate.

## Decision 5 — `HeaderDecoder.Decode` nested-table fallback (M-32)

### Decision

Recursively decode `IDictionary` and non-string `IEnumerable` values to a JSON-shaped string using `System.Text.Json`. Wrap the recursive call in a defensive `try/catch` whose `catch` block returns the type name, preserving the existing "must never throw" invariant. Update [`HeaderDecoderTests.cs:50-58`](../../../src/ServiceConnect.UnitTests/HeaderDecoderTests.cs#L50-L58) to assert the new structured output.

Sketch (final form lives in Phase 6b's plan):

```csharp
public static string? Decode(object? value)
{
    if (value is null) return null;
    if (value is byte[] bytes) return Encoding.UTF8.GetString(bytes);
    if (value is string s) return s;

    try
    {
        if (value is IDictionary dict)            return RenderDictionary(dict);
        if (value is IEnumerable list)            return RenderList(list);
    }
    catch
    {
        return value.GetType().FullName;
    }

    return value.ToString();
}
```

`RenderDictionary` and `RenderList` recursively call `Decode` on each element and emit JSON-shaped output. Flat-value call sites (`sender`, `MessageType`, etc.) are unaffected.

### Rationale

The pre-decision fallback returned the runtime type name for any nested AMQP table or array (e.g. `"System.Collections.Generic.Dictionary`2[System.String,System.Object]"`), silently dropping all nested data — including `x-death` chains used for retry diagnostics. The current test explicitly locked in the broken behaviour with a comment ("the exact content is unspecified — the invariant is that it does not throw"); the test is part of the defect and is updated along with the implementation. The defensive `try/catch` keeps the "must never throw" invariant the XML doc guarantees, because a throw at this site would cascade to the consumer host's nack-with-requeue and produce a poison-message loop.

### Consuming items / phases

M-32 — Phase 6b.

## Side-effect resolutions

| Item | Originally planned in | Resolved by | Action |
|---|---|---|---|
| L-58 — `RequestOptions.Default` allocates on every access | Phase 7b | Decision 4 (struct conversion makes `Default` a stack alloc) | Drop from Phase 7b's slate |
| L-73 — `ILeaseAwareTimeoutStore` does not extend `ITimeoutStore` | Phase 7b | Decision 2 (interfaces collapse into one) | Drop from Phase 7b's slate |

These two items are kept on the consolidated-issues tracker but flipped to `fixed in <sha>` when the consuming phase commits, citing the consuming phase's commit as the resolution.

## Consuming-phase update

Per the strategy spec § 5.8, Phase 0 is a hard gate for Phases 5, 6b, and the interface portion of 7b. With this design doc landed and reviewed, those phases can now be planned. Each phase plan cites the relevant decision section above and applies it to its slate of source-list items.
