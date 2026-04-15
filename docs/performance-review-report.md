# ServiceConnect-CSharp — Performance Audit Report

**Date:** 2026-04-14
**Branch:** `improvements-and-fixes`
**Scope:** All production `.cs` files in `src/ServiceConnect.sln`
**Methodology:** 5 parallel audit agents covering message dispatch, bus lifecycle/serialization, RabbitMQ transport, persistence layers, and interfaces/telemetry

---

## Summary

| Severity | Count |
|----------|-------|
| Critical | 8 |
| High | 25 |
| Medium | 24 |
| Low | 15 |
| **Total** | **72** |

### Hot-Path Classification

Findings are marked with their path frequency:
- **Every message** — runs on every publish, send, or consume
- **Per saga** — runs per process-manager message
- **Per stream** — runs per stream packet or completion
- **Startup** — runs once at startup
- **Error path** — runs on failures/retries only

---

## Critical

---

### P-001 — `MessageDispatcher.Dispatch`: Middleware chain rebuilt with closures per message

**File:** `ServiceConnect/Services/MessageDispatcher.cs`, lines 69–97
**Category:** Memory & GC — closure captures
**Path:** Every message (when middleware configured)

The middleware chain is rebuilt via closure-allocating loop on every message dispatch. Each middleware layer allocates a delegate + closure object. With 3 middleware types: 4+ heap objects per message.

**Impact:** O(N) delegate + closure allocations per message. At 10k msg/s with 3 middleware: ~40k short-lived objects/sec.

**Fix:** Build and cache the chain once at startup in a `Lazy<MessageProcessingDelegate>`, mirroring `SendMessagePipeline._publishChain`.

---

### P-002 — `NewtonsoftJsonMessageSerializer`: Intermediate string allocation on every serialize/deserialize

**File:** `ServiceConnect/Services/NewtonsoftJsonMessageSerializer.cs`, lines 26–28, 44–45
**Category:** Serialization — UTF-8 string allocation
**Path:** Every message

`Serialize` calls `JsonConvert.SerializeObject` -> `string` -> `Encoding.UTF8.GetBytes`. `Deserialize` calls `GetString` -> `DeserializeObject`. Two full-size intermediate strings per message. Additionally, `JsonConvert` internally creates a new `JsonSerializer` instance per call (not cached).

**Impact:** Two unnecessary string allocations per message. For 10 KB messages at 5k msg/s: ~100 MB/s of garbage. The uncached `JsonSerializer` adds ~2-3x throughput loss.

**Fix:** Create and cache `JsonSerializer.Create(_settings)` as a field. Use `JsonTextWriter`/`JsonTextReader` over `MemoryStream` to avoid the intermediate string entirely.

---

### P-003 — `RabbitMqConsumerHost.ProcessMessageAsync`: `args.Body.ToArray()` on every message

**File:** `ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs`, line 174
**Category:** Memory & GC — buffer copy
**Path:** Every message received

`args.Body` is `ReadOnlyMemory<byte>`. `.ToArray()` unconditionally allocates a full heap copy of every message body.

**Impact:** One `byte[]` allocation per message = full body size. At 10k msg/s with 1 KB avg: ~10 MB/s garbage. Larger messages hit LOH.

**Fix:** Change `ConsumerEventHandler` delegate to accept `ReadOnlyMemory<byte>`. Short-term: use `ArrayPool<byte>.Shared.Rent()`.

---

### P-004 — `Producer.PublishAsync`: Exchange declare round-trip on every publish

**File:** `ServiceConnect.Client.RabbitMQ/Producer.cs`, line 110
**Category:** I/O — unnecessary AMQP round-trip
**Path:** Every publish

`ConfigureExchangeAsync` issues `ExchangeDeclareAsync` to the broker on every `PublishAsync`. Exchange declare is idempotent but requires a full network round-trip (~0.1–1 ms).

