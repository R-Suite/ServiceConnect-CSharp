Progress: Critical 3/3 · High 1/8 · Medium 0/22 · Low 0/14 · Uncertain 1/2 (updated 2026-04-23)

Legend: `[ ]` pending · `[x] (commit: <sha>)` done · `[-] <reason>` deferred/disconfirmed · `[~] <reason>` inconclusive

---

## Critical

- [x] (commit: 2ac5c4d6) **`MongoDbTimeoutStore.EnsureTimeoutIndexAsync` attempts an invalid explicit unique index on `_id`** — [MongoDbTimeoutStore.cs:361-363 (pre-fix)](../src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs)
  `TimeoutData.Id` maps to MongoDB's `_id` field by driver convention. The code created a `CreateIndexModel<TimeoutData>` on `x.Id` with `Unique = true` and passed it to `CreateManyAsync`. MongoDB rejects this with `MongoCommandException: "The field 'unique' is not valid for an _id index specification"` — `_id` is always unique. The 85/86 `IndexOptionsConflict/IndexKeySpecsConflict` catch doesn't trap this error. Result: any fresh Mongo database would fail the very first `InsertTimeoutAsync`/`GetTimeoutsBatchAsync` call — cold-start outage for timeout persistence. Discovered during Phase 0 Task 3 while running `MongoDbTimeoutStoreFacetTests`; fix (dropping the redundant `idIndexModel`) landed bundled with the investigation commit. [discovered post-audit]

- [x] (commit: 11fd17cf) **Poison-message unbounded redelivery when retry/error publish throws** — [RabbitMqConsumerHost.cs:354-379](../src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs)
  CONFIRMED by `PoisonMessageRedeliveryTests.RetryPublishFailure_DoesNotCauseUnboundedRedelivery` — 73,462 handler invocations in 10s against a ceiling of 100; promoted from Uncertain to Critical on 2026-04-23. Fixed in Phase 1: both `HandleFailureAsync` and `HandleTerminalFailureAsync` call sites in `ProcessMessageAsync` are now wrapped in try/catch. On exception, logs at Error and swallows so the method returns `true`, causing `EventAsync` to ack the original message. The message is dropped once; the hot-loop no longer exists. Regression guard `PoisonMessageRedeliveryTests.RetryPublishFailure_DoesNotCauseUnboundedRedelivery` un-skipped and passes with `attemptCount == 1`. [r4, Phase 0 Task 4]

