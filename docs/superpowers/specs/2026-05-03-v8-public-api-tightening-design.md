# v8 public-API tightening — design (Group A)

**Status:** approved, awaiting implementation plan
**Source review:** [architecture-review-deep.md](../../../architecture-review-deep.md)
**Roadmap:** [architecture-fix-plan.md](../../../architecture-fix-plan.md) — Group A
**Date:** 2026-05-03

---

## Overview

ServiceConnect-CSharp is approaching a v8 release with deliberate breaking changes already on the branch. This design covers **Group A** of the architecture-review fix plan: a coordinated cleanup of the public API surface to land in v8 GA, co-shipped with the System.Text.Json migration so the interface shape and the serializer impl change together as one coherent break.

The other six groups (metrics, security defaults, resilience features, perf reductions, smaller extensibility, docs/contracts) ship under their own brainstorm cycles and are deliberately not in scope here.

### Cross-cutting decisions

1. **Clean break.** No `[Obsolete]` shims, no transitional overloads. v7 → v8 callers update once. Justified because v8 is already the breaking-change window for unrelated work (handler signatures, header mutability, `TimeoutsBatch`).
2. **STJ replaces Newtonsoft in v8.** The interface is redesigned for STJ semantics; the impl follows in the same release. Wire compatibility is preserved via a test corpus (see Section 7).
3. **Wire-compat test corpus is non-negotiable.** Without it the v7↔v8 mixed-deployment story is unproven, which is unacceptable for a production messaging library.

---

## In scope

| # | Public-API change | Driver |
|---|---|---|
| 1 | `IMessageSerializer` reduced to 3 methods, all `byte[]` paths removed | STJ migration / zero-copy |
| 2 | `IProducer.{Publish,Send,SendBytes}Async` body → `ReadOnlyMemory<byte>`; headers → `IReadOnlyDictionary<string,string>?` | Zero-copy + immutability |
| 3 | `IBus.SendToManyAsync(...)` added | DX for fan-out |
| 4 | `SendOptions.EndPoints` removed; `RequestOptions.EndPoints` removed | DX consistency, removes weird semantics |
| 5 | `IBus.RouteAsync` destinations → `IReadOnlyList<string>` | Immutability |
| 6 | `IConsumer.StartConsumingAsync` messageTypes → `IReadOnlyList<string>` | Immutability |
| 7 | `IMessageDispatcher.DispatchAsync` headers → `IReadOnlyDictionary<string,object>` | Immutability |
| 8 | `ReplyOptions` struct introduced; `IConsumeContext.ReplyAsync` signature updated | Surface consistency |
| 9 | `TimeoutData.Destination` → `string?` | Nullability consistency |
| 10 | `IAggregatorPersistor` factory ctor convention removed; param types tightened to `IHasCorrelationId` | Smell removal + type safety |
| 11 | `IHasCorrelationId` interface introduced; reflection path in InMemory persistor deleted | Type-safe contract |

## Out of scope (deliberately)

- `IFilter`, `IMessageProcessingMiddleware`, `ISendMessageMiddleware` — internal-pipeline `IDictionary<string,object>` headers stay, mutation is intended at those seams.
- `IRequestReplyManager` — left alone.
- Per-handler retry config, idempotency story, TLS default, metrics rollout — own groups.
- `params string[]` form on `SendToManyAsync` — using `IReadOnlyList<string>` for consistency with `RouteAsync`. Decided closed; not revisited.
- All Group F perf reductions *except* the STJ migration (which is co-shipped here because the interface and impl change together).

---

## Design

### 1. `IMessageSerializer` — new shape

```csharp
namespace ServiceConnect.Interfaces;

public interface IMessageSerializer
{
    void Serialize<T>(T message, IBufferWriter<byte> output) where T : Message;
    T Deserialize<T>(ReadOnlyMemory<byte> data) where T : Message;
    object Deserialize(ReadOnlyMemory<byte> data, Type type);
}
```