**Impact:** Caps publish throughput to ~1/RTT msg/s per channel. At 1 ms RTT: max ~1000 msg/s regardless of other optimizations.

**Fix:** Cache declared exchange names in a `HashSet<string>`. Skip declare after first success.

---

### P-005 — `ProcessManagerProcessor.ProcessAsync`: `new DefaultProcessManagerPropertyMapper()` per message

**File:** `ServiceConnect/Services/Processors/ProcessManagerProcessor.cs`, line 41
**Category:** Memory & GC — allocation + expression compilation
**Path:** Per saga message

Fresh mapper + internal `List<ProcessManagerToMessageMap>` + compiled expression lambdas per message. The mapper's mappings are identical for a given handler type.

**Impact:** 2+ allocations + expression compilation (~50–500 us) per saga message.

**Fix:** Cache mappings per handler type at registry build time. Store on the descriptor.

---

### P-006 — `MongoDbProcessManagerFinder.InsertDataAsync`: Uncached `MakeGenericMethod` per call

**File:** `ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs`, lines 125–128
**Category:** Reflection — uncached hot-path reflection
**Path:** Per saga start

`GetMethod` + `MakeGenericMethod` + `Invoke` on every insert. ~5–15 us + 2–5 allocations per call.

**Impact:** At 1000 saga starts/sec: ~5–15 ms/s pure reflection overhead.

**Fix:** Cache compiled delegates in `ConcurrentDictionary<Type, Func<...>>`.

---

### P-007 — `MongoDbProcessManagerFinder.FindDataAsync`: Uncached Expression tree built per call

**File:** `ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs`, lines 80–100
**Category:** Reflection + allocations
**Path:** Per saga message

6–12 intermediate `Expression` nodes, `ParameterExpression`, `BinaryExpression`, `LambdaExpression`, uncached `GetProperty` reflection, `Reverse()` enumeration — all rebuilt from scratch on every `FindDataAsync`.

**Impact:** ~1–4 us CPU + 10+ heap objects per saga lookup.

**Fix:** Cache the compiled filter expression or `FilterDefinition<MongoDbData<T>>` keyed by `(Type, PropertiesHierarchy)`.

---

### P-008 — `ServiceConnectActivitySource.Consume`: Full header decode dictionary per message

**File:** `ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`, lines 110–114
**Category:** Memory & GC — dictionary + N string allocations
**Path:** Every consumed message (when telemetry active)

Allocates a `Dictionary<string, string?>` and decodes ALL headers (15–20) even though only 2–3 are actually read.

**Impact:** 1 dictionary + ~15 string allocations per consumed message. At 10k msg/s: ~200k objects/sec.

**Fix:** Decode only the specific headers needed (`DestinationAddress`, `MessageId`) via targeted `TryGetValue` calls.

---

## High

---

### P-009 — `Envelope` allocated per outgoing message even when no filters registered

**File:** `ServiceConnect/Bus.cs`, lines 298–335
**Category:** Memory & GC
**Path:** Every outgoing message

Every Publish/Send/Route creates `Envelope` + `Dictionary<string, object>` + a second `Dictionary<string, string>` via `ExtractHeaders`. When no outgoing filters exist, the Envelope is pure waste.

**Fix:** Check filter count before constructing Envelope. Provide a no-filter fast path.

---

### P-010 — `HandlerProcessor.ForwardRoutingSlipAsync`: Uncached `MakeGenericMethod` reflection

**File:** `ServiceConnect/Services/Processors/HandlerProcessor.cs`, lines 85–86
**Category:** Reflection — uncached
**Path:** Every message with RoutingSlip header

`GetMethod` + `MakeGenericMethod` + `object[]` allocation per routed message.

**Fix:** Cache delegates in `ConcurrentDictionary<Type, Func<...>>`.

---

### P-011 — `StreamProcessor.ProcessAsync`: Uncached `Task.FromResult` at 8+ return sites

