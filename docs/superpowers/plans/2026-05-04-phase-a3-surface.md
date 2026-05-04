# Phase A.3 — Public-surface tightening + ReplyOptions + streaming cascade Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land the remaining v8 public-API breaks — `ReplyOptions` struct + `IConsumeContext.ReplyAsync` shape, `IConsumer.StartConsumingAsync` and `IMessageDispatcher.DispatchAsync` immutability tightening, `IBus.RouteAsync` destinations tightening, `SendOptions.EndPoints`/`RequestOptions.EndPoints` removal with `IBus.SendToManyAsync` replacement, the streaming-path body-type cascade through `IMessageBusReadStream.Write` and `IMessageBusWriteStream.WriteAsync`, plus the examples migration and consolidated v8 release notes.

**Architecture:** Third and final phase of Group A's v8 public-API tightening. Phases A.1 (foundations) and A.2 (serializer + body-type cascade) are landed. A.3 is the remaining surface — most are mechanical type tightenings that compose with A.2's principles. The streaming cascade is the largest single change and goes last among the surface tasks. Examples + release notes close the phase.

**Tech Stack:** .NET 8/10, C# 12/14, xUnit. Dotnet wrapper at `~/.local/bin/dotnet` enforces cgroup limits (CPUQuota=800%, MemoryMax=8G, TasksMax=200) — call `dotnet` normally; do NOT use `/usr/lib/dotnet/dotnet` directly.

**Key v8 invariants:** `TreatWarningsAsErrors=true`, `Nullable=enable`, `EnforceCodeStyleInBuild=true`, `GenerateDocumentationFile=true` (so XML docs are mandatory on public types). Analyzers: Meziantou + VS Threading + NetAnalyzers + IDE0xxx.