- Down from 9 methods to 3.
- `byte[] Serialize<T>(T)` removed; callers needing bytes use `ArrayBufferWriter<byte>` and read `.WrittenMemory.ToArray()` themselves.
- `byte[]` and `ReadOnlySpan<byte>` deserialize overloads removed; callers pass `ReadOnlyMemory<byte>` (use `bytes.AsMemory()` for `byte[]`). `ROM<byte>` chosen over `Span<byte>` because interfaces can't carry stack-only types across awaits, and the STJ impl can convert `.Span` internally for the sync `Utf8JsonReader` path.
- `ReadOnlySequence<byte>` overload dropped (rare; can be added back if a real consumer surfaces).

### 2. STJ implementation

`SystemTextJsonMessageSerializer` replaces `NewtonsoftJsonMessageSerializer`. Configuration to maintain wire-compat with the prior Newtonsoft defaults:

```csharp
new JsonSerializerOptions
{
    MaxDepth = 32,                                              // matches HeaderDecoder cap
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,      // matches Newtonsoft literal-char default
    NumberHandling = JsonNumberHandling.AllowReadingFromString, // tolerates "3" → int (Newtonsoft-compatible)
    PropertyNameCaseInsensitive = false,                        // Newtonsoft default
    IncludeFields = false,                                      // Newtonsoft default
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,         // matches NullValueHandling.Include
    // DateTime: STJ default ISO 8601 round-trip; explicit Converters added for any
    // edge case the wire-compat corpus surfaces (DateTimeKind.Unspecified is the
    // most likely offender).
}
```

Implementation notes:
- The `Serialize<T>(T, IBufferWriter<byte>)` path uses `Utf8JsonWriter(output, options)` to write directly into the caller's writer — genuinely zero-copy on the send hot path.
- The `Deserialize<T>(ReadOnlyMemory<byte>)` path calls `JsonSerializer.Deserialize<T>(data.Span, options)` for the contiguous-buffer fast path.

### 3. Body-type cascade (internal)

```csharp
// SendContext (init-only — bytes set once at construction; middleware mutates Headers, never bytes)
public sealed class SendContext
{
    public required ReadOnlyMemory<byte> MessageBytes { get; init; }   // was: byte[]
    public required IDictionary<string, string> Headers { get; init; } // unchanged: middleware-mutable collection
    // ... other fields unchanged
}

// Envelope — already ReadOnlyMemory<byte> in the codebase; no Phase A.2 change needed.
public sealed class Envelope
{
    public ReadOnlyMemory<byte> Body { get; init; } = ReadOnlyMemory<byte>.Empty;
    public IDictionary<string, object> Headers { get; init; } = new Dictionary<string, object>(StringComparer.Ordinal);
    // ... other fields unchanged
}
```

Filter / middleware semantics:
- **Headers are mutable; bytes are immutable.** Middleware in the current pipeline mutates the `Headers` dictionary (telemetry stamps, etc.) but does not replace the body bytes. Keeping `MessageBytes` and `Body` init-only matches the de-facto contract — middleware that needs a different payload constructs a new context. YAGNI: enabling `set` for an unused capability would expand the public surface for no gain.
- The class of in-place mutation through a shared buffer (`bytes[0] = ...`) was already removed by the time this design was written: `Envelope.Body` was already `ReadOnlyMemory<byte>`, and `SendContext.MessageBytes` will follow in this phase. A shared buffer cannot be written through the local handle.

### 4. `IProducer` — final shape

```csharp
public interface IProducer : IAsyncDisposable
{
    Task PublishAsync(Type type, ReadOnlyMemory<byte> body,
        IReadOnlyDictionary<string,string>? headers = null, CancellationToken ct = default);
    Task SendAsync(Type type, ReadOnlyMemory<byte> body,
        IReadOnlyDictionary<string,string>? headers = null, CancellationToken ct = default);
    Task SendAsync(string endPoint, Type type, ReadOnlyMemory<byte> body,
        IReadOnlyDictionary<string,string>? headers = null, CancellationToken ct = default);
    Task SendBytesAsync(string endPoint, Type type, ReadOnlyMemory<byte> packet,
        IReadOnlyDictionary<string,string>? headers = null, CancellationToken ct = default);

    long MaximumMessageSize { get; }
    bool IsHealthy { get; }
    bool HasAttemptedConnection { get; }
    Task DisconnectAsync(CancellationToken ct = default);
}
```

### 5. `IBus` — endpoint shape

`SendOptions` and `RequestOptions` lose `EndPoints` (the multi-endpoint form). `EndPoint` (singular `string?`) stays as the canonical single-target field.