**File:** `ServiceConnect/Services/Processors/StreamProcessor.cs`, lines 47–122
**Category:** Memory & GC
**Path:** Every message on stream-aware endpoints

`Task.FromResult(ProcessResult.NotHandled/Handled)` allocates on each call. `ReplyProcessor` correctly caches these.

**Fix:** Add static cached fields: `private static readonly Task<ProcessResult> NotHandledTask/HandledTask`.

---

### P-012 — `HandlerProcessor`/`ProcessManagerProcessor`: `IBus` re-resolved from DI per message

**File:** `ServiceConnect/Services/Processors/HandlerProcessor.cs:39`, `ProcessManagerProcessor.cs:59`
**Category:** DI overhead
**Path:** Every message

`IBus` is a singleton, resolved via `GetRequiredService<IBus>()` on every dispatch.

**Fix:** Inject `IBus` into constructors.

---

### P-013 — `AggregatorProcessor.ResetTimer`: New `Timer` allocated per message

**File:** `ServiceConnect/Services/Processors/AggregatorProcessor.cs`, lines 54–68
**Category:** Memory & GC + OS resources
**Path:** Every aggregator message with timeout

New `Timer` + closure per message. Old timer disposed. Timer involves kernel resource alloc/free.

**Fix:** Use `Timer.Change(dueTime, period)` on existing timer instead of replacing.

---

### P-014 — `Producer.CreateBasicProperties`: LINQ `Select` header copy per message

**File:** `ServiceConnect.Client.RabbitMQ/Producer.cs`, lines 191–215
**Category:** Memory & GC — LINQ
**Path:** Every publish/send

LINQ `Select` projection over header dict creates enumerator + intermediary. A direct `foreach` copy eliminates 3–4 allocations.

**Fix:** Replace with `foreach` loop into pre-sized dictionary.

---

### P-015 — `Producer.GetHeaders`: `DateTime.UtcNow.ToString("O")` on every message

**File:** `ServiceConnect.Client.RabbitMQ/Producer.cs:244`, `RabbitMqConsumerHost.cs:159,177`
**Category:** Memory & GC — string allocation
**Path:** Every message (1 on send, 2 on receive = 3 total per round-trip)

28-char string allocation per timestamp. Three per message round-trip.

**Fix:** Use `TryFormat` with stackalloc buffer.

---

### P-016 — `Producer.GetHeaders`: Uncached `type.FullName` and `type.AssemblyQualifiedName`

**File:** `ServiceConnect.Client.RabbitMQ/Producer.cs`, lines 248–251
**Category:** Memory & GC — repeated identical strings
**Path:** Every publish/send

Two string allocations per message that are always identical for the same `Type`.

**Fix:** Cache in `ConcurrentDictionary<Type, (string, string)>`.

---

### P-017 — `Producer.PublishAsync`: Exchange name `type.FullName.Replace(".", "")` per publish

**File:** `ServiceConnect.Client.RabbitMQ/Producer.cs`, line 110
**Category:** Memory & GC — string allocation
**Path:** Every publish

String allocation from `Replace` on every call.

**Fix:** Cache exchange name alongside declaration state (P-004).

---

### P-018 — `RabbitMqConsumerHost.ProcessMessageAsync`: Header dictionary copy per message

**File:** `ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs`, lines 144–152
**Category:** Memory & GC
**Path:** Every message received

New `Dictionary<string, object>` copying all AMQP headers. Capacity hint doesn't account for 3 consumer-added headers.

**Fix:** Size hint as `(sourceHeaders?.Count ?? 4) + 3`. Long-term: pooled or struct-based header wrapper.

---

### P-019 — `RabbitMqConsumerHost.ProcessMessageAsync`: Double dictionary lookup on type name

**File:** `ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs`, line 164
**Category:** CPU — wasted lookup
**Path:** Every message received

`ContainsKey` + indexer = 2 lookups when `TryGetValue` suffices.

**Fix:** Use `TryGetValue`.

---

