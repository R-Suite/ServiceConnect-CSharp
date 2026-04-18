# Performance Remediation — 2026-04-18

Design for resolving every finding in `docs/performance-review-report-2026-04-15.md`. Fixes land on the current `improvements-and-fixes` branch in-place (no separate PR).

## Scope

All ten findings from the 2026-04-15 performance review. Each finding has been verified against the current source:

- Items 1–2 (High) — redundant byte-array copies on deserialize and stream assembly paths.
- Items 3–7 (Medium) — `Task.Run` over sync stream handler, per-rent wrapper allocation in `ConsumeContextPool`, O(n) in-memory timeout scan, three-round-trip MongoDB timeout poll, fixed-delay E2E polling.
- Items 8–10 (Low) — `ContainsKey`+assign pairs in RabbitMQ header stamping, linear propagator field scan in telemetry, startup type-name build.

## Non-Goals

- Switching serializers. Wire-format compatibility with older services on Newtonsoft.Json rules out replacing the serializer with `System.Text.Json`.
- Pooling packet buffers in `MessageBusReadStream`. Packet lifetimes extend past the RabbitMQ callback, and adopting `ArrayPool<byte>` introduces ownership/return complexity that is out of scope for an "obvious copies" pass.
- Broader consumer-concurrency redesign. Inline dispatch for stream handlers matches the existing non-cancellable branch; thread-pool offload for handlers in general is a separate decision.

## Design

### Cross-cutting: add `ReadOnlyMemory<byte>` to `IMessageSerializer`

`IMessageSerializer` currently exposes `byte[]` and `ReadOnlySpan<byte>` overloads. The span overload is unusable from asynchronous plumbing because spans cannot cross `await` points, and all hot-path callers hold `ReadOnlyMemory<byte>`. Add two new methods:

```csharp
T Deserialize<T>(ReadOnlyMemory<byte> data) where T : Message;
object Deserialize(ReadOnlyMemory<byte> data, Type type);
```

The existing `byte[]` and `ReadOnlySpan<byte>` overloads delegate to the memory overload. Implementation in `NewtonsoftJsonMessageSerializer`:

```csharp
public object Deserialize(ReadOnlyMemory<byte> data, Type type)
{
    using var ms = new ReadOnlyMemoryStream(data);
    using var sr = new StreamReader(ms, Encoding.UTF8);
    using var jr = new JsonTextReader(sr);
    ...
}
```

`ReadOnlyMemoryStream` is a small internal read-only `Stream` subclass backed by `ReadOnlyMemory<byte>`. No buffer copy is required — `StreamReader` consumes the stream byte-by-byte and the memory is pinned implicitly through the `Memory` handle.

**Resolves:** item 1 (no more `data.ToArray()` inside the serializer).

### Stream reassembly: hand the serializer a `ReadOnlySequence<byte>`

Add:

```csharp
object Deserialize(in ReadOnlySequence<byte> data, Type type);
```

Backed by a `ReadOnlySequenceStream` wrapper (standard pattern: walks segments on `Read`). Add a new method `ReadOnlySequence<byte> ReadSequence()` to `IMessageBusReadStream` and `MessageBusReadStream`. `ReadSequence` stitches the existing `_packets` byte arrays into a `ReadOnlySequenceSegment<byte>` linked list, in packet order — no copy.

**Preserve the existing `byte[] Read()` method** on the interface. Handler code consumes it directly (verified at `StreamingTests.cs:136`, `StreamOutOfOrderTests.cs:140`). Its implementation can be simplified to `return ReadSequence().ToArray();` but the copy still exists for handlers that request it. The dispatch path switches to `ReadSequence` so the deserialization copy goes away; handlers asking for raw bytes opt in to the copy.

`StreamProcessor.cs:152-155` becomes:

```csharp
var assembled = state.Stream.ReadSequence();
var originalMessage = _serializer.Deserialize(in assembled, resolvedType);
```

**Resolves:** item 2's assembly and serializer copies. The per-packet ingest copy at `StreamProcessor.cs:100` stays (non-goal above).

### Item 3 — inline sync stream handlers

Delete the `Task.Run` branch in `StreamProcessor.InvokeHandlerAsync`. Always invoke synchronously:

```csharp
private Task<ProcessResult> InvokeHandlerAsync(...)
{
    cancellationToken.ThrowIfCancellationRequested();
    descriptor.InvokeExecute(handler, originalMessage);
    return HandledTask;
}
```

This matches the existing non-cancellable branch and how regular message handlers are dispatched.

### Item 4 — drop the `ReadOnlyDictionary` wrapper in `ConsumeContextPool`