**Lessons from Phases A.1 + A.2 baked into this plan:**
1. Every pre-commit verification step runs `dotnet build src/ServiceConnect.slnx -m:1` (full solution, serial), not just per-project. Catches `EndToEndTests` collateral that unit-tests-only builds miss.
2. Each retyping commit includes a "variance audit": grep for `as IList<`, `as IDictionary<`, `as Span<`, `as Memory<` in touched files; verify each remains live under the new types.
3. Spec/plan agreement check before implementation kicks off — diff every code block in the spec against the actual current source.
4. Direct unit test for any new public-API method (don't rely on integration coverage alone).
5. Run `SerializationCompatTests` after any task that could perturb how the inbound serializer is invoked — A.3 doesn't change the serializer but the consume-side changes touch `MessageDispatcher` which calls into it.
6. Examples updates land *last* in the phase, after surface changes have stabilised, to avoid swamping surface-change diffs with example diffs.
7. Release-notes consolidation is the final commit, covering A.1 + A.2 + A.3 together as one v8 migration story.

---

## File Structure

**New files:**
- `src/ServiceConnect.Interfaces/Options/ReplyOptions.cs` — small record-struct mirroring `PublishOptions`/`SendOptions` shape, carrying only `Headers`.

**Files modified (production):**
- `src/ServiceConnect.Interfaces/Bus/IConsumeContext.cs` — `ReplyAsync` signature uses `ReplyOptions?` instead of raw `IDictionary<string,string>?`.
- `src/ServiceConnect.Interfaces/Bus/IConsumer.cs:23` — `IList<string>` → `IReadOnlyList<string>` for messageTypes.
- `src/ServiceConnect.Interfaces/Bus/IMessageDispatcher.cs:11` — `IDictionary<string,object>` → `IReadOnlyDictionary<string,object>` for headers.
- `src/ServiceConnect.Interfaces/Bus/IBus.cs:49` — `RouteAsync` destinations: `IList<string>` → `IReadOnlyList<string>`. Plus new `SendToManyAsync` method (line ~50).
- `src/ServiceConnect.Interfaces/Options/SendOptions.cs` — drop `EndPoints` property.
- `src/ServiceConnect.Interfaces/Options/RequestOptions.cs` — drop `EndPoints` property; update `ExpectedReplyCount` XML doc to remove the EndPoints fallback clause.
- `src/ServiceConnect.Interfaces/Streaming/IMessageBusReadStream.cs` — `Write` takes `ReadOnlyMemory<byte>`.
- `src/ServiceConnect.Interfaces/Streaming/IMessageBusWriteStream.cs` — `WriteAsync(byte[], int, int, CT)` → `WriteAsync(ReadOnlyMemory<byte>, CT)`.
- `src/ServiceConnect/Services/ConsumeContext.cs` — implements new `ReplyAsync(TReply, ReplyOptions?, CT)` signature.
- `src/ServiceConnect/Services/RequestReplyManager.cs` — drops `EndPoints`-fan-out branch in request methods; update `ExpectedReplyCount` fallback logic.
- `src/ServiceConnect/Services/MessageDispatcher.cs` — `DispatchAsync` parameter type tightens to `IReadOnlyDictionary<string,object>`; internal copy at the public-boundary seam if downstream needs mutable.
- `src/ServiceConnect/Services/MessageBusReadStream.cs` — `Write(ReadOnlyMemory<byte>, long)`; internal `ConcurrentDictionary<long, byte[]>` storage stays (RabbitMQ.Client v7 doesn't extend buffer lifetime past the callback, so copy-on-write is required for retention) — only the API surface tightens.
- `src/ServiceConnect/Services/MessageBusWriteStream.cs` — `WriteAsync(ReadOnlyMemory<byte>, CT)`; threads directly to `Producer.SendBytesAsync(ROM<byte>)` for genuine zero-copy on the outbound side.
- `src/ServiceConnect/Bus.cs` — drops the SendAsync mutex guard at lines 144-149; the multi-endpoint loop at lines 171-187 moves into a new `SendToManyAsync<T>(...)` method.
- `src/ServiceConnect/Services/Processors/StreamProcessor.cs:158` — drops `messageBytes.ToArray()`; passes `messageBytes` directly to `state.Stream.Write(...)`.
- `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs` — `StartConsumingAsync` parameter type follows `IConsumer` change; internal `messageTypes` storage stays the same (read-only views compose).
- `src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs` — same as above.

**Files modified (tests):**
- `src/ServiceConnect.UnitTests/` — every test that constructs `SendOptions { EndPoints = ... }` or `RequestOptions { EndPoints = ... }` migrates to `IBus.SendToManyAsync(...)` (Send) or to `EndPoint = ...` single-target form (Request). Every test using `IConsumeContext.ReplyAsync(message, headers, ct)` migrates to `ReplyAsync(message, new ReplyOptions { Headers = ... }, ct)`. Every test using `IMessageBusReadStream.Write(byte[], long)` migrates to `Write(ROM<byte>, long)` (typically just an `.AsMemory()` wrap). Every test using `IMessageBusWriteStream.WriteAsync(buf, off, cnt, ct)` migrates to `WriteAsync(buf.AsMemory(off, cnt), ct)`.
- Mock setups using `It.IsAny<IDictionary<string,object>>()` for `MessageDispatcher.DispatchAsync` headers → `It.IsAny<IReadOnlyDictionary<string,object>>()`.
- Mock setups using `It.IsAny<IList<string>>()` for `IConsumer.StartConsumingAsync` messageTypes → `It.IsAny<IReadOnlyList<string>>()`.
- E2E tests in `src/ServiceConnect.EndToEndTests/` — same migration patterns.

**Files modified (examples):**
- `examples/ScatterGather/` — most likely consumer of `SendRequestMultiAsync` and the deprecated `RequestOptions.EndPoints` fan-out. Migrate to `IBus.PublishRequestAsync` + correlate replies.
- `examples/ContentBasedRouting/`, `examples/RoutingSlip/`, `examples/CompetingConsumers/`, `examples/PolymorphicMessages/`, `examples/RequestReply/`, `examples/PublishSubscribe/`, `examples/PointToPoint/`, `examples/ProcessManager/`, `examples/Aggregator/`, `examples/Filters/`, `examples/Streaming/`, `examples/Telemetry/` — touch only those that reference changed APIs (most won't; spot-check by `grep -rn 'EndPoints\|ReplyAsync\|RouteAsync\|StartConsumingAsync\|DispatchAsync' examples/`).

**Files modified (docs):**
- `website/src/content/docs/releases.mdx` — extend the v8 highlights section (already drafted in A.2 follow-up `28fc687d`) with A.3's breaking changes.
- `architecture-fix-plan.md` — final status flip to `*Phase A.3 done*` (or remove the A.3 mention entirely; the whole Group A is done after this phase).

**Out of scope for this phase:**
- Group B (metrics rollout), Group C (broader docs/contracts), Groups D-G — separate brainstorm cycles per the roadmap.
- `MessageBusReadStream` storage layer change to `ROM<byte>`-keyed dict — out of scope for the reasons documented above (RabbitMQ.Client buffer lifetime). Logged as future work if a different transport ever delivers buffers that extend past the callback.

---

## Variance audit candidates

After every retyping commit (Tasks 4–9), grep the diffed files for these patterns and confirm each `as` cast still has a runtime-realistic chance of succeeding under the new types:

```bash
grep -nE 'as IList<|as ICollection<|as IDictionary<|as IReadOnlyList<|as IReadOnlyCollection<|as IReadOnlyDictionary<' \
  src/ServiceConnect/ src/ServiceConnect.Interfaces/ src/ServiceConnect.Client.RabbitMQ/ \
  --include='*.cs' --recursive | grep -v 'obj/\|bin/'
```

Expected: no hits in the touched range, or every hit is verifiably live under the new types.

---

## Task 1: Pre-flight — extend spec for streaming cascade

The streaming cascade was identified as Phase A.3 scope by the A.2 final review, after the original spec was written. Extend the spec to document the new scope before implementation begins.

**Files:**
- Modify: `docs/superpowers/specs/2026-05-03-v8-public-api-tightening-design.md`

- [ ] **Step 1: Add streaming-cascade section to the spec**

In `docs/superpowers/specs/2026-05-03-v8-public-api-tightening-design.md`, find the closing of Section 9 (`### 9. TimeoutData` — about `Destination` nullability) and insert a new Section 11 (or 9b) before "Internal cascade":

```markdown
### 11. Streaming body-type cascade (`IMessageBusReadStream`, `IMessageBusWriteStream`)

```csharp
public interface IMessageBusReadStream
{
    void Write(ReadOnlyMemory<byte> data, long packetNumber);   // was: byte[] data
    // ... rest unchanged
}

public interface IMessageBusWriteStream : IAsyncDisposable
{
    Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default);  // was: byte[], int offset, int count
    Task CloseAsync(CancellationToken ct = default);
}
```

Reasoning:
- **Inbound (`IMessageBusReadStream.Write`)**: API consistency with the rest of the v8 surface. The internal storage stays `ConcurrentDictionary<long, byte[]>` because RabbitMQ.Client v7 does not extend the buffer lifetime past the consumer callback — `data.ToArray()` is required to retain the data. This is an *API consistency* change, not a perf change.
- **Outbound (`IMessageBusWriteStream.WriteAsync`)**: drops the `int offset, int count` parameters; callers slice via `buffer.AsMemory(offset, count)`. The impl threads `ROM<byte>` directly to `IProducer.SendBytesAsync(ReadOnlyMemory<byte>)` (already ROM-typed after Phase A.2), genuinely eliminating a buffer copy on the outbound stream path.
- `IMessageBusReadStream.Read()` returning `byte[]` and `ReadSequence()` returning `ReadOnlySequence<byte>` are unchanged — they're the read-out APIs whose consumers are out of scope for v8 surface tightening.

BREAKING CHANGE: callers of `IMessageBusWriteStream.WriteAsync(byte[], int, int, CT)` migrate to `WriteAsync(buffer.AsMemory(offset, count), ct)` — one-line change at every call site.
```

- [ ] **Step 2: Commit the spec extension**

```bash
git add docs/superpowers/specs/2026-05-03-v8-public-api-tightening-design.md
git commit -m "$(cat <<'EOF'
docs(spec): extend v8 design with streaming body-type cascade (Phase A.3)

The Phase A.2 final review identified the streaming-path body-type
cascade as a Phase A.3 candidate that wasn't covered by the original
spec. Add Section 11 documenting:

- IMessageBusReadStream.Write byte[] -> ReadOnlyMemory<byte> (API
  consistency only — internal storage stays byte[] because RabbitMQ.Client
  v7 doesn't extend buffer lifetime past the callback).
- IMessageBusWriteStream.WriteAsync(byte[], int, int, CT) ->
  WriteAsync(ReadOnlyMemory<byte>, CT). Drops offset/count parameters;
  threads ROM<byte> directly to IProducer.SendBytesAsync for genuine
  zero-copy on the outbound stream path.

The spec now covers the full A.3 scope before implementation begins.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Introduce `ReplyOptions` struct

**Files:**
- Create: `src/ServiceConnect.Interfaces/Options/ReplyOptions.cs`

- [ ] **Step 1: Create the struct**

Create `src/ServiceConnect.Interfaces/Options/ReplyOptions.cs`:

```csharp
namespace ServiceConnect.Interfaces.Options;

/// <summary>
/// Optional settings for reply operations sent through <see cref="IConsumeContext.ReplyAsync"/>.
/// Mirrors the shape of <see cref="PublishOptions"/> and <see cref="SendOptions"/> for surface
/// consistency.
/// </summary>
/// <remarks>
/// Carries only <see cref="Headers"/>: replies do not need an endpoint (the destination is the
/// request's reply-to header), do not need a routing key (replies don't fan out), and do not
/// need a correlation id (auto-correlated via the request's <c>MessageId</c>).
/// </remarks>
public readonly record struct ReplyOptions
{
    /// <summary>
    /// Additional headers to attach to the reply message. Read-only view so the framework does
    /// not invite concurrent-caller mutation of a shared dictionary while the async pipeline is
    /// iterating it.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}
```

- [ ] **Step 2: Build the Interfaces project**

Run: `dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj`
Expected: 0 errors, 0 warnings.

- [ ] **Step 3: Full solution build**

Run: `dotnet build src/ServiceConnect.slnx -m:1`
Expected: 0 errors, 0 warnings. (Nothing yet uses `ReplyOptions`; the type is added but not referenced.)

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.Interfaces/Options/ReplyOptions.cs
git commit -m "$(cat <<'EOF'
feat(interfaces): introduce ReplyOptions

Mirrors PublishOptions/SendOptions shape for IConsumeContext.ReplyAsync.
Carries only Headers — replies don't need endpoint, routing key, or
correlation id (auto-correlated via the request's MessageId).

Not yet wired into IConsumeContext; the next commit updates the
ReplyAsync signature.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Update `IConsumeContext.ReplyAsync` to use `ReplyOptions`

Atomic commit: interface change + impl update + test updates.

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Bus/IConsumeContext.cs:30`
- Modify: `src/ServiceConnect/Services/ConsumeContext.cs` — implementation of `ReplyAsync`
- Modify: every test file that calls `ReplyAsync(message, headers, ct)` with the raw dictionary form

- [ ] **Step 1: Inventory the call sites**

Run: `grep -rn 'ReplyAsync' src/ --include='*.cs' | grep -v 'obj/\|bin/'`
Note every site. Tests under `src/ServiceConnect.UnitTests/` will be the bulk; production has the interface declaration + the `ConsumeContext.cs` impl.

- [ ] **Step 2: Update the interface**

Edit `src/ServiceConnect.Interfaces/Bus/IConsumeContext.cs:30`:

```csharp
// Before:
Task ReplyAsync<TReply>(TReply message, IDictionary<string, string>? headers = null, CancellationToken cancellationToken = default) where TReply : Message;

// After:
Task ReplyAsync<TReply>(TReply message, Options.ReplyOptions? options = null, CancellationToken cancellationToken = default) where TReply : Message;
```

Add `using ServiceConnect.Interfaces.Options;` at the top of the file if not already present.

Update the XML doc above the method to mention `ReplyOptions`:

```csharp
/// <summary>
/// Sends <paramref name="message"/> back to the requester as a reply, setting the
/// <c>ResponseMessageId</c> header so the request/reply manager correlates it to the
/// originating <c>SendRequestAsync</c> call.
/// </summary>
/// <param name="message">The reply message.</param>
/// <param name="options">Optional headers for the reply. The reply destination is implicit
/// from the request's reply-to header; no endpoint or routing key applies.</param>
/// <param name="cancellationToken">A token that cancels the reply send.</param>
```

- [ ] **Step 3: Update the `ConsumeContext` implementation**

Find the `ReplyAsync` impl in `src/ServiceConnect/Services/ConsumeContext.cs`. Update its signature to match the new interface:

```csharp
public Task ReplyAsync<TReply>(TReply message, Options.ReplyOptions? options = null, CancellationToken cancellationToken = default) where TReply : Message
{
    // Adapt internal call to use options?.Headers in place of the previous raw `headers` parameter.
    // The body's existing logic that reads `headers` becomes `options?.Headers`.
    // ... [rest of method body, with `headers` replaced by `options?.Headers`]
}
```

Read the current `ConsumeContext.cs` file (likely 100-200 lines) and update the body to consume `options?.Headers` everywhere `headers` was previously consumed.

- [ ] **Step 4: Update test call sites**

Every test that calls `ReplyAsync(message, headers, ct)` with `headers` as `IDictionary<string,string>` migrates to one of:

```csharp
// Most common case — caller passed null:
await ctx.ReplyAsync(reply, cancellationToken: ct);
// Or:
await ctx.ReplyAsync(reply);

// Caller passed a dict:
await ctx.ReplyAsync(reply, new ReplyOptions { Headers = dict }, ct);
```

Find via grep:
```bash
grep -rn 'ReplyAsync' src/ServiceConnect.UnitTests/ src/ServiceConnect.EndToEndTests/ --include='*.cs' | grep -v 'obj/\|bin/'
```

Update each. Add `using ServiceConnect.Interfaces.Options;` to test files that now need to construct `ReplyOptions`.

- [ ] **Step 5: Full solution build**

Run: `dotnet build src/ServiceConnect.slnx -m:1`
Expected: 0 errors, 0 warnings.

- [ ] **Step 6: Run unit tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-build --no-restore`
Expected: all tests pass.

- [ ] **Step 7: Variance audit**

Run: `grep -nE 'as IDictionary<string, string>' src/ServiceConnect/Services/ConsumeContext.cs`
Expected: no hits, or every hit is verifiably live under the new `ReplyOptions` shape.

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect.Interfaces/Bus/IConsumeContext.cs src/ServiceConnect/Services/ConsumeContext.cs src/ServiceConnect.UnitTests/ src/ServiceConnect.EndToEndTests/

git commit -m "$(cat <<'EOF'
feat(interfaces)!: IConsumeContext.ReplyAsync uses ReplyOptions

ReplyAsync now takes ReplyOptions? instead of a raw
IDictionary<string,string>? headers parameter, mirroring the
PublishOptions/SendOptions/RequestOptions pattern.

BREAKING CHANGE: callers passing custom headers must now construct
new ReplyOptions { Headers = dict } instead of passing the dict
directly. Callers passing null are unaffected (the parameter is still
optional).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Tighten `IConsumer` and `IMessageDispatcher` collection types (atomic)

Per A.2 final review recommendation — bundle these together. Both are pure type tightenings on the inbound path; splitting them creates an intermediate state where one is `IReadOnly*` and the other isn't, inviting variance-cast risk.

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Bus/IConsumer.cs:23`
- Modify: `src/ServiceConnect.Interfaces/Bus/IMessageDispatcher.cs:11`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs` — implementer
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs` — implementer
- Modify: `src/ServiceConnect/Services/MessageDispatcher.cs` — implementer
- Modify: `src/ServiceConnect/Bus.cs` — internal callers
- Modify: tests using `It.IsAny<IList<string>>()` or `It.IsAny<IDictionary<string,object>>()` for these methods

- [ ] **Step 1: Update `IConsumer`**

Edit `src/ServiceConnect.Interfaces/Bus/IConsumer.cs:23`:

```csharp
// Before:
Task StartConsumingAsync(string queueName, IList<string> messageTypes, ConsumerEventHandler eventHandler, CancellationToken cancellationToken = default);

// After:
Task StartConsumingAsync(string queueName, IReadOnlyList<string> messageTypes, ConsumerEventHandler eventHandler, CancellationToken cancellationToken = default);
```

- [ ] **Step 2: Update `IMessageDispatcher`**

Edit `src/ServiceConnect.Interfaces/Bus/IMessageDispatcher.cs:11`:

```csharp
// Before:
Task<ConsumeEventResult> DispatchAsync(ReadOnlyMemory<byte> messageBytes, string messageType, IDictionary<string, object> headers, CancellationToken cancellationToken = default);

// After:
Task<ConsumeEventResult> DispatchAsync(ReadOnlyMemory<byte> messageBytes, string messageType, IReadOnlyDictionary<string, object> headers, CancellationToken cancellationToken = default);
```

- [ ] **Step 3: Update RabbitMQ consumer implementers**

Edit `src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs` and `RabbitMqConsumerHost.cs`. Both have `StartConsumingAsync` methods that need the parameter type updated to `IReadOnlyList<string>`. Internal usage (`foreach` over messageTypes) is unchanged — `IReadOnlyList<T>` supports iteration.

- [ ] **Step 4: Update `MessageDispatcher.DispatchAsync`**

Edit `src/ServiceConnect/Services/MessageDispatcher.cs:46`:

```csharp
public async Task<ConsumeEventResult> DispatchAsync(ReadOnlyMemory<byte> messageBytes, string messageType, IReadOnlyDictionary<string, object> headers, CancellationToken cancellationToken = default)
```

The downstream pipeline still uses `IDictionary<string,object>` for middleware mutation. At the public-boundary seam (where the dispatcher passes headers downstream), the framework copies `IReadOnlyDictionary` → `IDictionary` if mutation is needed:

```csharp
// Inside DispatchAsync, if headers needs to flow into a mutable downstream:
var mutableHeaders = headers as IDictionary<string, object>
    ?? new Dictionary<string, object>(headers, StringComparer.Ordinal);
// ... use mutableHeaders for the middleware pipeline
```

The `as IDictionary<string,object>` fast-path may succeed if the caller passed a `Dictionary<,>` instance (which implements both interfaces) — saves the copy in the common case. Verify with the variance audit at Step 8.

Read `MessageDispatcher.cs` and make the appropriate code change. Look for sites that construct `Envelope`, the middleware chain, or the per-processor `headers` argument — those are the consumers of the parameter inside this method.

- [ ] **Step 5: Update `Bus.cs` callers (if any)**

Run: `grep -nE 'IList<string>|IDictionary<string, object>' src/ServiceConnect/Bus.cs | grep -v 'obj/\|bin/'`
For any hit in Bus.cs that's a parameter of `IConsumer.StartConsumingAsync` or `IMessageDispatcher.DispatchAsync`, the implicit conversion from `List<string>` and `Dictionary<string,object>` to the readonly views works automatically — no edit needed unless Bus.cs explicitly constructs an `IList<string>` or `IDictionary<string,object>` to pass in. If so, change to the readonly counterpart.

- [ ] **Step 6: Update test fixtures**

Find via grep:
```bash
grep -rn 'StartConsumingAsync\|DispatchAsync' src/ServiceConnect.UnitTests/ src/ServiceConnect.EndToEndTests/ --include='*.cs' | grep -v 'obj/\|bin/'
```

Update each:
- `It.IsAny<IList<string>>()` → `It.IsAny<IReadOnlyList<string>>()`
- `It.IsAny<IDictionary<string, object>>()` → `It.IsAny<IReadOnlyDictionary<string, object>>()`
- Tests that pass a `List<string>` or `Dictionary<string,object>` to these methods continue to compile (implicit conversion).

- [ ] **Step 7: Full solution build**

Run: `dotnet build src/ServiceConnect.slnx -m:1`
Expected: 0 errors, 0 warnings.

- [ ] **Step 8: Variance audit**

Run:
```bash
grep -nE 'as IList<|as ICollection<|as IDictionary<' \
  src/ServiceConnect/Bus.cs \
  src/ServiceConnect/Services/MessageDispatcher.cs \
  src/ServiceConnect.Client.RabbitMQ/Consumer/*.cs
```

For every hit, confirm the cast remains live under the new readonly types. Specifically: `as IDictionary<string,object>` on a `headers` parameter of type `IReadOnlyDictionary<string,object>` only succeeds when the runtime type is a `Dictionary<,>` (which implements both interfaces). That's the expected fast path; the fallback copy at the seam handles non-`Dictionary<,>` runtime types.

- [ ] **Step 9: Run unit tests + SerializationCompatTests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-build --no-restore`
Expected: all tests pass.

Run: `dotnet test src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj --no-build --no-restore`
Expected: all tests pass (the consume-side change touches `MessageDispatcher` which calls `IMessageSerializer`; corpus tests verify no behavioural shift).

- [ ] **Step 10: Commit**

```bash
git add \
  src/ServiceConnect.Interfaces/Bus/IConsumer.cs \
  src/ServiceConnect.Interfaces/Bus/IMessageDispatcher.cs \
  src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs \
  src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs \
  src/ServiceConnect/Services/MessageDispatcher.cs

# Plus any test files modified
# Plus Bus.cs if it needed an edit

git commit -m "$(cat <<'EOF'
refactor(interfaces)!: IConsumer + IMessageDispatcher use IReadOnly* collections

IConsumer.StartConsumingAsync messageTypes:
  IList<string> -> IReadOnlyList<string>

IMessageDispatcher.DispatchAsync headers:
  IDictionary<string, object> -> IReadOnlyDictionary<string, object>

Pure type tightenings on the inbound path; the underlying internal
pipeline still uses mutable IDictionary<string,object> for the
middleware-mutable seam, with a one-time as-cast (fast path) plus
fallback copy at the public boundary.

Bundled atomically per the Phase A.2 final review recommendation:
splitting them would create an intermediate state where one parameter
is IReadOnly* and the sibling isn't, which invites variance-cast
confusion.

BREAKING CHANGE: callers must pass IReadOnlyList<string>-compatible
messageTypes (List<string>, IReadOnlyList<string>, etc.) and
IReadOnlyDictionary<string, object>-compatible headers
(Dictionary<string, object> still works via implicit conversion).
Mock setups using It.IsAny<IList<string>>() / It.IsAny<IDictionary<...>>
must update to the IReadOnly* variants.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Tighten `IBus.RouteAsync` destinations

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Bus/IBus.cs:49`
- Modify: `src/ServiceConnect/Bus.cs` — `RouteAsync` impl
- Modify: tests calling `RouteAsync(message, destinations, ct)` with `IList<string>` destinations

- [ ] **Step 1: Update the interface**

Edit `src/ServiceConnect.Interfaces/Bus/IBus.cs:49`:

```csharp
// Before:
Task RouteAsync<T>(T message, IList<string> destinations, CancellationToken cancellationToken = default) where T : Message;

// After:
Task RouteAsync<T>(T message, IReadOnlyList<string> destinations, CancellationToken cancellationToken = default) where T : Message;
```

- [ ] **Step 2: Update `Bus.RouteAsync` impl**

Find `RouteAsync` in `src/ServiceConnect/Bus.cs`. Update the parameter type to `IReadOnlyList<string>`. The impl uses `destinations.Count`, indexer, and `.Skip(1)` (for `BuildRoutingSlip`); all are valid on `IReadOnlyList<T>`.

- [ ] **Step 3: Update tests**

Grep for `RouteAsync`:
```bash
grep -rn 'RouteAsync' src/ServiceConnect.UnitTests/ src/ServiceConnect.EndToEndTests/ --include='*.cs' | grep -v 'obj/\|bin/'
```

Tests passing `List<string>` keep working (implicit conversion). Tests passing `IList<string>` explicitly may need updating (likely just the type annotation in a `var` or method-local declaration).

- [ ] **Step 4: Full solution build**

Run: `dotnet build src/ServiceConnect.slnx -m:1`
Expected: 0 errors, 0 warnings.

- [ ] **Step 5: Run unit tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-build --no-restore`
Expected: all tests pass.

- [ ] **Step 6: Variance audit**

Run: `grep -nE 'as IList<string>|as ICollection<string>' src/ServiceConnect/Bus.cs`
Expected: no hits (or any hit is verifiably live).

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.Interfaces/Bus/IBus.cs src/ServiceConnect/Bus.cs
# Plus any test file changes

git commit -m "$(cat <<'EOF'
refactor(interfaces)!: IBus.RouteAsync destinations is IReadOnlyList

IBus.RouteAsync(T, IList<string>, CT) -> RouteAsync(T, IReadOnlyList<string>, CT).

Routing-slip destinations are read-only by intent — the framework
iterates the list and joins entries 1..N into the routing-slip
header. Read-only typing prevents a caller's concurrent mutation
of a shared list while the framework iterates.

BREAKING CHANGE: callers must pass IReadOnlyList<string>-compatible
destinations. List<string> still works via implicit conversion.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: Drop `SendOptions.EndPoints`, add `IBus.SendToManyAsync`, delete the runtime mutex guard

Atomic commit: removes `SendOptions.EndPoints`, adds the new fan-out method, deletes the now-unreachable runtime guard, migrates internal callers, migrates tests.

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Options/SendOptions.cs` — drop `EndPoints` property
- Modify: `src/ServiceConnect.Interfaces/Bus/IBus.cs` — add `SendToManyAsync` method
- Modify: `src/ServiceConnect/Bus.cs` — implement `SendToManyAsync`; simplify `SendAsync` (drop the `EndPoints` branch + the mutex guard)
- Modify: tests that constructed `new SendOptions { EndPoints = ... }` → migrate to `IBus.SendToManyAsync(...)`
- Examples: any usage of `SendOptions.EndPoints` (rare; spot-check)

- [ ] **Step 1: Inventory `EndPoints` usage**

Run: `grep -rn 'SendOptions.*EndPoints\|new SendOptions { EndPoints' src/ examples/ --include='*.cs' | grep -v 'obj/\|bin/' | head -30`
Note all sites. This is the `Send`-side; `RequestOptions.EndPoints` is handled in Task 7.

- [ ] **Step 2: Drop `SendOptions.EndPoints`**

Edit `src/ServiceConnect.Interfaces/Options/SendOptions.cs`:

```csharp
namespace ServiceConnect.Interfaces.Options;

public readonly record struct SendOptions
{
    /// <summary>
    /// Gets the additional headers to attach to the message. Typed as a read-only
    /// view so the framework does not invite concurrent-caller mutation of a
    /// shared dictionary while the async pipeline is iterating it.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Gets the destination endpoint. Use <see cref="IBus.SendToManyAsync"/> for fan-out
    /// to multiple endpoints.
    /// </summary>
    public string? EndPoint { get; init; }
}
```

- [ ] **Step 3: Add `SendToManyAsync` to `IBus`**

Edit `src/ServiceConnect.Interfaces/Bus/IBus.cs`. Add a new method (place it next to `SendAsync` for discoverability, around line 19):

```csharp
/// <summary>
/// Sends a message to each of the specified endpoints. Each delivery is dispatched as a
/// separate <see cref="SendAsync{T}"/>-equivalent call; failures on one endpoint do not
/// abort the others.
/// </summary>
Task SendToManyAsync<T>(T message, IReadOnlyList<string> endPoints, SendOptions? options = null, CancellationToken cancellationToken = default) where T : Message;
```

The `options.EndPoint` is ignored when this method is called — the explicit `endPoints` parameter wins. Document this in the XML doc.

- [ ] **Step 4: Implement `SendToManyAsync` in `Bus.cs`**

In `src/ServiceConnect/Bus.cs`, add a new method below `SendAsync`. Lift the multi-endpoint loop from current `SendAsync` (lines 171–187) into `SendToManyAsync`:

```csharp
public async Task SendToManyAsync<T>(T message, IReadOnlyList<string> endPoints, SendOptions? options = null, CancellationToken cancellationToken = default) where T : Message
{
    ThrowIfDisposed();
    cancellationToken.ThrowIfCancellationRequested();
    ArgumentNullException.ThrowIfNull(endPoints);
    if (endPoints.Count == 0)
    {
        throw new ArgumentException("SendToManyAsync requires at least one endpoint.", nameof(endPoints));
    }

    var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>();
    _serializer.Serialize(message, bufferWriter);
    var messageBytes = bufferWriter.WrittenMemory;
    Dictionary<string, string> headers;

    if (_hasOutgoingFilters)
    {
        var envelope = CreateEnvelope(messageBytes, message.CorrelationId, options?.Headers);
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

    foreach (var endpoint in endPoints)
    {
        var context = new SendContext
        {
            Message = message,
            MessageType = typeof(T),
            MessageBytes = messageBytes,
            Headers = headers,
            EndPoint = endpoint,
            RoutingKey = null,
            Operation = SendOperation.Send,
        };
        await _sendPipeline.ExecuteSendMessagePipelineAsync(context, cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 5: Simplify `Bus.SendAsync`**

Edit the existing `SendAsync` in `src/ServiceConnect/Bus.cs`. Delete:
- The `if (options is { EndPoint.Length: > 0, EndPoints.Count: > 0 })` mutex guard (lines 144–149).
- The `if (options?.EndPoints is { Count: > 0 } endpoints)` multi-endpoint branch (lines 171–187).

The remaining body sends to a single endpoint (or the configured-queue mapping if `options?.EndPoint` is null):

```csharp
public async Task SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default) where T : Message
{
    ThrowIfDisposed();
    cancellationToken.ThrowIfCancellationRequested();

    var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>();
    _serializer.Serialize(message, bufferWriter);
    var messageBytes = bufferWriter.WrittenMemory;
    Dictionary<string, string> headers;

    if (_hasOutgoingFilters)
    {
        var envelope = CreateEnvelope(messageBytes, message.CorrelationId, options?.Headers);
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

    var context = new SendContext
    {
        Message = message,
        MessageType = typeof(T),
        MessageBytes = messageBytes,
        Headers = headers,
        EndPoint = options?.EndPoint,
        RoutingKey = null,
        Operation = SendOperation.Send,
    };
    await _sendPipeline.ExecuteSendMessagePipelineAsync(context, cancellationToken).ConfigureAwait(false);
}
```

- [ ] **Step 6: Update test fixtures**

For every test using `new SendOptions { EndPoints = ["a", "b"] }`:

```csharp
// Was:
await bus.SendAsync(message, new SendOptions { EndPoints = ["a", "b"] });

// Now:
await bus.SendToManyAsync(message, ["a", "b"]);
```

Find sites:
```bash
grep -rn 'SendOptions.*EndPoints' src/ServiceConnect.UnitTests/ src/ServiceConnect.EndToEndTests/ --include='*.cs' | grep -v 'obj/\|bin/'
```

Update each. Add a test asserting `SendToManyAsync` actually fans out (sends one envelope per endpoint) — the new method needs at least one direct unit test per the A.2 final review's lesson.

Suggested test in a fitting location (e.g., `src/ServiceConnect.UnitTests/BusTests.cs` or a new `BusSendToManyTests.cs`):

```csharp
[Fact]
public async Task SendToManyAsync_FansOut_ToEachEndpoint()
{
    // Arrange
    var pipelineCalls = new List<string?>();
    var pipeline = new Mock<ISendMessagePipeline>();
    pipeline
        .Setup(p => p.ExecuteSendMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
        .Callback<SendContext, CancellationToken>((ctx, _) => pipelineCalls.Add(ctx.EndPoint))
        .Returns(Task.CompletedTask);
    // ... build Bus with this pipeline (use existing test scaffolding pattern)

    // Act
    await bus.SendToManyAsync(new FakeMessage1(Guid.NewGuid()), ["queue-a", "queue-b", "queue-c"]);

    // Assert
    Assert.Equal(new[] { "queue-a", "queue-b", "queue-c" }, pipelineCalls);
}

[Fact]
public async Task SendToManyAsync_EmptyEndpointList_Throws()
{
    var bus = /* build bus */;
    var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
        bus.SendToManyAsync(new FakeMessage1(Guid.NewGuid()), []));
    Assert.Contains("at least one endpoint", ex.Message);
}

[Fact]
public async Task SendToManyAsync_NullEndpointList_ThrowsArgumentNull()
{
    var bus = /* build bus */;
    await Assert.ThrowsAsync<ArgumentNullException>(() =>
        bus.SendToManyAsync(new FakeMessage1(Guid.NewGuid()), null!));
}
```

Adapt the test scaffolding to whatever existing pattern `BusTests.cs` uses for arranging `Bus` with mocked pipeline.

- [ ] **Step 7: Spot-check examples**

Run: `grep -rn 'SendOptions.*EndPoints' examples/ --include='*.cs' | grep -v 'obj/\|bin/'`
Update any hits to `SendToManyAsync`. Document in the example's README if behaviour changes (unlikely — `SendOptions.EndPoints` was a niche API).

- [ ] **Step 8: Full solution build**

Run: `dotnet build src/ServiceConnect.slnx -m:1`
Expected: 0 errors, 0 warnings.

- [ ] **Step 9: Run unit tests + SerializationCompatTests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-build --no-restore`
Expected: all tests pass (existing + 3 new SendToManyAsync tests).

Run: `dotnet test src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj --no-build --no-restore`
Expected: all pass.

- [ ] **Step 10: Variance audit**

Run: `grep -nE 'as IList<|as IReadOnlyList<' src/ServiceConnect/Bus.cs`
Expected: no hits, or any hit is verifiably live under the new types.

- [ ] **Step 11: Commit**

```bash
git add \
  src/ServiceConnect.Interfaces/Options/SendOptions.cs \
  src/ServiceConnect.Interfaces/Bus/IBus.cs \
  src/ServiceConnect/Bus.cs \
  src/ServiceConnect.UnitTests/

# Plus any examples files updated
git add examples/ 2>/dev/null || true

git commit -m "$(cat <<'EOF'
refactor(interfaces)!: drop SendOptions.EndPoints, add IBus.SendToManyAsync

Single-endpoint sends and multi-endpoint fan-out are now distinct
operations, expressed by distinct methods rather than by a runtime
mutex on a single options struct.

- SendOptions.EndPoints removed. Single-endpoint sends use
  SendOptions.EndPoint as before (string?).
- IBus.SendToManyAsync(T, IReadOnlyList<string>, SendOptions?, CT)
  added. Iterates the endpoints and dispatches each delivery as a
  separate SendContext through the same pipeline.
- Bus.SendAsync's runtime guard ("EndPoint and EndPoints cannot both
  be set") deletes — the trap is no longer reachable.

BREAKING CHANGE: callers using
  new SendOptions { EndPoints = [a, b] }
must migrate to
  bus.SendToManyAsync(msg, [a, b]).
The default (null EndPoint) single-endpoint behaviour is unchanged.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: Drop `RequestOptions.EndPoints`; clean up `ExpectedReplyCount` doc

`RequestOptions.EndPoints` is dropped without a `RequestToManyAsync` replacement. Per the brainstorm, scatter-gather request/reply doesn't have natural semantics (which reply wins? do we await all? timeout per endpoint or total?). Callers wanting fan-out request/reply use `IBus.PublishRequestAsync` (broadcast) and correlate replies manually.

`ExpectedReplyCount`'s "fall back to `EndPoints`.Count" clause becomes unreachable; update the XML doc.

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Options/RequestOptions.cs`
- Modify: `src/ServiceConnect/Services/RequestReplyManager.cs` — drops `EndPoints`-fan-out branches
- Modify: tests using `new RequestOptions { EndPoints = ... }`
- Examples: `examples/ScatterGather/` is the most likely consumer

- [ ] **Step 1: Inventory RequestOptions.EndPoints usage**

Run:
```bash
grep -rn 'RequestOptions.*EndPoints\|new RequestOptions { EndPoints' src/ examples/ --include='*.cs' | grep -v 'obj/\|bin/' | head -30
```

- [ ] **Step 2: Drop `EndPoints` from `RequestOptions`**

Edit `src/ServiceConnect.Interfaces/Options/RequestOptions.cs`:

```csharp
using System.Collections.Generic;

namespace ServiceConnect.Interfaces.Options;

public readonly record struct RequestOptions
{
    public const int DefaultTimeoutMs = 10_000;

    public RequestOptions()
    {
        Timeout = DefaultTimeoutMs;
    }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Single-destination override for the request.</summary>
    public string? EndPoint { get; init; }

    public int Timeout { get; init; }

    /// <summary>
    /// Expected reply count.
    /// <list type="bullet">
    /// <item><description>
    /// <b>Positive value</b> — the call completes as soon as that many replies have arrived,
    /// or when <see cref="Timeout"/> elapses (whichever happens first).
    /// </description></item>
    /// <item><description>
    /// <b>Zero, negative, or null (default)</b> — the call always waits the full
    /// <see cref="Timeout"/> and returns every reply received during the window.
    /// </description></item>
    /// </list>
    /// </summary>
    public int? ExpectedReplyCount { get; init; }

    public static RequestOptions Default => new();
}
```

The XML doc on `ExpectedReplyCount` no longer mentions `EndPoints`.

- [ ] **Step 3: Update `RequestReplyManager`**

Read `src/ServiceConnect/Services/RequestReplyManager.cs`. Find every place that reads `options?.EndPoints` or branches on `EndPoints.Count`. The fan-out logic that delivered to multiple endpoints is gone; the single-endpoint path (`options?.EndPoint`) is the only path.

Specifically:
- `SendRequestAsync<TRequest, TReply>` already used a single-endpoint pattern (the `EndPoints` branch was effectively unused in this method); confirm no edit is needed.
- `SendRequestMultiAsync<TRequest, TReply>` may have used `EndPoints.Count` to set `ExpectedReplyCount` defaults. Update so:
  - If `options?.ExpectedReplyCount` is positive, use it.
  - Otherwise, wait for the full Timeout and return every reply received.
- `PublishRequestAsync<TRequest, TReply>` may have similar logic. Same update.

Read the file, identify the affected branches, and remove the `EndPoints` references.

- [ ] **Step 4: Update tests**

```bash
grep -rn 'RequestOptions.*EndPoints' src/ServiceConnect.UnitTests/ src/ServiceConnect.EndToEndTests/ --include='*.cs' | grep -v 'obj/\|bin/'
```

For tests that constructed `RequestOptions { EndPoints = ... }` for fan-out request/reply: those scenarios are no longer expressible. Either:
- Migrate to `IBus.PublishRequestAsync` (the natural fan-out request/reply pattern) and adjust the test to assert publish-style behaviour, OR
- If the test was specifically asserting the old multi-endpoint Send behaviour, delete the test (it's testing a scenario that no longer exists). Document why in the deletion commit message body.

- [ ] **Step 5: Update examples**

`examples/ScatterGather/` is the most likely consumer. Spot-check by:

```bash
grep -rn 'RequestOptions.*EndPoints\|SendRequestMulti' examples/ScatterGather/ --include='*.cs'
```

If the example uses `RequestOptions.EndPoints` for fan-out, migrate it to `PublishRequestAsync`. Update the example's README to explain the new pattern.

- [ ] **Step 6: Full solution build**

Run: `dotnet build src/ServiceConnect.slnx -m:1`
Expected: 0 errors, 0 warnings.

- [ ] **Step 7: Run unit tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-build --no-restore`
Expected: all tests pass.

- [ ] **Step 8: Variance audit**

Run: `grep -nE 'as IList<|as IReadOnlyList<' src/ServiceConnect/Services/RequestReplyManager.cs`
Expected: no hits.

- [ ] **Step 9: Commit**

```bash
git add \
  src/ServiceConnect.Interfaces/Options/RequestOptions.cs \
  src/ServiceConnect/Services/RequestReplyManager.cs \
  src/ServiceConnect.UnitTests/

# Plus example files if changed
git add examples/ 2>/dev/null || true

git commit -m "$(cat <<'EOF'
refactor(interfaces)!: drop RequestOptions.EndPoints; clarify ExpectedReplyCount

RequestOptions no longer carries an EndPoints fan-out field. Multi-
recipient request/reply was always semantically awkward: which reply
counts as "the" reply for SendRequestAsync? Should the timeout cover
the slowest endpoint or each endpoint independently? The pattern
that's actually wanted here is PublishRequestAsync (broadcast +
correlate replies) — RequestOptions.EndPoints conflated the two.

ExpectedReplyCount loses its "fall back to EndPoints.Count" branch;
its semantics are now:
  - Positive value: complete on N replies or Timeout, whichever first.
  - Zero/negative/null: always wait the full Timeout, return every reply.

RequestReplyManager's multi-endpoint code paths are simplified
accordingly.

BREAKING CHANGE: callers using
  new RequestOptions { EndPoints = [...] }
must migrate to IBus.PublishRequestAsync for broadcast request/reply.
Single-endpoint requests (RequestOptions.EndPoint) are unchanged.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: Streaming inbound cascade — `IMessageBusReadStream.Write` takes `ReadOnlyMemory<byte>`

API consistency change. Internal storage stays `byte[]`-keyed (RabbitMQ.Client v7 doesn't extend buffer lifetime past the callback, so retention requires copying — `data.ToArray()` inside Write replaces the previous `data` reference store).

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Streaming/IMessageBusReadStream.cs`
- Modify: `src/ServiceConnect/Services/MessageBusReadStream.cs`
- Modify: `src/ServiceConnect/Services/Processors/StreamProcessor.cs:158`
- Modify: `src/ServiceConnect.UnitTests/MessageBusReadStreamTests.cs` (test call sites use collection literals — most need no edit because `byte[]` implicitly converts to `ROM<byte>`)

- [ ] **Step 1: Update the interface**

Edit `src/ServiceConnect.Interfaces/Streaming/IMessageBusReadStream.cs:13`:

```csharp
// Before:
void Write(byte[] data, long packetNumber);

// After:
void Write(ReadOnlyMemory<byte> data, long packetNumber);
```

Update the XML doc above:

```csharp
/// <summary>
/// Writes a packet into the stream buffer.
/// </summary>
/// <param name="data">The packet payload. The implementation copies the bytes for retention;
/// the caller's buffer can be reused after the call returns.</param>
/// <param name="packetNumber">The zero-based packet number.</param>
```

- [ ] **Step 2: Update the impl**

Edit `src/ServiceConnect/Services/MessageBusReadStream.cs`. Find `public void Write(byte[] data, long packetNumber)`. Update the signature and the body's defensive `data.Length` and `_packets.TryAdd(packetNumber, data)` to use `data` (now `ROM<byte>`) properly:

```csharp
/// <inheritdoc />
public void Write(ReadOnlyMemory<byte> data, long packetNumber)
{
    if (packetNumber < 0)
    {
        throw new ArgumentOutOfRangeException(nameof(packetNumber), packetNumber,
            "Packet number must be non-negative.");
    }

    var preLast = Volatile.Read(ref _lastPacketNumber);
    if (preLast >= 0 && packetNumber > preLast)
    {
        throw new ArgumentOutOfRangeException(nameof(packetNumber), packetNumber,
            $"Packet number {packetNumber} exceeds LastPacketNumber {preLast} for stream {SequenceId}.");
    }

    long newTotal = Interlocked.Add(ref _totalBytesWritten, data.Length);
    if (newTotal > MaxTotalStreamSize)
    {
        Interlocked.Add(ref _totalBytesWritten, -data.Length);
        throw new InvalidOperationException($"Stream exceeds maximum size of {MaxTotalStreamSize / (1024 * 1024)} MB.");
    }

    // RabbitMQ.Client v7 does not extend the buffer lifetime past the consumer
    // callback. Copy into a retained byte[] so reads after the stream completes
    // see the correct bytes. The ROM<byte> parameter is the v8 API contract;
    // the byte[] storage is an internal implementation detail.
    var stored = data.ToArray();

    if (!_packets.TryAdd(packetNumber, stored))
    {
        Interlocked.Add(ref _totalBytesWritten, -data.Length);
        return;
    }

    var postLast = Volatile.Read(ref _lastPacketNumber);
    if (postLast >= 0 && packetNumber > postLast)
    {
        _packets.TryRemove(packetNumber, out _);
        Interlocked.Add(ref _totalBytesWritten, -data.Length);
        throw new ArgumentOutOfRangeException(nameof(packetNumber), packetNumber,
            $"Packet number {packetNumber} exceeds LastPacketNumber {postLast} (set concurrently) for stream {SequenceId}.");
    }

    Interlocked.Increment(ref _receivedCount);
}
```

The `ArgumentNullException.ThrowIfNull(data)` line is gone — `ReadOnlyMemory<byte>` is a value type and cannot be null (only `default` / `Empty`).

- [ ] **Step 3: Update `StreamProcessor.cs`**

Edit `src/ServiceConnect/Services/Processors/StreamProcessor.cs:158`:

```csharp
// Before:
state.Stream.Write(messageBytes.ToArray(), packetNumber);

// After:
state.Stream.Write(messageBytes, packetNumber);
```

The `ToArray()` is gone — the API now accepts `ROM<byte>` directly. (Internal storage still copies via `Write`'s impl, so the allocation hasn't moved — see the Task description.)

- [ ] **Step 4: Update tests**

Most test call sites use collection literals (`stream.Write([1, 2], 0)`); `byte[]` implicitly converts to `ROM<byte>`, so they continue to compile.

The one test asserting null-rejection (`Assert.Throws<ArgumentNullException>(() => stream.Write(null!, 0))`) deletes — `ROM<byte>` cannot be null. Find via:

```bash
grep -n 'stream.Write(null' src/ServiceConnect.UnitTests/MessageBusReadStreamTests.cs
```

Delete that test case if found. Document why in the commit body.

- [ ] **Step 5: Full solution build**

Run: `dotnet build src/ServiceConnect.slnx -m:1`
Expected: 0 errors, 0 warnings.

- [ ] **Step 6: Run unit tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-build --no-restore`
Expected: all tests pass (minus the one null-rejection test that was deleted, if applicable).

- [ ] **Step 7: Commit**

```bash
git add \
  src/ServiceConnect.Interfaces/Streaming/IMessageBusReadStream.cs \
  src/ServiceConnect/Services/MessageBusReadStream.cs \
  src/ServiceConnect/Services/Processors/StreamProcessor.cs \
  src/ServiceConnect.UnitTests/MessageBusReadStreamTests.cs

git commit -m "$(cat <<'EOF'
refactor(interfaces)!: IMessageBusReadStream.Write takes ReadOnlyMemory<byte>

API consistency with the rest of the v8 surface (Phase A.2 retyped
IProducer body params, SendContext.MessageBytes, Envelope.Body to
ROM<byte>). The streaming receive API was the last byte[] holdout
on the inbound path.

Internal storage stays byte[] — RabbitMQ.Client v7 doesn't extend
the consumer-callback buffer lifetime, so retention requires a copy
regardless of the API surface. Write internally calls data.ToArray()
to capture the bytes for later read-out. The allocation hasn't moved;
StreamProcessor's caller-side ToArray() is replaced by an impl-side
ToArray(). Net change is API consistency, not perf.

BREAKING CHANGE: callers passing null to Write are no longer
expressible (ROM<byte> is a value type). Default(ROM<byte>) /
ROM<byte>.Empty produces an empty packet. The null-rejection test
in MessageBusReadStreamTests is deleted.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: Streaming outbound cascade — `IMessageBusWriteStream.WriteAsync` takes `ReadOnlyMemory<byte>`

Drops `int offset, int count`; callers slice via `buffer.AsMemory(offset, count)`. The impl threads ROM<byte> directly to `IProducer.SendBytesAsync(ROM<byte>)` — genuine zero-copy on the outbound stream path.

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Streaming/IMessageBusWriteStream.cs`
- Modify: `src/ServiceConnect/Services/MessageBusWriteStream.cs`
- Modify: callers of `WriteAsync(buf, off, cnt, ct)` — find via grep.
- Modify: tests using the old signature.

- [ ] **Step 1: Inventory call sites**

Run: `grep -rn 'WriteAsync.*offset\|IMessageBusWriteStream' src/ examples/ --include='*.cs' | grep -v 'obj/\|bin/' | head -30`

The impl is in `MessageBusWriteStream.cs`. User callers go through `IBus.CreateStream<T>` which returns `IMessageBusWriteStream`.

- [ ] **Step 2: Update the interface**

Edit `src/ServiceConnect.Interfaces/Streaming/IMessageBusWriteStream.cs`:

```csharp
namespace ServiceConnect.Interfaces;

public interface IMessageBusWriteStream : IAsyncDisposable
{
    /// <summary>
    /// Writes the supplied buffer to the stream as one or more transport packets.
    /// </summary>
    /// <param name="buffer">The bytes to write. The buffer is read once and the underlying
    /// memory is not retained past the call completion; the caller can reuse the buffer.</param>
    /// <param name="cancellationToken">Token that cancels the write before it is dispatched
    /// to the producer.</param>
    Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Flushes any remaining data and marks the stream as complete.
    /// </summary>
    /// <param name="cancellationToken">Token that aborts the in-flight drain wait and the close send.</param>
    Task CloseAsync(CancellationToken cancellationToken = default);
}
```

- [ ] **Step 3: Update the impl**

Read `src/ServiceConnect/Services/MessageBusWriteStream.cs` and find the existing `WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)` method.

Update the signature. The body changes:
- Replace `buffer.AsSpan(offset, count)` or `new byte[count]` allocations with the `buffer` ROM<byte> directly.
- The producer call already takes ROM<byte> via `IProducer.SendBytesAsync(string, Type, ReadOnlyMemory<byte>, ...)` after Phase A.2.
- Any internal partitioning into packets stays the same — slice the input ROM<byte> as needed.

Read the file's current body and adapt the bookkeeping carefully. The behaviour (split into packets of `MaxPacketSize`, dispatch each via `_producer.SendBytesAsync(...)`, increment `_packetNumber`) is unchanged; only the input shape changes.

- [ ] **Step 4: Update callers**

Run: `grep -rn 'WriteAsync.*[a-z], [0-9]\+, [a-z]' src/ examples/ --include='*.cs' | grep -v 'obj/\|bin/'`
For every site like `await stream.WriteAsync(buffer, offset, count, ct)`, migrate to `await stream.WriteAsync(buffer.AsMemory(offset, count), ct)`.

For sites like `await stream.WriteAsync(buffer, 0, buffer.Length, ct)`, migrate to `await stream.WriteAsync(buffer, ct)` (the implicit `byte[]` → `ROM<byte>` covers the full-buffer case).

- [ ] **Step 5: Update tests**

```bash
grep -rn 'WriteAsync' src/ServiceConnect.UnitTests/MessageBusWriteStream src/ServiceConnect.EndToEndTests/Streaming/ --include='*.cs' 2>/dev/null | grep -v 'obj/\|bin/'
```

Apply the same migration.

- [ ] **Step 6: Update examples**

```bash
grep -rn 'IMessageBusWriteStream\|CreateStream' examples/ --include='*.cs' | grep -v 'obj/\|bin/'
```

`examples/Streaming/` is the most likely consumer. Update its WriteAsync call sites and its README.

- [ ] **Step 7: Full solution build**

Run: `dotnet build src/ServiceConnect.slnx -m:1`
Expected: 0 errors, 0 warnings.

- [ ] **Step 8: Run unit tests + E2E build**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-build --no-restore`
Expected: all tests pass.

Run: `dotnet build src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj -m:1`
Expected: 0 errors, 0 warnings.

- [ ] **Step 9: Commit**

```bash
git add \
  src/ServiceConnect.Interfaces/Streaming/IMessageBusWriteStream.cs \
  src/ServiceConnect/Services/MessageBusWriteStream.cs \
  src/ServiceConnect.UnitTests/

# Plus example files
git add examples/ 2>/dev/null || true

git commit -m "$(cat <<'EOF'
refactor(interfaces)!: IMessageBusWriteStream.WriteAsync takes ReadOnlyMemory<byte>

WriteAsync(byte[], int offset, int count, CT) -> WriteAsync(ROM<byte>, CT).
Drops the offset/count parameters; callers slice via
buffer.AsMemory(offset, count). The impl threads ROM<byte> directly
to IProducer.SendBytesAsync (already ROM<byte>-typed in Phase A.2),
so the outbound stream path is now genuinely zero-copy: no internal
buffer copy between the user write and the producer publish.

Migration is mechanical:
  await stream.WriteAsync(buffer, 0, buffer.Length, ct)
becomes
  await stream.WriteAsync(buffer, ct)
and
  await stream.WriteAsync(buffer, off, cnt, ct)
becomes
  await stream.WriteAsync(buffer.AsMemory(off, cnt), ct).

BREAKING CHANGE: callers using the byte[]/offset/count signature must
migrate. The full-buffer case is one-line change at every call site.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 10: Examples migration

Per A.2 final review: examples land last. Spot-check every example for usage of changed APIs.

**Files:**
- All directories under `examples/`

- [ ] **Step 1: Find every example using a changed API**

```bash
grep -rln 'EndPoints\|ReplyAsync\|IMessageBusReadStream\|IMessageBusWriteStream\|StartConsumingAsync\|DispatchAsync\|RouteAsync.*IList' examples/ --include='*.cs' 2>/dev/null
```

For each file listed, inspect and update according to the migration patterns from Tasks 3-9.

- [ ] **Step 2: Update each example**

Per file, apply the migrations:
- `RequestOptions.EndPoints` → `PublishRequestAsync` or single `EndPoint` (Task 7)
- `SendOptions.EndPoints` → `SendToManyAsync` (Task 6)
- `ReplyAsync(msg, dict, ct)` → `ReplyAsync(msg, new ReplyOptions { Headers = dict }, ct)` (Task 3)
- `WriteAsync(buf, 0, buf.Length, ct)` → `WriteAsync(buf, ct)` (Task 9)
- `Write(byte[], long)` (read-stream) — typically callers pass byte[] directly; implicit conversion handles the migration (Task 8)

- [ ] **Step 3: Build each example solution**

Each example has its own .sln. Build each:

```bash
for slnfile in examples/*/[A-Za-z]*.sln; do
    echo "=== $slnfile ==="
    dotnet build "$slnfile" -m:1 2>&1 | tail -3
done
```

Expected: 0 errors, 0 warnings for every example.

If any fails, read the error and apply the appropriate migration pattern.

- [ ] **Step 4: Update example READMEs**

Each example with code changes needs a README touch documenting the v8 migration. For most examples, no code changed — skip the README edit.

For changed examples, append a short v8 migration note at the bottom of the README, e.g.:

```markdown
## v8 migration note

This example was updated for the v8 API:
- `SendOptions.EndPoints` is replaced by `IBus.SendToManyAsync(...)`. See
  the source for the migration.
```

- [ ] **Step 5: Commit**

```bash
git add examples/

git commit -m "$(cat <<'EOF'
refactor(examples)!: migrate examples to v8 API surface

Updates every example that referenced a changed public API:
- SendOptions.EndPoints / RequestOptions.EndPoints removed
- IBus.SendToManyAsync added
- IConsumeContext.ReplyAsync uses ReplyOptions
- IMessageBusReadStream.Write takes ROM<byte>
- IMessageBusWriteStream.WriteAsync takes ROM<byte> (drops offset/count)

Each affected example's README has a short migration note. Examples
that didn't reference any changed API are untouched.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 11: Consolidate v8 release notes

The v8 release-notes section in `website/src/content/docs/releases.mdx` was drafted in Phase A.2's follow-up commit `28fc687d` and covers A.1 + A.2 changes. Extend it with A.3 changes for a complete A-group v8 migration story.

**Files:**
- Modify: `website/src/content/docs/releases.mdx`

- [ ] **Step 1: Extend the v8 highlights section**

Open `website/src/content/docs/releases.mdx`. Find the "## v8 highlights" section (added in `28fc687d`).

Add new subsections for the A.3 breaking changes, after the existing "Internal — `SendContext.MessageBytes` is `ReadOnlyMemory<byte>`" subsection:

```markdown
### Breaking — `IConsumeContext.ReplyAsync` uses `ReplyOptions`

```csharp
// Before:
Task ReplyAsync<TReply>(TReply message, IDictionary<string,string>? headers = null, CancellationToken ct = default);

// After:
Task ReplyAsync<TReply>(TReply message, ReplyOptions? options = null, CancellationToken ct = default);
```

Mirrors the `PublishOptions`/`SendOptions` shape. Migration:

```csharp
// Was:
await ctx.ReplyAsync(reply, new Dictionary<string,string> { ["X-Trace"] = "abc" });

// Now:
await ctx.ReplyAsync(reply, new ReplyOptions { Headers = new Dictionary<string,string> { ["X-Trace"] = "abc" } });
```

### Breaking — `IConsumer.StartConsumingAsync` and `IMessageDispatcher.DispatchAsync` use `IReadOnly*`

```csharp
// IConsumer.StartConsumingAsync messageTypes:
//   IList<string> -> IReadOnlyList<string>
// IMessageDispatcher.DispatchAsync headers:
//   IDictionary<string, object> -> IReadOnlyDictionary<string, object>
```

`List<string>` and `Dictionary<string, object>` continue to compile as arguments via implicit conversion. Mock setups using `It.IsAny<IList<string>>()` / `It.IsAny<IDictionary<string,object>>()` must update to the readonly variants.

### Breaking — `IBus.RouteAsync` destinations is `IReadOnlyList<string>`

`IBus.RouteAsync(T, IList<string>, CT)` becomes `RouteAsync(T, IReadOnlyList<string>, CT)`. `List<string>` arguments continue to work via implicit conversion.

### Breaking — `SendOptions.EndPoints` and `RequestOptions.EndPoints` removed; `IBus.SendToManyAsync` added

`SendOptions` and `RequestOptions` no longer carry an `EndPoints` field. Migration:

| Was | Now |
|---|---|
| `bus.SendAsync(msg, new SendOptions { EndPoints = ["a", "b"] })` | `bus.SendToManyAsync(msg, ["a", "b"])` |
| `bus.SendRequestAsync(msg, new RequestOptions { EndPoints = ["a", "b"] })` | `bus.PublishRequestAsync(msg, onReply, options)` (broadcast pattern) |

The previous runtime guard preventing both `EndPoint` and `EndPoints` from being set is gone — the trap is no longer reachable.

`RequestOptions.ExpectedReplyCount`'s "fall back to `EndPoints.Count`" clause is gone; it now defaults to "wait the full Timeout, return every reply" when null/zero/negative.

### Breaking — Streaming body-type cascade

`IMessageBusReadStream.Write` and `IMessageBusWriteStream.WriteAsync` align with the v8 ROM<byte>-typed surface:

```csharp
// IMessageBusReadStream.Write:
//   void Write(byte[] data, long packetNumber)
//   -> void Write(ReadOnlyMemory<byte> data, long packetNumber)

// IMessageBusWriteStream.WriteAsync:
//   Task WriteAsync(byte[] buffer, int offset, int count, CT ct)
//   -> Task WriteAsync(ReadOnlyMemory<byte> buffer, CT ct)
```

`IMessageBusWriteStream.WriteAsync` migration is mechanical:
- `WriteAsync(buf, 0, buf.Length, ct)` → `WriteAsync(buf, ct)` (implicit `byte[]` → `ROM<byte>`)
- `WriteAsync(buf, off, cnt, ct)` → `WriteAsync(buf.AsMemory(off, cnt), ct)`

The outbound write path is now genuinely zero-copy (no internal buffer copy between user write and producer publish). The inbound read path retains its internal copy because RabbitMQ.Client v7 does not extend buffer lifetimes past the consumer callback.
```

- [ ] **Step 2: Commit**

```bash
git add website/src/content/docs/releases.mdx
git commit -m "$(cat <<'EOF'
docs(website): extend v8 release notes with Phase A.3 breaking changes

Adds five new subsections to the v8 highlights:
- IConsumeContext.ReplyAsync uses ReplyOptions
- IConsumer + IMessageDispatcher IReadOnly* tightening
- IBus.RouteAsync destinations IReadOnlyList<string>
- SendOptions.EndPoints / RequestOptions.EndPoints removed +
  IBus.SendToManyAsync added; ExpectedReplyCount semantics updated
- IMessageBusReadStream.Write / IMessageBusWriteStream.WriteAsync
  body-type cascade

The v8 highlights section now covers the complete Group A migration
story (Phases A.1 + A.2 + A.3) — a single document a v7 user can read
to plan their upgrade.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 12: Final phase verification + Group A close

- [ ] **Step 1: Full solution build**

Run: `dotnet build src/ServiceConnect.slnx -m:1`
Expected: 0 errors, 0 warnings.

- [ ] **Step 2: Full unit-test sweep**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-build --no-restore`
Expected: all tests pass. Net unit-test count vs end-of-A.2 should be roughly equal — minus a handful for deleted scenarios (null-body in Write, EndPoints fan-out tests), plus the new SendToManyAsync tests.

- [ ] **Step 3: SerializationCompatTests sweep**

Run: `dotnet test src/ServiceConnect.SerializationCompatTests/ServiceConnect.SerializationCompatTests.csproj --no-build --no-restore`
Expected: 48/48 (unchanged from end-of-A.2 — A.3 doesn't touch the serializer).

- [ ] **Step 4: EndToEndTests build**

Run: `dotnet build src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj -m:1`
Expected: 0 errors, 0 warnings.

- [ ] **Step 5: Verify every example builds**

```bash
for slnfile in examples/*/[A-Za-z]*.sln; do
    echo "=== $slnfile ==="
    dotnet build "$slnfile" -m:1 2>&1 | tail -1
done
```

Expected: every example "Build succeeded" with 0 errors.

- [ ] **Step 6: Update phase status in the roadmap**

Edit `architecture-fix-plan.md`. Find:
```
## Group A — v8 public-API tightening · *Phase A.2 done; A.3 pending*
```

Change to:
```
## Group A — v8 public-API tightening · *done*
```

(Group A is fully shipped at this point — the entire v8 public-API tightening across all three phases.)

Commit:

```bash
git add architecture-fix-plan.md
git commit -m "$(cat <<'EOF'
docs(architecture): mark Group A done in the fix plan

Phase A.3 of Group A (v8 public-API tightening) is complete — and with
it, the entire Group A:

- ReplyOptions struct + IConsumeContext.ReplyAsync shape change.
- IConsumer.StartConsumingAsync messageTypes -> IReadOnlyList<string>.
- IMessageDispatcher.DispatchAsync headers -> IReadOnlyDictionary<string,object>.
- IBus.RouteAsync destinations -> IReadOnlyList<string>.
- SendOptions.EndPoints / RequestOptions.EndPoints removed;
  IBus.SendToManyAsync added; runtime guard for both-set deletes;
  ExpectedReplyCount semantics simplified.
- IMessageBusReadStream.Write / IMessageBusWriteStream.WriteAsync
  body-type cascade aligning the streaming path with the rest of the
  ROM<byte>-typed v8 surface.
- Examples migrated.
- v8 release notes consolidated covering Phases A.1 + A.2 + A.3.

Group A is the v8 public-API tightening; it is complete. Groups B-G
(metrics, security defaults, resilience features, perf reductions,
extensibility, docs/contracts) remain queued per the roadmap.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Risks and rollback

- **`MessageDispatcher.DispatchAsync` headers cast at the public-boundary seam**: the `as IDictionary<string,object>` fast-path may fail if a future caller passes a `IReadOnlyDictionary<,>` runtime type that isn't `Dictionary<,>`. The fallback copy ensures correctness; the only risk is per-message allocation for that specific code path. Variance audit at Task 4 Step 8 catches this.
- **`RequestOptions.ExpectedReplyCount` semantics shift**: the "fall back to `EndPoints.Count`" branch is gone. Users who relied on `ExpectedReplyCount = null` to mean "wait for N replies where N is the endpoint count" need to set `ExpectedReplyCount = N` explicitly, or accept the new "wait full timeout" default. Documented in the v8 release notes (Task 11).
- **Streaming inbound zero-copy expectation mismatch**: the A.2 final review framed item 5 as a perf improvement; the realistic outcome is API consistency only on the inbound side. The plan and commit messages are explicit about this. Genuine zero-copy on the outbound side (Task 9) compensates.
- **Example breakage**: if an example uses an API in a way the migration patterns don't cover, the example's solution build fails at Task 10 Step 3. The engineer reads the failure and applies the correct migration; no rollback needed.
- **Rollback** is `git revert` of Tasks 2 through 12 in reverse order. Tasks 2-9 are interface changes; reverting any one in isolation requires reverting all of its consumers. For this reason, recommend rolling back the entire phase as one atomic operation if needed.

---

## Decisions banked from earlier brainstorm

For audit trail (these came out of Group A's brainstorm + A.2's final review):

- **ReplyOptions shape**: just `Headers`. No `EndPoint` (reply destination is implicit), no `RoutingKey` (replies don't fan out), no `CorrelationId` (auto-correlated).
- **`IBus.SendToManyAsync` shape**: takes `IReadOnlyList<string>` rather than `params string[]` (consistency with `RouteAsync`; explicit collection-literal at the call site).
- **`RequestOptions.EndPoints` removal**: scatter-gather request/reply has no natural single-call semantics. Use `IBus.PublishRequestAsync` for broadcast.
- **Streaming inbound storage**: stays `byte[]` because RabbitMQ.Client v7 doesn't extend buffer lifetimes past the callback. API surface change is consistency-only.
- **Streaming outbound zero-copy**: `WriteAsync(ROM<byte>, CT)` threads directly to `Producer.SendBytesAsync(ROM<byte>)` — genuine zero-copy on the outbound stream.
- **Bundling discipline**: Tasks 3 (ReplyOptions), 4 (Consumer + Dispatcher), 6 (SendOptions + SendToMany + mutex guard delete), 7 (RequestOptions + ExpectedReplyCount), 10 (examples) are each one atomic commit with internal consistency. Per the A.2 final review, examples land last to keep their diff out of surface-change reviews.