### P-020 — `HandlerProcessor.ProcessAsync`: `GetServices` allocates new enumerable per hierarchy level

**File:** `ServiceConnect/Services/Processors/HandlerProcessor.cs`, lines 27–32
**Category:** Memory & GC — DI enumerable
**Path:** Every message with handlers

`serviceProvider.GetServices(type)` materializes a new collection. Called once per hierarchy level.

**Fix:** Cache resolved handler instances on descriptors at startup (for singletons).

---

### P-021 — `MongoDbPersistenceExtensions`: Multiple `MongoClient` instances

**File:** `ServiceConnect.Persistence.MongoDb/MongoDbPersistenceExtensions.cs`, lines 13–28
**Category:** Connection pool — 3x connections
**Path:** Startup/runtime

DI registers one `MongoClient` singleton, but constructors of `MongoDbProcessManagerFinder` and `MongoDbAggregatorPersistor` each create their own via `MongoClientFactory.Create()`. Three independent connection pools.

**Fix:** Inject the DI-registered `IMongoClient` into all persistence classes.

---

### P-022 — `MongoDbProcessManagerFinder`: Missing timeout collection indexes

**File:** `ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs`, lines 335–351
**Category:** Database — full collection scan
**Path:** Every timeout poll

Queries on `Locked`, `Time`, `LockedBy` but only `Id` is indexed. Full collection scan per poll.

**Fix:** Add compound indexes `{ Locked: 1, Time: 1 }` and `{ LockedBy: 1, Locked: 1 }`.

---

### P-023 — `MongoDbProcessManagerFinder.EnsureTimeoutIndex`: Synchronous `CreateOne` from async context

**File:** `ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs`, lines 335–351
**Category:** Async — thread pool starvation
**Path:** First timeout operation per startup

Synchronous `collection.Indexes.CreateOne(...)` blocks a thread-pool thread.

**Fix:** Use `CreateOneAsync`. Move to startup initialization.

---

### P-024 — `CacheProvider`: Rx `Observable.Timer` subscriptions never disposed (memory leak)

**File:** `ServiceConnect.Persistence.InMemory/CacheProvider.cs`, lines 134–142
**Category:** Memory leak
**Path:** Every cache add/update

Each `StartObserving` creates an Rx timer subscription that is never stored or disposed. On `Remove` or `Update`, old subscriptions leak. Silent error handler swallows faults.

**Fix:** Store `IDisposable` per key and dispose on remove. Or replace with `System.Threading.Timer`.

---

### P-025 — `InMemoryProcessManagerFinder`: Coarse `_memoryCacheLock` serializes all persistence operations

**File:** `ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs`, lines 40–41
**Category:** Concurrency — lock contention
**Path:** Per saga message

All find/insert/update/delete and timeout-batch operations serialize on a single lock, defeating `ConcurrentDictionary`'s native concurrency.

**Fix:** Use striped locks or `ReaderWriterLockSlim`. Separate timeout store from PM data.

---

### P-026 — `InMemoryProcessManagerFinder.FindMatchingItem`: Uncached reflection in fallback

**File:** `ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs`, lines 84–90
**Category:** Reflection — uncached
**Path:** Per saga find (type-mismatch entries)

`GetProperty("Data")` and `GetProperty("Version")` per cache entry in the fallback branch.

**Fix:** Cache `PropertyInfo` or compiled getters in `ConcurrentDictionary<Type, (Func, Func)>`.

---

### P-027 — `MessageBusReadStream.IsComplete()`: O(N) linear scan per packet, O(N^2) total

**File:** `ServiceConnect/Services/MessageBusReadStream.cs`, lines 59–67
**Category:** Algorithm — quadratic
**Path:** Per stream packet (after LastPacketNumber known)

Iterates 0..LastPacketNumber checking `ContainsKey` per packet arrival. 1000-packet stream = 500k `ContainsKey` calls total.

**Fix:** Track received count via `Interlocked.Increment`. `IsComplete` becomes O(1): `_receivedCount == LastPacketNumber + 1`.