`PooledConsumeContext._headers` stays typed as `IReadOnlyDictionary<string, object>` (public `Headers` property unchanged). Assign the underlying `Dictionary<string, object>` directly, since `Dictionary<K,V>` implements `IReadOnlyDictionary<K,V>`. If the incoming `IDictionary<string, object>` isn't already a concrete `Dictionary`, clone into one (this branch is rarely hit — RabbitMQ supplies a `Dictionary` — but preserves the existing safety story).

```csharp
_headers = headers as Dictionary<string, object>
         ?? new Dictionary<string, object>(headers);
```

The `ReadOnlyDictionary` wrapper never actually protected callers from mutation (the wrapped dictionary is the same reference); it only hid `Add` from the `IDictionary<K,V>` view. The `IReadOnlyDictionary<K,V>` type already provides that hiding without an extra allocation.

Remove the `EmptyHeaders` `ReadOnlyDictionary` constant — replace with a static empty `Dictionary<string, object>`.

### Item 5 — sorted index for in-memory timeouts

Augment `InMemoryPersistenceState` with:

```csharp
internal readonly SortedSet<TimeoutEntry> TimeoutIndex = new(TimeoutEntryComparer.Instance);
internal readonly record struct TimeoutEntry(DateTimeOffset Time, Guid Id);
```

`TimeoutEntryComparer` orders by `Time`, ties broken by `Id`, giving a total order suitable for `SortedSet`. All index mutations happen under the existing `SyncRoot` write lock.

- `InsertTimeoutAsync`: `TimeoutIndex.Add(new(timeoutData.Time, timeoutData.Id))` alongside the `Provider.Add` call.
- `RemoveDispatchedTimeoutAsync`: fetch the timeout's `Time` before removing from the provider, then `TimeoutIndex.Remove(new(time, id))`.
- `GetTimeoutsBatchAsync`: walk the head of `TimeoutIndex` while `entry.Time <= utcNow`, load each corresponding `TimeoutData` from the provider, add to `DueTimeouts`. The next entry after the due run (if any) becomes `NextQueryTime`. No full scan.