```csharp
public readonly record struct SendOptions
{
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public string? EndPoint { get; init; }
}

public readonly record struct RequestOptions
{
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public string? EndPoint { get; init; }
    public TimeSpan? Timeout { get; init; }
}
```

`IBus` adds an explicit fan-out method, and `RouteAsync` tightens its destinations type:

```csharp
public interface IBus
{
    // unchanged signatures elided
    Task SendAsync<T>(T message, SendOptions? options = null, CancellationToken ct = default) where T : Message;
    Task SendToManyAsync<T>(T message, IReadOnlyList<string> endPoints,
        SendOptions? options = null, CancellationToken ct = default) where T : Message;
    Task RouteAsync<T>(T message, IReadOnlyList<string> destinations,
        SendOptions? options = null, CancellationToken ct = default) where T : Message;
}
```

The runtime guard at `Bus.cs:142–147` (mutual exclusion of `EndPoint`/`EndPoints`) is no longer reachable and deletes.

### 6. `IConsumer`

```csharp
public interface IConsumer : IAsyncDisposable
{
    bool IsConnected { get; }
    bool IsCancelledByBroker { get; }

    Task StartConsumingAsync(string queueName, IReadOnlyList<string> messageTypes,
        ConsumerEventHandler eventHandler, CancellationToken ct = default);
}
```

### 7. `IMessageDispatcher`

```csharp
public interface IMessageDispatcher
{
    Task<ConsumeEventResult> DispatchAsync(ReadOnlyMemory<byte> messageBytes, string messageType,
        IReadOnlyDictionary<string, object> headers, CancellationToken ct = default);
}
```

The internal pipeline downstream still uses `IDictionary<string,object>` for the middleware-mutable seam. The public-boundary `IReadOnlyDictionary` is copied into a mutable dict at the seam by the framework; one allocation, scoped to the dispatch.

### 8. `IConsumeContext` + `ReplyOptions`

```csharp
public readonly record struct ReplyOptions
{
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

public interface IConsumeContext
{
    IBus Bus { get; }
    IReadOnlyDictionary<string, object> Headers { get; }
    string? MessageId { get; }
    Guid CorrelationId { get; }
    CancellationToken CancellationToken { get; }

    Task ReplyAsync<TReply>(TReply message, ReplyOptions? options = null,
        CancellationToken ct = default) where TReply : Message;
}
```