---

### P-028 — `MessageBusReadStream.Read()`: `MemoryStream` + `ToArray()` doubles memory

**File:** `ServiceConnect/Services/MessageBusReadStream.cs`, lines 50–57
**Category:** Memory & GC — double buffer
**Path:** Per stream completion

Unsized `MemoryStream` doubles ~9 times for 10 MB. `ToArray()` creates a second full copy. Both > 85 KB hit LOH.

**Fix:** Pre-size `MemoryStream` with `_totalBytesWritten`. Or allocate `byte[_totalBytesWritten]` and copy directly.

---

### P-029 — `HeaderHelpers.ToNullableHeaders`: Full LINQ `.ToDictionary()` copy

**File:** `ServiceConnect.Client.RabbitMQ/HeaderHelpers.cs`, line 14
**Category:** Memory & GC — LINQ
**Path:** Every retry/audit message

Creates enumerator + new dictionary via LINQ on every retry and audit publish.

**Fix:** Use `foreach` loop with pre-sized `Dictionary(count, StringComparer.Ordinal)`.

---

### P-030 — `Producer.SendAsync`: Per-endpoint header dict inside `_publishLock`

**File:** `ServiceConnect.Client.RabbitMQ/Producer.cs`, lines 126–131
**Category:** Memory & GC + Concurrency — lock hold time
**Path:** Every multi-endpoint send

Full `GetHeaders` dict per endpoint in loop, all inside the publish lock. N endpoints = N dictionary allocations under lock.

**Fix:** Build base headers once, clone only `DestinationAddress` per endpoint.

---

### P-031 — `ConsumeContext`: Properties re-decode headers on every access

**File:** `ServiceConnect/Services/ConsumeContext.cs`, lines 12, 16
**Category:** Memory & GC — repeated decoding
**Path:** Every handler that accesses `MessageId`/`CorrelationId`

`HeaderDecoder.Decode` + `Guid.TryParse` re-executed on every property access.

**Fix:** Cache decoded values with lazy initialization: `_messageId ??= Decode(...)`.

---

### P-032 — `MessageRetryHandler.HandleFailureAsync`: Boxing + string round-trip for retry count

**File:** `ServiceConnect.Client.RabbitMQ/MessageRetryHandler.cs`, lines 33–35
**Category:** Memory & GC — boxing
**Path:** Every retry

`raw?.ToString()` on a boxed int allocates a string, then `int.TryParse` parses it back.

**Fix:** Direct unbox: `raw is int i ? i : int.TryParse(raw?.ToString(), ...)`.

---

### P-033 — `ConsumeEventArgs.Headers`: Default dictionary allocated then immediately replaced

**File:** `ServiceConnect.Interfaces/ConsumeEventArgs.cs`, lines 9–15
**Category:** Memory & GC — wasted allocation
**Path:** Every consumed message

Field initializer `= new Dictionary<string, object>()` runs before the `init` accessor replaces it.

**Fix:** Initialize to `null!`. Use `??=` in getter.

---

## Medium

---

### P-034 — `ConsumeContext`: Heap allocation per message

**File:** `ServiceConnect/Services/Processors/HandlerProcessor.cs:40`, `ProcessManagerProcessor.cs:62`
**Path:** Every handled message. **Fix:** Pool via `ObjectPool<ConsumeContext>`.

### P-035 — `MessageTypeRegistry._types`: `ConcurrentDictionary` for read-only-after-startup lookup

**File:** `ServiceConnect/Services/MessageTypeRegistry.cs`, line 8
**Path:** Every message. **Fix:** Convert to `FrozenDictionary` after registration completes.

### P-036 — `MessageHandlerRegistry._descriptors`: `ConcurrentDictionary` vs `FrozenDictionary` for pre-registered types

**File:** `ServiceConnect/Services/Processors/MessageHandlerRegistry.cs`, line 11
**Path:** Every message. **Fix:** Use `FrozenDictionary` for known types + `ConcurrentDictionary` for lazy-built.