Expired-via-`ExpiryDuration` entries get pruned from the index opportunistically during poll when their provider lookup returns `null` (i.e., the provider's absolute expiry already removed them).

**Result:** O(k log n) instead of O(n) per poll, where k = due count.

### Item 6 — two round trips for MongoDB timeout polling

Keep the `UpdateMany` claim at [MongoDbTimeoutStore.cs:71](src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs#L71). Replace the two subsequent `Find` calls with a single aggregation pipeline using `$facet`:

```
db.Timeouts.aggregate([
  { $facet: {
      due:  [ { $match: { LockedBy: <sessionId>, Locked: true } } ],
      next: [ { $match: { Time: { $gt: <utcNow> }, Locked: false } },
              { $sort: { Time: 1 } },
              { $limit: 1 },
              { $project: { Id: 1, Time: 1 } } ]
  }}
])
```

Executed via `collection.Aggregate<BsonDocument>(pipeline)`. Decode the two facet arrays into `dueLocked` and `nextTimeout`. Skip the aggregation entirely when `UpdateMany.ModifiedCount == 0` and the index knows of no locked owned docs, but still need `next` — in that case, issue only the `next` query (still one round trip, not two).

**Result:** 3 → 2 round trips in the common case (due > 0); 3 → 1 when there's nothing due (skip UpdateMany if we know from a local watermark — optional optimization, deferred unless trivial).

### Item 7 — E2E polling helper

Add `src/ServiceConnect.EndToEndTests/Helpers/TestPolling.cs`:

```csharp
internal static class TestPolling
{
    public static async Task<T?> WaitUntilAsync<T>(
        Func<Task<T?>> probe,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(50);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var result = await probe().ConfigureAwait(false);
            if (result is not null) return result;
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    public static Task WaitUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default);
}
```

Replace the 7 fixed-delay sites:

- `RetryAndErrorQueueTests.cs:99-103`, `CustomErrorQueueTests.cs`, `AggregatorExceptionTests.cs`, `MalformedMessageTests.cs`, `ProcessManagerExceptionTests.cs` — change `for (int i = 0; i < 30 && errorMsg == null; i++)` loops to `TestPolling.WaitUntilAsync(async () => await channel.BasicGetAsync(...), TimeSpan.FromSeconds(30))`.
- `QueuePurgeTests.cs:103` and `AuditingTests.cs:85` — these are "give setup time" sleeps. Replace with a condition-based wait where possible (e.g., poll the consumer's `IsConsuming` state for `QueuePurgeTests`). Where no observable condition exists, keep a short fixed wait but document why.

### Items 8–10 — mechanical cleanups

**8. `Producer.GetHeaders`** — replace `if (!result.ContainsKey(k)) result[k] = v;` pairs with `result.TryAdd(k, v);` at [Producer.cs:283-300](src/ServiceConnect.Client.RabbitMQ/Producer.cs#L283-L300). Two lookups become one.

**9. `ServiceConnectActivitySource.TryGetExistingContext`** — delete the `foreach`/`ContainsKey` pre-scan at [ServiceConnectActivitySource.cs:169-177](src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L169-L177) entirely. The subsequent `DistributedContextPropagator.Current.ExtractTraceIdAndState` call already queries the headers; when no trace headers are present, the extract callback returns `null` values and `ActivityContext.TryParse(null, null, out _)` returns `false` cleanly. The pre-scan duplicates the work. Simplified body: null-guard headers, call `ExtractTraceIdAndState`, then `ActivityContext.TryParse` and return.

**10. `Bus.cs` startup type-name build** — replace the LINQ chain at [Bus.cs:242-247](src/ServiceConnect/Bus.cs#L242-L247) with a single-pass `HashSet<string>` build, then materialise to `List<string>`:

```csharp
var typeNameSet = new HashSet<string>(_handlerReferences.Count);
foreach (var h in _handlerReferences)
    typeNameSet.Add(h.MessageType.FullName!.Replace(".", string.Empty));
messageTypeNames = [.. typeNameSet];
```

One pass, one allocation for the set plus the output list.

## Testing

- **Unit:** `NewtonsoftJsonMessageSerializerTests` gains assertions for the `ReadOnlyMemory<byte>` and `ReadOnlySequence<byte>` overloads (round-trip equality with existing `byte[]`/span paths). New `ReadOnlyMemoryStreamTests` and `ReadOnlySequenceStreamTests`. `InMemoryTimeoutStoreTests` adds "large number of future entries + small due subset" to verify polling cost via behaviour (time-based, not asserting O-complexity but ensuring correctness under the new data structure). `ConsumeContextPoolTests` verifies `Headers` still exposes `IReadOnlyDictionary<K,V>` with expected contents.
- **Integration / Testcontainers:** `MongoDbTimeoutStoreTests` verifies the `$facet` shape returns the same batch contents and `NextQueryTime` as before for the "due > 0", "due == 0 + future exists", and "due == 0 + no future" cases.
- **E2E:** existing suite must pass with the new polling helper; wall-clock runtime should drop materially for the seven converted sites. Watch for flake: if 50ms polls miss a fast-settling condition on a loaded machine, bump default interval to 100ms.
- **Benchmark (optional, nice-to-have):** a micro-benchmark for `MessageDispatcher.Dispatch` before/after on a representative message size would quantify item 1's win. Not a prerequisite for merging.

## Risk & Rollback

- **Item 1/2** — behaviour risk is near-zero if the `ReadOnlyMemoryStream` / `ReadOnlySequenceStream` wrappers are exercised by unit tests. Rollback = revert the serializer and `MessageBusReadStream` changes; call sites continue to work with the preserved `byte[]` overloads.
- **Item 3** — removing `Task.Run` means a slow synchronous stream handler now blocks the dispatch task for its full duration. This matches how the non-cancellable branch already worked, so anyone relying on the Task.Run isolation was already getting it only "sometimes." Document in release notes.
- **Item 4** — the `ReadOnlyDictionary` wrapper hid `Add` from a cast to `IDictionary<K,V>`. Consumers who cast `Headers` up and expected `NotSupportedException` on `Add` will now see success. Mitigation: the wrapper was never a security boundary; downcasting `IReadOnlyDictionary<K,V>` from a `Dictionary` backing is detectable but not "prevented" today either. Accept as a behaviour change.
- **Item 5** — the sorted index must stay in sync with the provider on all mutation paths, including failures. Put index updates inside the existing write lock, after successful provider mutation, and wrap in try/catch to restore on downstream failure.
- **Item 6** — `$facet` requires MongoDB 3.4+. All supported deployments meet this. Shape change is driver-internal; no API change.
- **Item 7** — test polling at higher frequency exposes existing race conditions if any. That's desirable but may surface flake — budget for two re-runs.
- **Items 8–10** — trivial; rollback by reverting the touched lines.

## Sequencing (on-branch commit cadence)

Commit per severity tier for reviewability, even without a PR split:

1. **Commit A — High (items 1, 2):** `ReadOnlyMemoryStream`, `ReadOnlySequenceStream`, new serializer overloads, `MessageBusReadStream.ReadSequence`, dispatcher/reply-manager/stream-processor call-site updates, tests.
2. **Commit B — Medium (items 3–7):** each item ideally its own commit, or one commit with a clear message body enumerating them.
3. **Commit C — Low (items 8–10):** single commit.

## Open Questions

None — all design decisions above were confirmed in brainstorming (stay on Newtonsoft, inline stream handlers, full remediation on branch, commit-per-tier cadence).