- [x] (commit: 97570dd8) **`ConsumeContextPool` self-referential `EnsureActive` guard permits cross-message data leak** — [ConsumeContextPool.cs:52-123](../src/ServiceConnect/Services/ConsumeContextPool.cs#L52-L123)
  CONFIRMED by `ConsumeContextPoolTests.EnsureActive_RejectsAccessAfterReleaseAndReuse`. `_activeToken` lived on the pooled instance and was reset by `Initialize()`, so a stale reference passed the guard after Release → Rent → Initialize. Fix: `Rent()` now returns a `RentalHandle` struct that captures the token at rent time; `EnsureActive(expectedToken)` on the pooled instance compares against that snapshot. The stale handle's old token never matches the re-incremented `_rentToken`. [r3]

## High

### Core

- [x] (commit: TBD) **Producer has no publish-side timeout under publisher confirms** — [Producer.cs:134-143, 233, 272, 309, 344](../src/ServiceConnect.Client.RabbitMQ/Producer.cs)
  With `PublisherAcknowledgements=true`, each `BasicPublishAsync` awaits a broker ack without a timeout. A half-open connection or broker stall blocks the task while `_publishLock` is held; the entire producer backlogs until the connection heartbeat eventually kills the socket (worst case ~minutes). Fixed: added `PublishTimeout` client setting (default 30s) read in constructor; all 4 `BasicPublishAsync` call sites now go through `PublishWithTimeoutAsync` helper which uses a linked `CancellationTokenSource` with `CancelAfter(_publishTimeout)` and maps the timeout case to `TimeoutException`. `TimeoutException` also excluded from `IsRetriablePublishException` to prevent a reconnect loop on timeout. [r4]

- [ ] **`MessageBusReadStream.IsComplete` accepts non-contiguous packet sets → silent data corruption** — [MessageBusReadStream.cs:46-65, 115-120](../src/ServiceConnect/Services/MessageBusReadStream.cs)
  `Write` has no range check on `packetNumber`; it just bumps `_receivedCount`. `IsComplete` compares the count against `LastPacketNumber+1` without verifying the key set. A sender delivering packets `0, 1, 999` with `LastPacketNumber=2` marks the stream complete while a real packet is missing; downstream `Read` silently returns truncated bytes. [r3, r4]

- [ ] **`IRequestReplyManager` split-brain when user registers a custom impl** — [ServiceCollectionExtensions.cs:63-69](../src/ServiceConnect/ServiceCollectionExtensions.cs#L63-L69)
  `TryAddSingleton<RequestReplyManager>()` registers the concrete type unconditionally. The `IRequestReplyManager` registration is a factory forwarding to the same concrete instance; the `IReplyStatusRequestReplyManager` registration also forwards to the stock concrete type. If the user pre-registered their own `IRequestReplyManager`, outgoing requests use their impl while reply tracking still goes through the stock `RequestReplyManager` — replies silently dropped. XML comment claims "fails fast"; it does not. [r4]

### Interfaces

- [ ] **Sync `Execute` on `Aggregator<T>` forces sync-over-async** — [Aggregator.cs:35](../src/ServiceConnect.Interfaces/Aggregation/Aggregator.cs#L35)
  `abstract void Execute(IList<T>)` is synchronous but implementations need async bus/persistence calls, so every implementor must `.Result` / `.GetAwaiter().GetResult()` — standard sync-over-async deadlock footgun. Breaking-change fix (`Task ExecuteAsync`) appropriate on a v7 branch. [r3]

- [ ] **Sync `Execute` on `IStreamHandler<T>`** — [IStreamHandler.cs:18](../src/ServiceConnect.Interfaces/Handlers/IStreamHandler.cs#L18)
  Same class of bug — realistic stream handlers must read the reassembled `IMessageBusReadStream` asynchronously. [r3]

### MongoDB persistence

- [ ] **Lease-aware Remove/Release don't inspect result counts → duplicate delivery** — [MongoDbTimeoutStore.cs:250-268, 271-292](../src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs#L250-L292)
  `DeleteOneAsync`/`UpdateOneAsync` results are discarded. If the reaper unlocked the row after this owner's lease expired (and another worker re-claimed it), the filter on `LockedBy==lockOwner` matches zero rows; the caller sees "success" with no signal to stop. The row is still present and is redispatched → duplicate delivery. [r3]

- [ ] **`GuidRepresentationMode` V2→V3 toggle silently swallowed → filters match zero documents** — [MongoDbPersistenceExtensions.cs:32-39](../src/ServiceConnect.Persistence.MongoDb/MongoDbPersistenceExtensions.cs#L32-L39)
  `BsonDefaults.GuidRepresentationMode = V3` throws `InvalidOperationException` if another component has already set V2 or if serialization has begun; the catch discards silently. Stored Guids then use CSharpLegacy subtype 3 while filter literals are built with Standard subtype 4 semantics → every Guid filter returns zero documents. Only bites processes sharing the driver with legacy code; severity is high when it hits. [r3]

### InMemory persistence

- [ ] **InMemory has no lease safety → duplicate dispatch after lease expiry** — [InMemoryTimeoutStore.cs:9, 139-183](../src/ServiceConnect.Persistence.InMemory/InMemoryTimeoutStore.cs)
  Does not implement `ILeaseAwareTimeoutStore`. Id-only Remove/Release overloads remove/release unconditionally without checking `LockedBy`. `ProcessManagerTimeoutService` falls through to these because the cast to `ILeaseAwareTimeoutStore` returns null. A worker completing work after its lease was stolen deletes or unlocks the row currently leased to another worker. Parity gap with Mongo. [r3]

## Medium

### Core

- [ ] **`MessageType`/`CorrelationId` in headers are caller-overridable** — [Bus.cs:442-465, 510-533](../src/ServiceConnect/Bus.cs#L442-L533)
  `CreateEnvelope`/`BuildHeadersDirect` set `MessageType` and `CorrelationId` first, then iterate caller `options.Headers` (which can overwrite either), and only `MessageId` is stamped last. The recent "move MessageId authority to Bus" commit enforced spoof-proofing for MessageId only; the other two are still spoofable via `options.Headers`. [r4]

- [ ] **`StopConsumingCoreAsync` sets `_stopped = true` even if consumption never started** — [Bus.cs:371-383](../src/ServiceConnect/Bus.cs#L371-L383)
  Lock body unconditionally assigns `_stopped = true` regardless of `_consuming`. A host that observes a startup error and calls `StopConsumingAsync` defensively can no longer call `StartConsumingAsync` — throws "bus has been stopped" even though nothing started. Forces users to discard the Bus instance. [r4]

- [ ] **`SemaphoreSlim` can be disposed while a lifecycle caller is about to wait on it** — [Bus.cs:403-416](../src/ServiceConnect/Bus.cs#L403-L416)
  `DisposeAsync` sets `_disposed`, awaits `StopConsumingCoreAsync`, then disposes `_lifecycleSemaphore`. A `StartConsumingAsync` that passed `ThrowIfDisposed()` before `_disposed` flipped will call `WaitAsync` on a disposed semaphore → `ObjectDisposedException` on an apparently clean shutdown. [r1, r4]

- [ ] **Missing `OperationCanceledException` handling in `StopConsumingCoreAsync`** — [Bus.cs:386-393](../src/ServiceConnect/Bus.cs#L386-L393)
  `WaitAsync(_disposeTimeout, cancellationToken)` catches only `TimeoutException`. Host cancellation during shutdown throws OCE out of the try-catch, abandoning the in-flight consumer dispose and leaving resources unreleased. (Public `StopConsumingAsync` path only — `DisposeAsync` passes `default`.) [r1]

- [ ] **`MessageDispatcher` reports `Success=false` for untracked replies → retry/DLQ noise** — [MessageDispatcher.cs:131-142](../src/ServiceConnect/Services/MessageDispatcher.cs#L131-L142)
  Stale/late replies (requester timed out, duplicates) return `Success=false` with `InvalidOperationException`. The transport treats this as a dispatch failure → nack/requeue/DLQ cycle for what should be a benign discard. High-frequency in long-running systems with request timeouts. [r3]

- [ ] **`ScanAssemblies(...)` is ignored when `ScanForMessageHandlers=false`** — [ServiceCollectionExtensions.cs:228-237](../src/ServiceConnect/ServiceCollectionExtensions.cs#L228-L237)
  `ScanAssembliesList` is documented as the preferred explicit registration path, but the code short-circuits to `[]` when `ScanForMessageHandlers=false`. Configurations intending "scan these and nothing else" silently register zero handlers; first message hits "Unregistered message type" and DLQs. [r4]

- [ ] **Handler singleton guard misses factory-registered singletons** — [ServiceCollectionExtensions.cs:247-254](../src/ServiceConnect/ServiceCollectionExtensions.cs#L247-L254)
  Guard only checks `ImplementationType`; descriptors with only `ImplementationFactory` slip through. `TryAddEnumerable(Transient)` then adds a second descriptor, so `GetServices<IMessageHandler<T>>()` returns both. Per-message `IConsumeContext` state on the singleton handler is shared across messages — the precise race the guard was meant to prevent. [r4]

### RabbitMQ client

- [ ] **DisposeAsync leaks connection/channel on lock-acquire timeout** — [Producer.cs:395-411](../src/ServiceConnect.Client.RabbitMQ/Producer.cs#L395-L411)
  If the 30-second wait for `_publishLock` or `_connectionSemaphore` times out, `DisposeAsync` returns without tearing down the channel or connection. No finalizer, so a stuck publish leaves a zombie TCP connection for the lifetime of the process. [r3]

- [ ] **Audit publish uses queue name as exchange, silently drops on non-empty routing key** — [MessageAuditPublisher.cs:39-45](../src/ServiceConnect.Client.RabbitMQ/MessageAuditPublisher.cs#L39-L45)
  The utility topology binds the audit queue to the audit direct-exchange with an empty routing key. `MessageAuditPublisher` publishes with `AuditRoutingKey ?? ""`. Any non-empty `AuditRoutingKey` is unroutable and silently dropped (`mandatory:false`). [r1, r3]

- [ ] **RetryCount header not decoded before parsing → retries broken for interop** — [MessageRetryHandler.cs:39-55](../src/ServiceConnect.Client.RabbitMQ/MessageRetryHandler.cs#L39-L55)
  If the header arrives as `byte[]` (any sender stamping it as an AMQP string, e.g. non-.NET clients), `raw.ToString()` returns `"System.Byte[]"`, `int.TryParse` fails, `candidate=-1`, message routes straight to error with no retry. Native C# producers stamp `int`, so the issue is interop-only. Fix: run `HeaderDecoder.Decode` on `raw` before parsing. [r3]

- [ ] **`Consumer.DisposeAsync` only swallows `ObjectDisposedException`** — [Consumer.cs:181-185](../src/ServiceConnect.Client.RabbitMQ/Consumer.cs#L181-L185)
  Any other exception (timeout, `OperationInterruptedException`, broker errors) aborts the `foreach` and leaks the remaining consumer hosts and the setup-channel cleanup below. [r4]

- [ ] **`Consumer.StartConsumingAsync` is not idempotent — `_clients` bag never cleared** — [Consumer.cs:78-174](../src/ServiceConnect.Client.RabbitMQ/Consumer.cs#L78-L174)
  Public API: a second direct call doubles consumers on the same queue, multiplies prefetch, and leaks the original hosts until process exit. `Bus`-level callers are gated by `_consuming`/`_stopped`, so the risk is to direct `IConsumer` consumers only. [r3]

- [ ] **No subscription to broker-initiated `basic.cancel` / connection events** — [RabbitMqConsumerHost.cs:134-137](../src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs#L134-L137)
  `AsyncEventingBasicConsumer` exposes cancel-notification events and `IConnection` exposes `ConnectionBlockedAsync` / `ConnectionShutdownAsync` / `ChannelShutdownAsync`; none are subscribed. Broker deletes the queue or applies a memory-pressure block and consumption silently stalls while the connection still reports "open". [r3, r4]

- [ ] **Null-valued `TypeName` header survives admission but crashes dispatch** — [RabbitMqConsumerHost.cs:166-168, 328-329](../src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs)
  Admission check uses `args.BasicProperties.Headers.ContainsKey(TypeName)` — admits a key with a null value. `CopyInboundHeaders` skips null values, so the dispatch-site indexer `headers[TypeName]` throws `KeyNotFoundException`, caught by the outer `catch`, routing the message to retry/error. Burns retry budget instead of rejecting at admission. [r4]

### MongoDB persistence

- [ ] **ProcessManagerFinder index marker flipped before `CreateOneAsync` awaits** — [MongoDbProcessManagerFinder.cs:274-290](../src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs#L274-L290)
  `_indexedCollections.TryAdd(name, true)` wins before the unique-index creation returns. Concurrent thread B short-circuits on the marker and proceeds to `InsertOneAsync` while the unique index doesn't yet exist → two rows with the same CorrelationId can land before the index is enforced. [r2, r3]

- [ ] **ProcessManagerFinder does not handle MongoCommandException 85/86 on concurrent index creation** — [MongoDbProcessManagerFinder.cs:274-289](../src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs#L274-L289)
  Bare catch rethrows every index-creation error. `MongoDbTimeoutStore:386-392` discriminates `IndexOptionsConflict`/`IndexKeySpecsConflict` as success. Parity gap causes spurious first-insert failures on multi-process startup. [r4]

- [ ] **`UpdateDataAsync` silently swallows `w:0` conflicts; `DeleteDataAsync` spuriously throws** — [MongoDbProcessManagerFinder.cs:211-221, 259-271](../src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs)
  Update checks `IsAcknowledged && ModifiedCount==0` — under `w:0` (IsAcknowledged=false), `ConcurrencyException` is never thrown and `Version` still bumps on the caller's instance. Delete checks only `DeletedCount==0`, which is always zero under `w:0` → always throws. Only bites users explicitly running `w:0`, but asymmetry is real. [r4]

### InMemory persistence

- [ ] **`Provider.Keys()` races with external `IKeyValueStore` callers → NRE** — [InMemoryProcessManagerFinder.cs:93-105](../src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs#L93-L105)
  The `CacheProvider` backing the finder is also registered as `IKeyValueStore`. Finder iterates `Provider.Keys()` under the finder's read lock; external KV callers are not gated by that lock. A removal between the `Keys()` snapshot and `Get(...)` makes `Get` return null, then the fallback at line 105 calls `value.GetType()` → NRE. [r3, r4]

### Telemetry

- [ ] **`Send` skips trace-context injection when `Message` is null** — [ServiceConnectActivitySource.cs:169-176](../src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L169-L176)
  `Send` returns early on `eventArgs.Message is null` before calling `InjectTraceContext`. `Publish` injects unconditionally. Payload-less sends lose W3C `traceparent` propagation — distributed traces break at the broker boundary. [r4]

- [ ] **Trace context not injected when `EnablePublishTelemetry`/`EnableSendTelemetry=false`** — [ServiceConnectActivitySource.cs:56, 156, 239-243](../src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L239-L243)
  When telemetry is disabled, `StartActivity` returns null and the method returns before `InjectTraceContext`. Any ambient `Activity.Current` (ASP.NET etc.) never propagates across the broker. Users who disable ServiceConnect's own spans but still rely on an outer OTel scope silently lose cross-broker trace linkage. [r3]

- [ ] **Activity status never set on failure → error dashboards useless** — [ServiceConnectActivitySource.cs:46-181](../src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L46-L181)
  `Publish`/`Send`/`Consume` start and tag activities but never call `SetStatus(Error)` or `AddException`; no caller sets status either. Failed operations render as `Unset` (appears as `Ok` in most OTel backends), so error-rate/alerting queries built on `status.code` return zero regardless of how often the bus throws. [r1]

### Interfaces

- [ ] **`IMessageTypeRegistry.TryResolve` has non-nullable `out Type`** — [IMessageTypeRegistry.cs:14](../src/ServiceConnect.Interfaces/Messages/IMessageTypeRegistry.cs#L14)
  Implementation uses `types.TryGetValue(out type!)`, so `type` is actually `null` on `false`. Core callers pre-declare `Type? type = null` and happen to be safe; any external consumer trusting the declared signature will NRE. Fix with `[MaybeNullWhen(false)] out Type? type`. [r3]

## Low

### Core

- [ ] **`MessageTypeRegistry.TryResolve` CAS races with concurrent `Register` invalidation** — [MessageTypeRegistry.cs:16-45](../src/ServiceConnect/Services/MessageTypeRegistry.cs#L16-L45). Resolver may publish a stale frozen snapshot lacking a just-registered type; the type is then permanently unresolvable until the next `Register`. Startup-only in the default flow, reachable only if a user calls `Register` dynamically at runtime. [r3, r4]
- [ ] **`ProcessManagerProcessor.ConfigureMapper` may fire twice under contention** — [ProcessManagerProcessor.cs:56-61](../src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs#L56-L61). `ConcurrentDictionary.GetOrAdd` factory is not exactly-once; any side effect in `ConfigureMapper` runs twice on concurrent first-dispatch. [r3]
- [ ] **`HandlerScanner` silently swallows `ReflectionTypeLoadException`** — [HandlerScanner.cs:27-28](../src/ServiceConnect/Services/HandlerScanner.cs#L27-L28). Broken assembly yields partial scan with no startup warning; handler missing → "no handler for message" at runtime instead of a loud startup failure. [r3]
- [ ] **`ProcessManagerTimeoutService.DisposeAsync` doesn't `Interlocked.Exchange` `_cts`** — [ProcessManagerTimeoutService.cs:163-173](../src/ServiceConnect/Services/ProcessManagerTimeoutService.cs#L163-L173). Racy `StopAsync + DisposeAsync` pair can double-Dispose the CTS. [r4]
- [ ] **`AggregatorProcessor.DisposeAsync` flush-lock leak via late timer callbacks** — [AggregatorProcessor.cs:139, 190-225](../src/ServiceConnect/Services/Processors/AggregatorProcessor.cs). Bounded resource leak; timer-side semaphore waits fail fast via `_disposeCts.Token`. [r1, r3]

### RabbitMQ client

- [ ] **Field-initialised `CancellationTokenSource` instances leak when replaced in `StartConsumingAsync`** — [RabbitMqConsumerHost.cs:51, 56, 127-128](../src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs#L51-L128). One-shot leak per host on the first start; fresh leak per restart. [r4]
- [ ] **Direct cast of client-settings `Arguments` to `Dictionary<string, object?>`** — [Consumer.cs:57-59](../src/ServiceConnect.Client.RabbitMQ/Consumer.cs#L57-L59), [RabbitMqConsumerHost.cs:92-94](../src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs#L92-L94). Config-time `InvalidCastException` for any non-`Dictionary<,>` concrete type (e.g. `ReadOnlyDictionary`, `SortedDictionary`). [r3]

### MongoDB persistence

- [ ] **AggregatorPersistor bare catch in index creation** — [MongoDbAggregatorPersistor.cs:236-240](../src/ServiceConnect.Persistence.MongoDb/MongoDbAggregatorPersistor.cs#L236-L240). No 85/86 discrimination; first-insert spurious failure on multi-process startup. [r2]
- [ ] **Index creation ignores `CancellationToken`** across Aggregator/Finder/TimeoutStore. A shutting-down host can't interrupt index creation if the broker stalls. [r1]
- [ ] **AggregatorPersistor `RemoveDataAsync` does not validate `DeleteOneAsync` result** — [MongoDbAggregatorPersistor.cs:140-157](../src/ServiceConnect.Persistence.MongoDb/MongoDbAggregatorPersistor.cs). No-op delete (wrong name or missing CorrelationId) returns silently; caller assumes removal succeeded. [r2]

### InMemory persistence

- [ ] **`CacheProvider.Keys<TKey>()` exact-type match loses covariance** — [CacheProvider.cs:138-145](../src/ServiceConnect.Persistence.InMemory/CacheProvider.cs#L138-L145). `Keys<object>()` returns empty; any request by a base/interface type is dropped. Surprising public-API behaviour. [r3]
- [ ] **`PredicateCacheKey` ctor has no null guard on `propertiesHierarchy`** — [ProcessManagerPredicateCache.cs:24-47](../src/ServiceConnect.Persistence.InMemory/ProcessManagerPredicateCache.cs#L24-L47). Null hierarchy triggers NRE inside `Equals`/`GetHashCode` on the hot path instead of a clear `ArgumentNullException`. [r1]
- [ ] **`DeepClone` loses polymorphic subclass data inside collections** — [DeepClone.cs:14-32](../src/ServiceConnect.Persistence.InMemory/DeepClone.cs). Top-level object round-trips fine (runtime type passed explicitly); members inside collections or base-typed fields serialize at the declared type under `TypeNameHandling=None` → subclass data silently lost. Bites sagas with polymorphic state. [r4]

### Telemetry

- [ ] **`SendEventArgs.EndPoints` (plural) never tagged on span** — [ServiceConnectActivitySource.cs:158-167](../src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L158-L167). Multi-destination sends misreport destination; only the first endpoint is reflected. [r4]

## Uncertain — reproduce with a test before acting

Each entry names the specific test that would clinch the finding. These are kept because the potential impact is high enough that verification is worth the effort.

- [-] disconfirmed by MongoDbTimeoutStoreFacetTests.GetTimeoutsBatchAsync_ReturnsDueTimeoutsFromFacet **Mongo `AggregateFacetResult<T>` pattern-match may fail under the pinned driver** — [MongoDbTimeoutStore.cs:167-182](../src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs#L167-L182)
  If the driver returns `AggregateFacetResult<BsonDocument>` (or a non-generic carrier) instead of the typed `AggregateFacetResult<TimeoutData>` / `<NextTimeoutProjection>`, both branches silently fail and `DueTimeouts` is always empty → total timeout-dispatch outage on Mongo.
  **Test to write (end-to-end):** `tests/ServiceConnect.EndToEndTests` against a real MongoDB via Testcontainers (`sg docker -c`). Insert a `TimeoutData` row with `Time <= utcNow`, call `GetTimeoutsBatchAsync`, assert the result contains the inserted row. Existing unit tests use Moq and don't exercise the real driver. [r4]

- [ ] **`AggregatorProcessor` timer-fired flush accesses disposed `_disposeCts`** — [AggregatorProcessor.cs:190-225](../src/ServiceConnect/Services/Processors/AggregatorProcessor.cs#L190-L225)
  If a timer callback fires after `DisposeAsync` snapshots `_activeFlushes` but before `_disposeCts` is disposed, its `RunFlushAsync` accesses `_disposeCts.Token` on a disposed CTS → `ObjectDisposedException`. Narrow window.
  **Test to write (unit):** use `FakeTimeProvider` in `ServiceConnect.UnitTests/Services/Processors/AggregatorProcessorTests`. Arrange a timer that fires between the `_activeFlushes` snapshot and the `_disposeCts.Dispose()` call in `DisposeAsync` (manual synchronisation via `TaskCompletionSource` handshake), assert no unhandled `ObjectDisposedException` escapes. [r3]