### P-037 — `AggregatorProcessor.FlushAggregatorAsync`: Sequential `await` loop for batch removal

**File:** `ServiceConnect/Services/Processors/AggregatorProcessor.cs`, lines 103–107
**Path:** Per aggregator flush. **Fix:** `Task.WhenAll` or batch `RemoveAllAsync`. Also add `RemoveDataBatchAsync` to `IAggregatorPersistor`.

### P-038 — `StreamProcessor`: Dual `ConcurrentDictionary` for stream state

**File:** `ServiceConnect/Services/Processors/StreamProcessor.cs`, lines 14–15
**Path:** Per stream packet. **Fix:** Merge timestamp into `MessageBusReadStream` or use single composite-value dict.

### P-039 — `StreamProcessor.InvokeExecute`: Sync on dispatch thread + serializer re-resolved from DI

**File:** `ServiceConnect/Services/Processors/StreamProcessor.cs`, line 115, 119
**Path:** Per stream completion. **Fix:** Inject `IMessageSerializer` at construction. Wrap sync `Execute` in `Task.Run`.

### P-040 — `IMessageSerializer` interface: `byte[]` contract blocks span/pipeline adoption

**File:** `ServiceConnect.Interfaces/IMessageSerializer.cs`, lines 5–7
**Path:** Every message. **Fix:** Add `IBufferWriter<byte>` / `ReadOnlySpan<byte>` overloads.

### P-041 — `RequestReplyManager`: Two `CancellationTokenSource` per request

**File:** `ServiceConnect/Services/RequestReplyManager.cs`, lines 31–32, 90–91
**Path:** Per request-reply. **Fix:** Use single `CreateLinkedTokenSource` + `CancelAfter`.

### P-042 — `Bus.RouteAsync`: `destinations.Skip(1)` LINQ allocation

**File:** `ServiceConnect/Bus.cs`, line 182
**Path:** Per routed message. **Fix:** `string.Join(",", destinations, 1, destinations.Count - 1)`.

### P-043 — `ConsumeContext.ReplyAsync`: `SendOptions` heap allocation per reply

**File:** `ServiceConnect/Services/ConsumeContext.cs`, lines 19–32
**Path:** Per reply. **Fix:** Make `SendOptions` a struct or use a static default.

### P-044 — `QueueConfiguration.AddQueueMapping`: `ImmutableList.Contains` is O(N)

**File:** `ServiceConnect/Configuration/QueueConfiguration.cs`, lines 32–34
**Path:** Startup. **Fix:** Use `ImmutableHashSet<string>`.

### P-045 — `MessageBusWriteStream.WriteAsync`: Defensive `byte[]` copy per packet

**File:** `ServiceConnect/Services/MessageBusWriteStream.cs`, lines 32–33
**Path:** Per stream packet. **Fix:** Check if transport copies. Pass `ReadOnlyMemory<byte>` instead.

### P-046 — `ProcessManagerFinders.FindDataAsync`: Double `FirstOrDefault` LINQ scan

**File:** `InMemoryProcessManagerFinder.cs:40`, `MongoDbProcessManagerFinder.cs:48`
**Path:** Per saga message. **Fix:** Single-pass `foreach` with type cache.

### P-047 — `InMemoryProcessManagerFinder.UpdateDataAsync`: Remove+Add causes timer churn

**File:** `ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs`, lines 183–193
**Path:** Per saga update. **Fix:** Add `CacheProvider.Update` method that replaces value without resetting timer.

### P-048 — `AggregatorProcessor`: DI re-resolution of `IAggregatorPersistor` and aggregator per message/flush

**File:** `ServiceConnect/Services/Processors/AggregatorProcessor.cs`, lines 32, 90, 98
**Path:** Per aggregator message. **Fix:** Inject at construction.