`ReplyOptions` carries only `Headers`. No `EndPoint` (reply destination is implicit from the request's reply-to header), no `RoutingKey` (replies don't fan out), no `CorrelationId` (auto-correlated via the request's `MessageId`).

### 9. `TimeoutData`

```csharp
public sealed class TimeoutData
{
    public Guid Id { get; set; }
    public string? Destination { get; set; }   // was: string = string.Empty
    public Guid ProcessManagerId { get; set; }
    public DateTimeOffset Time { get; set; }
    public IReadOnlyDictionary<string, object> Headers { get; init; }
        = new Dictionary<string, object>(StringComparer.Ordinal);
    public bool Locked { get; set; }
    public Guid LockedBy { get; set; }
    public DateTimeOffset? LockExpiresAt { get; set; }
}
```

`Destination` is the only change. Persistence layers (InMemory + MongoDb) treat `null` as "no destination set" — same semantics empty string had, just typed honestly.

### 10. `IHasCorrelationId` + `IAggregatorPersistor`

```csharp
namespace ServiceConnect.Interfaces;

public interface IHasCorrelationId
{
    Guid CorrelationId { get; }
}
```

`Message` declares it (no behaviour change for any user already deriving from `Message`):

```csharp
public class Message(Guid correlationId) : IHasCorrelationId
{
    public Guid CorrelationId { get; init; } = correlationId;
}
```

`IAggregatorPersistor` parameter types tighten on the data-carrying paths only.
`RemoveDataAsync(string name, Guid correlationId, ...)` already takes the correlation
id directly and is unchanged. `RemoveSnapshotAsync` continues to take the snapshot it
should remove. The full v8 surface:

```csharp
public interface IAggregatorPersistor
{
    Task InsertDataAsync(IHasCorrelationId data, string name, CancellationToken ct = default);
    Task<IReadOnlyList<IHasCorrelationId>> GetDataAsync(string name, CancellationToken ct = default);
    Task<IAggregatorSnapshot> GetSnapshotAsync(string name, CancellationToken ct = default);
    Task RemoveDataAsync(string name, Guid correlationId, CancellationToken ct = default);
    Task RemoveAllAsync(string name, CancellationToken ct = default);
    Task RemoveSnapshotAsync(string name, IAggregatorSnapshot snapshot, CancellationToken ct = default);
    Task<int> CountAsync(string name, CancellationToken ct = default);
}
```

Factory ctor convention removed:

```csharp
// Before:
public InMemoryAggregatorPersistor(string connectionString, string databaseName,
    string collectionName, TimeProvider? timeProvider = null)
{
    // first three args ignored
}

// After:
public InMemoryAggregatorPersistor(TimeProvider? timeProvider = null) { ... }
```

`MongoDbAggregatorPersistor`'s ctor is unchanged (it actually uses `connectionString`, `databaseName`, `collectionName`). DI registration in each adapter package's extension method names only the dependencies that adapter actually needs.

The reflection path in `InMemoryAggregatorPersistor` (the `CorrelationIdAccessors` cache and the throwing-delegate workaround) is deleted; `data.CorrelationId` is direct property access through the interface.

---

## Internal cascade

Files changed inside the library, with no public-API impact:

| Site | Change |
|---|---|
| `src/ServiceConnect/Bus.cs` | `SendContext.MessageBytes` set to `ROM<byte>`; `Envelope.Body` set to `ROM<byte>`; `Serialize` callers use `ArrayBufferWriter<byte>`; `EndPoint`/`EndPoints` mutex guard deleted; `SendAsync` simplifies to single-target; multi-endpoint loop moves to new `SendToManyAsync` impl. |
| `src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs` | All four publish sites: body parameter `byte[]` → `ROM<byte>`; the `messageBytes.AsMemory()` casts at lines 189/247/295/342 delete (already `ROM<byte>`). |
| `src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs` | Headers param `IDictionary<string,string>?` → `IReadOnlyDictionary<string,string>?`. |
| `src/ServiceConnect/Services/MessageDispatcher.cs` | Public-boundary headers param tightens to `IReadOnlyDictionary<string,object>`; downstream pipeline keeps `IDictionary<string,object>` for middleware mutation; one copy at the seam. |
| `src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs` | Delete `GetCorrelationId`, `CorrelationIdAccessors` static field, the unused-ctor params; direct `data.CorrelationId` access via `IHasCorrelationId`. |
| `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs` | Same — replace any reflection / object-typed CorrelationId access with `data.CorrelationId`. |
| `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceExtensions.cs`, `src/ServiceConnect.Persistence.MongoDb/MongoDbPersistenceExtensions.cs` | Update DI registrations to new ctor shapes. |
| `src/ServiceConnect/Services/NewtonsoftJsonMessageSerializer.cs` | **Deleted entirely.** |
| `src/ServiceConnect/Services/SystemTextJsonMessageSerializer.cs` | **New file** (the v8 implementation). |
| `src/ServiceConnect/ServiceConnect.csproj` | Remove `Newtonsoft.Json` package reference. |
| `src/ServiceConnect/Services/IO/ReadOnlyMemoryStream.cs`, `ReadOnlySequenceStream.cs` | Likely deletable once STJ replaces Newtonsoft (used today by Newtonsoft `Deserialize(ROM<byte>)` and `Deserialize(ROS<byte>)` to wrap as `Stream`). Verify in plan. |
| `examples/**` | Update for new API shapes — singular endpoint cleanup, `ReplyOptions` usage, custom aggregator data types if any. |

---

## Wire-compatibility & testing strategy

**Test project:** `src/ServiceConnect.SerializationCompatTests/` (new).

**Corpus contents:**

A representative collection of `Message`-derived types covering:
- Primitives: `int`, `long`, `double`, `decimal`, `bool`, `string`, `Guid`
- Collections: `List<T>`, `Dictionary<,>`, arrays, `IEnumerable` shapes
- Nested objects, multiple levels deep
- Nullable fields (both nullable value types and nullable references)
- `DateTime` / `DateTimeOffset` / `TimeSpan` in all `DateTimeKind` values (`Utc`, `Local`, `Unspecified`)
- Enums (default int representation)
- Base64 byte fields (round-trip exact bytes)

**Round-trip assertions per corpus item:**

1. **STJ → STJ:** serialise + deserialise; structural equality on the result.
2. **Newtonsoft → STJ:** Newtonsoft serialises (reference impl, kept only for these tests), STJ deserialises; structural equality.
3. **STJ → Newtonsoft:** STJ serialises, Newtonsoft deserialises; structural equality.
4. **Wire-byte equivalence:** STJ output JSON-equivalent to Newtonsoft output. May differ in Unicode escape form (`é` vs `é`); parsed value must match.

**Pathological inputs:**
- Deeply nested (≥30 levels): asserts neither library overflows; STJ respects `MaxDepth = 32`.
- Very large strings (~1 MB): asserts both succeed.
- `NaN` / `Infinity` doubles: asserts STJ rejects (documented behaviour change vs Newtonsoft, called out in release notes).
- Mixed-case property names on the inbound side: asserts case-sensitive matching.

**Newtonsoft scoping:** referenced **only** in this test project. The shipped library packages contain no Newtonsoft reference after this change.

**CI:** test project runs on every PR; failures block merge.

---

## Documentation deliverables (paired with v8 release)

- v8 release-notes section on the website: per-API change with before/after code snippets.
- XML doc updates everywhere the public surface changes — `IConsumeContext.ReplyAsync`, `IBus.SendToManyAsync`, `ReplyOptions`, `IProducer.{Publish,Send,SendBytes}Async`, `IHasCorrelationId`, `IAggregatorPersistor`, `TimeoutData.Destination`.
- Migration guide: "Upgrading from v7 to v8" with explicit call-out to the JSON wire-compat behaviour: "your messages deserialise across mixed v7/v8 deployments because of the wire-compat corpus."
- Updated quickstart on the website to use the v8 API shapes.

These docs are deliverables of Group A even though Group C in the roadmap covers the broader docs work — the v8 release-notes and migration guide are scoped tightly to what changes here.

---

## Risks & mitigations

| Risk | Mitigation |
|---|---|
| STJ produces subtly different JSON for some edge case the corpus misses | Corpus is the safety net; failures land as new corpus entries; the corpus is part of the public regression contract |
| Custom aggregator data types in user code break (don't implement `IHasCorrelationId`) | Compile error is loud; release notes call out as the headline aggregator break; trivial fix at the user's end |
| `byte[]` → `ROM<byte>` ripples through downstream user middleware | Compile error is loud; release notes show the replacement pattern |
| Mixed v7/v8 deployment during rollout | Wire-compat corpus IS the proof; if it passes on PR, mixed deployments work |
| `params string[]` ergonomic regret on `SendToManyAsync` | Decided closed during brainstorm; reopen only if a real call-site survey shows pain |
| STJ rejects `NaN`/`Infinity` doubles that worked under Newtonsoft | Document as a known break in release notes; users with such payloads add a custom converter |
| Existing aggregator types deriving from non-`Message` base classes (rare) | Compile error; users implement `IHasCorrelationId` (one-line add) |

---

## Decisions banked from brainstorm

For audit trail:

1. **Q1:** deprecation posture — **(a) clean break, no shims**.
2. **Q2:** `IMessageSerializer` redesign timing — **(b) ship STJ in v8 + cross-version test corpus**.
3. **Q3:** `IAggregatorPersistor` strategy — **(a) refactor ctor convention AND introduce `IHasCorrelationId`**.
4. **Q4 (revised):** `SendOptions` endpoint shape — **(I) single endpoint stays as `string?`; fan-out via new `IBus.SendToManyAsync(...)` with `IReadOnlyList<string>` (not `params`)**.
5. **Q5:** `IMessageSerializer` shape + producer body type — **drop `byte[] Serialize`; `IProducer` body → `ROM<byte>` everywhere**.
6. **Q6:** consolidation — **`ReplyOptions { Headers }` only; `TimeoutData.Destination` → `string?`; `IReadOnly*` on transport contracts; `RouteAsync` destinations also tightened; `RequestOptions.EndPoints` removed for parallel cleanup**.