### P-049 — `Producer`: `Assembly.GetEntryAssembly()` + `Process.GetCurrentProcess()` on reconnect

**File:** `ServiceConnect.Client.RabbitMQ/Producer.cs`, lines 82–83
**Path:** Per reconnect. **Fix:** Cache as `static readonly`.

### P-050 — `CacheProvider.Count()`: `_cache.Keys.Count` snapshots all keys

**File:** `ServiceConnect.Persistence.InMemory/CacheProvider.cs`, line 96–99
**Path:** Per count call. **Fix:** Use `_cache.Count`.

### P-051 — `CacheProvider.PurgeNormalPriorities`: Double-pass LINQ with `Count(predicate)` side-effect

**File:** `ServiceConnect.Persistence.InMemory/CacheProvider.cs`, lines 104–108
**Path:** Per purge. **Fix:** Single-pass `foreach` loop.

### P-052 — `InMemoryAggregatorPersistor.RemoveDataAsync`: Double O(N) scan (LINQ + List.Remove)

**File:** `ServiceConnect.Persistence.InMemory/InMemoryAggregatorPersistor.cs`, lines 54–66
**Path:** Per aggregator removal. **Fix:** Index-based `RemoveAt`.

### P-053 — `MongoDbAggregatorPersistor`: Missing indexes on `Name` and `DataBson.CorrelationId`

**File:** `ServiceConnect.Persistence.MongoDb/MongoDbAggregatorPersistor.cs`
**Path:** Per aggregator query/remove. **Fix:** Add compound indexes at startup.

### P-054 — `ServiceCollectionExtensions`: Triple `GetInterfaces()` per handler at startup

**File:** `ServiceConnect/ServiceCollectionExtensions.cs`, lines 88–130
**Path:** Startup. **Fix:** Call `GetInterfaces()` once and reuse.

### P-055 — `MongoDbProcessManagerFinder.GetTimeoutsBatchAsync`: No projection, full doc fetch

**File:** `ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs`, lines 261–277
**Path:** Per timeout poll. **Fix:** Use projection for next-query-time. Project out `Headers` for batch.

### P-056 — `Connection.ConnectAsync`: Non-volatile `_connection` in double-checked lock

**File:** `ServiceConnect.Client.RabbitMQ/Connection.cs`, lines 18, 23
**Path:** Per reconnect. **Fix:** Use `Volatile.Read(ref _connection)`.

### P-057 — `MongoDbProcessManagerFinder.DeleteDataAsync`: `DeleteManyAsync` for unique key

**File:** `ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs`, line 214
**Path:** Per saga delete. **Fix:** Use `DeleteOneAsync`.

---

## Low

---

### P-058 — Registry constructors: `GetInterfaces().FirstOrDefault` LINQ allocation

**Files:** `MessageHandlerRegistry.cs:65`, `ProcessManagerHandlerRegistry.cs:26`, `StreamHandlerRegistry.cs:53`
**Path:** Startup. **Fix:** Replace with `foreach` + break.

### P-059 — `AggregatorRegistry`/`StreamHandlerRegistry`: Intermediate `ToDictionary` before `ToFrozenDictionary`

**Path:** Startup. **Fix:** Call `ToFrozenDictionary` with key/value selectors directly.

### P-060 — `DefaultProcessManagerPropertyMapper.ConfigureMapping`: `new Dictionary<string, Type>()` without capacity

**Path:** Per saga message. **Fix:** `new Dictionary<string, Type>(1)`.

### P-061 — `HandlerProcessor.IsValidRoutingSlipDestination`: Missing `[AggressiveInlining]`

**Path:** Per routing-slip destination. **Fix:** Add attribute.

### P-062 — `HeaderDecoder.Decode`: Missing `[AggressiveInlining]`

**Path:** Every header access. **Fix:** Add attribute. Add fast `string` path.

### P-063 — `HeaderHelpers.SetHeader`: Missing `[AggressiveInlining]`

**Path:** 4–6 calls per message. **Fix:** Add attribute.

### P-064 — `RequestReplyManager`/`MessageBusWriteStream`: `Guid.ToString()` / `long.ToString()` allocations

**Path:** Per request / per stream packet. **Fix:** Use `TryFormat` with stackalloc.

### P-065 — `InMemoryProcessManagerFinder`: `Guid.ToString()` for cache keys

**Path:** Per saga operation. **Fix:** Use `Guid` as dictionary key directly.

### P-066 — `CacheProvider.Remove`: `new EventArgs()` instead of `EventArgs.Empty`

**Path:** Per remove. **Fix:** Use `EventArgs.Empty`.

### P-067 — `Retry.DoAsync`: `List<Exception>` allocated even on success path

**Path:** Per retry call. **Fix:** Lazy init: `List<Exception>? exceptions = null`.

### P-068 — `PipelineConfiguration`: Mutable `List<Type>` for read-only runtime collections

**Path:** Every filter check. **Fix:** Freeze to `IReadOnlyList<Type>` after builder completes.

### P-069 — `RequestOptions.Default`: Allocated per `SendRequestAsync` when `null` passed

**Path:** Per request-reply. **Fix:** Use `static readonly RequestOptions Default`.

### P-070 — `SendEventArgs.EndPoints`: `Trim` + `Split` allocations on access

**Path:** Per send event. **Fix:** Use `AsSpan()` to trim without allocation.

### P-071 — `ServiceConnectInstrumentationOptions.Enable*Telemetry` flags: Never checked in activity sources

**Path:** Every message with telemetry. **Fix:** Gate activity creation on both `HasListeners()` AND `Enable*` flag.

### P-072 — `Producer.PublishWithRetryAsync`: Trivial async wrapper adds state machine overhead

**Path:** Every publish. **Fix:** Return `ValueTask` directly without `async`/`await`.

---

## Hot-Path Impact Summary

The following table shows estimated per-message allocation counts for common paths, before and after applying the Critical + High fixes:

| Path | Current Allocations/msg | After Fixes |
|------|------------------------|-------------|
| **Publish** (no filters) | ~15–20 | ~3–5 |
| **Publish** (3 middleware) | ~25–30 | ~5–8 |
| **Consume + dispatch** | ~20–25 | ~5–8 |
| **Saga message** | ~30–40 | ~8–12 |
| **Stream packet** | ~8–12 | ~3–5 |
| **Request-reply** | ~25–30 | ~8–12 |

### Top 5 Highest-Impact Fixes (by throughput gain)

1. **P-004** — Cache exchange declares. Removes network round-trip per publish. **~10x publish throughput.**
2. **P-002** — Cache `JsonSerializer` + stream-based serialize. **~2-3x serialization throughput.**
3. **P-003** — Pool message body buffer. **~10 MB/s GC reduction at 10k msg/s.**
4. **P-001** — Cache middleware chain. **Eliminates O(N) closures per message.**
5. **P-005** — Cache PM mappings at registry time. **Eliminates expression compilation per saga message.**

---

## Related Finding Groups

Findings that should be fixed together:

| Group | Findings | Theme |
|-------|----------|-------|
| A | P-001, P-034, P-009 | Per-message dispatch allocations |
| B | P-002, P-040 | Serialization pipeline |
| C | P-004, P-016, P-017 | Producer exchange/type caching |
| D | P-005, P-007, P-046 | PM mapper + expression caching |
| E | P-006, P-023, P-021 | MongoDB persistence startup + pooling |
| F | P-012, P-020, P-048 | DI resolution caching |
| G | P-013, P-047, P-024 | Timer/cache lifecycle |
| H | P-014, P-015, P-018, P-019 | Transport header handling |
| I | P-027, P-028, P-038 | Stream assembly efficiency |
| J | P-008, P-031, P-033 | Consume-side per-message allocations |

---

*Generated by Claude Code — 5 parallel performance audit agents, consolidated and deduplicated.*
