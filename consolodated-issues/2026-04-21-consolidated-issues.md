---
name: Consolidated src issues — re-verified against current v7-clean-architecture
description: Final list of real issues after re-verifying both prior verified-issues files against current source. Merged, deduplicated, severity-banded.
type: review
---

**Progress:** Critical 3/12 · High 1/20 · Medium 0/23 · Low 0/7  (updated 2026-04-21)

# Consolidated `src` Issues (final)

**Date:** 2026-04-21
**Branch:** `v7-clean-architecture`
**Inputs:**
- `verified-issues/2026-04-21-verified-issues-claude.md` (C1–C14, H1–H18, M1–M22, L1–L10 + PARTIAL/REJECTED tables)
- `verified-issues/2026-04-21-verified-issues.md` (42 numbered issues)

**Method:**
1. Merged and deduplicated issues across both files.
2. Dispatched 5 parallel `Explore` (sonnet) verification agents — one per code area (RabbitMQ transport, Bus/Dispatcher/Pipeline/Telemetry, MongoDb persistence, InMemory persistence, Processors/Streams/Misc).
3. Each agent independently read current source at cited lines and returned VERIFIED / PARTIAL / REJECTED / STALE-LINES verdicts with fresh file:line evidence.
4. Kept only VERIFIED + PARTIAL items in the main list below. REJECTED items are listed separately for audit.

Severity bands:
- **Critical** — silent data loss / corruption / trust-boundary violations on the golden path.
- **High** — reliability/correctness bugs that will bite in production.
- **Medium** — bugs on less-common paths, adversarial callers, or misconfigured inputs.
- **Low** — hardening items, API-shape problems, minor lifecycle issues.

---

## Critical

### [x] C1. Reserved transport headers are caller-overridable
- **File:** [Producer.cs:466-483](../src/ServiceConnect.Client.RabbitMQ/Producer.cs#L466-L483)
- **What:** `GetHeaders()` writes caller-supplied headers first via `result[kvp.Key] = kvp.Value`, then `TryAdd`s internal protocol headers (`DestinationAddress`, `MessageId`, `MessageType`, `TypeName`, `FullTypeName`). A caller can pre-seed any reserved key and win — breaking dispatch, reply routing, audit, and type resolution.
- **Fix:** Overwrite reserved keys unconditionally (or reject).

### [ ] C2. `HeaderDecoder.Decode` throws on unexpected AMQP header types → infinite redelivery loop
- **File:** [HeaderDecoder.cs:22-24](../src/ServiceConnect.Interfaces/Headers/HeaderDecoder.cs#L22-L24), callers at [ReplyProcessor.cs:22](../src/ServiceConnect/Services/Processors/ReplyProcessor.cs#L22), [StreamProcessor.cs:68,92,105](../src/ServiceConnect/Services/Processors/StreamProcessor.cs)
- **What:** Exactly `byte[]` and `string` supported; anything else throws `ArgumentException`. Call sites do not catch; outer `RabbitMqConsumerHost` catch nacks with `requeue:true` → infinite redelivery from a single oddly-typed header (interop `long`, `IDictionary`, short-string).
- **Fix:** Return null / `ToString()` fallback; log once.

### [ ] C3. `DefaultProcessManagerPropertyMapper.ConfigureMapping` silently accepts unsupported expression shapes
- **File:** [DefaultProcessManagerPropertyMapper.cs:18-24](../src/ServiceConnect/Services/Processors/DefaultProcessManagerPropertyMapper.cs#L18-L24)
- **What:** Only `UnaryExpression{MemberExpression}` and top-level `MemberExpression` are matched. Nested access (`x => x.Customer.Id`), method calls, coalescing fall through with `propertiesHierarchy` empty but the mapping is still stored. Predicate becomes reference-equality and never matches — saga silently re-initialises on every correlated message.
- **Fix:** Throw `ArgumentException` at configuration time for unsupported shapes; support nested chains if intended.

### [ ] C4. Audit publish failure turns a successful handler into a redelivery
- **File:** [RabbitMqConsumerHost.cs:340-348](../src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs#L340-L348), [MessageAuditPublisher.cs:39](../src/ServiceConnect.Client.RabbitMQ/MessageAuditPublisher.cs#L39)
- **What:** After handler success, `PublishAuditIfEnabledAsync` runs before ack. If audit publish throws, `processed` stays false, the original message is nacked with `requeue:true`, and the already-committed handler runs again.
- **Fix:** Swallow/log audit errors after successful handling; never fail delivery on an audit side-effect.

### [x] C5. `Type.GetType(String)` fallback on wire `FullTypeName` header (reply path)
- **File:** [MessageDispatcher.cs:96-105](../src/ServiceConnect/Services/MessageDispatcher.cs#L96-L105)
- **What:** Dispatcher resolves type via `_typeRegistry.TryResolve(fullTypeName)` first, but falls back to `Type.GetType(fullTypeName)` for reply traffic when not locally registered. Combined with C1 (header spoofing), this remains an untrusted-type-load path on replies.
- **Fix:** Resolve via a registered-handlers whitelist for replies too; do not parse arbitrary assembly-qualified names from the wire.

### [ ] C6. `NotHandled` sets `Success = true` — dispatcher silently acks unhandled messages
- **File:** [MessageDispatcher.cs:167-176](../src/ServiceConnect/Services/MessageDispatcher.cs#L167-L176)
- **What:** `RunProcessors` logs "No processor handled message" but returns `new ConsumeEventResult { Success = true }`. A deliberately rejected / unhandled message is indistinguishable from a handled one — no audit trail, no DLQ, no retry.
- **Fix:** Make `NotHandled` its own state; ack with explicit "skipped" trace or nack to DLQ based on config.

### [ ] C7. Active in-memory process managers expire at 2 days even while being updated
- **File:** [InMemoryProcessManagerFinder.cs:36,196](../src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs#L36), [CacheProvider.cs:155-163](../src/ServiceConnect.Persistence.InMemory/CacheProvider.cs#L155-L163)
- **What:** Inserts set an absolute 2-day expiry (`ExpiryDuration = TimeSpan.FromDays(2)`); `CacheProvider.Update()` only swaps the value — timer and sliding state untouched. Long-running sagas vanish after 48h; next correlated message starts a new instance.
- **Fix:** Remove absolute expiry on saga state, make it configurable, or refresh expiry on every write.

### [ ] C8. In-memory aggregator buffers expire on the same 2-day absolute timer
- **File:** [InMemoryAggregatorPersistor.cs:28,172](../src/ServiceConnect.Persistence.InMemory/InMemoryAggregatorPersistor.cs#L28)
- **What:** Same root cause as C7 for aggregators — absolute 2-day expiry set once; `InsertDataAsync` appends without refresh. Slow or long-lived aggregators lose in-flight messages after 48h.
- **Fix:** Same — configurable / refresh-on-write.

### [x] C9. Reply trust can be forged from inbound headers
- **File:** [ConsumeContext.cs:106-126,149-171](../src/ServiceConnect/Services/ConsumeContext.cs#L106-L171)
- **What:** `IsTrustedRequestReplyEnvelope` derives trust from attacker-controlled inbound headers (`RequestMessageId`, `SourceAddress`, `DestinationAddress`, `MessageId`) and compares `destinationAddress == queueConfig.QueueName`. `ValidateReplyDestinations` (defaults true) narrows the blast radius but the trust decision still rests on caller-controlled values. Combined with C1, a malicious publisher can redirect replies.
- **Fix:** Tie reply routing to a verified producer identity; reject reply headers that name queues the bus is not configured to talk to.

### [ ] C10. Process-manager delete bypasses optimistic concurrency (both backends)
- **File:** [MongoDbProcessManagerFinder.cs:240-241](../src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs#L240-L241), [InMemoryProcessManagerFinder.cs:285-286](../src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs#L285-L286)
- **What:** Both `DeleteDataAsync` implementations filter by `CorrelationId` only, with no `Version` predicate, unlike `UpdateDataAsync` which ANDs `Version`. A concurrent delete racing with an update loses the optimistic-concurrency contract — the saga can vanish mid-update.
- **Fix:** Delete with `{CorrelationId, Version}`; throw `ConcurrencyException` on 0-affected.

### [ ] C11. `traceparent` is never injected into outgoing messages — distributed tracing is dark outbound
- **File:** [ServiceConnectActivitySource.cs:46-54,140-148](../src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs), [Producer.cs:466-483](../src/ServiceConnect.Client.RabbitMQ/Producer.cs#L466-L483)
- **What:** `Publish()` and `Send()` start activities but never call `DistributedContextPropagator.Current.Inject` into outbound headers. Consumers call `ExtractTraceIdAndState` and find nothing — every consume span is a new root; trace graph broken end-to-end.
- **Fix:** Inject on the outgoing telemetry subscriber (mirroring the extract on consume).

### [ ] C12. Outbound paths do not stamp the `CorrelationId` header
- **File:** [Bus.cs:468-483](../src/ServiceConnect/Bus.cs#L468-L483), [Producer.cs:466-483](../src/ServiceConnect.Client.RabbitMQ/Producer.cs#L466-L483), [ConsumeContext.cs:93-95](../src/ServiceConnect/Services/ConsumeContext.cs#L93-L95)
- **What:** `BuildHeadersDirect` writes message-type headers but never `HeaderKeys.CorrelationId`. Consume side derives `Context.CorrelationId` from the header with `Guid.Empty` fallback. Handlers see `message.CorrelationId` in body but `Context.CorrelationId == Guid.Empty` — public-API contract mismatch.
- **Fix:** Populate the header from `message.CorrelationId` at send time.

---

## High

### [ ] H1. Retry, error, and audit helper publishes go through a channel with no publisher confirms
- **File:** [RabbitMqConsumerHost.cs:102-106](../src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs#L102-L106), [MessageRetryHandler.cs:51-56,102-106](../src/ServiceConnect.Client.RabbitMQ/MessageRetryHandler.cs), [MessageAuditPublisher.cs:38-39](../src/ServiceConnect.Client.RabbitMQ/MessageAuditPublisher.cs#L38-L39)
- **What:** `_publishChannel` is a dedicated helper channel created with defaults — `PublisherConfirmationsEnabled` not set. If the helper publish is lost mid-flight and is treated as complete, the retry/error/audit copy silently disappears. (Original message ack is sequenced after the publish await, so this is narrower than the "ack before confirm" framing but the publisher-confirm gap is real.)
- **Fix:** Enable publisher confirms on the helper channel (matching the main producer channel) and await the confirm.

### [ ] H2. `MessageDispatcher.Dispatch` ignores its public `messageType` parameter and hard-requires `FullTypeName` header
- **File:** [MessageDispatcher.cs:61-138](../src/ServiceConnect/Services/MessageDispatcher.cs#L61-L138)
- **What:** The public `IMessageDispatcher` contract takes `Type messageType` separately, but the implementation uses it only in the exception log path; all type resolution reads the `HeaderKeys.FullTypeName` header. A transport that honours the public contract still fails at runtime.
- **Fix:** Use the supplied `messageType`; treat the header as tie-break.

### [ ] H3. Inbound middleware resolves from the root `IServiceProvider` and the chain is cached
- **File:** [MessageDispatcher.cs:26-57,179-192](../src/ServiceConnect/Services/MessageDispatcher.cs), [ServiceCollectionExtensions.cs:41,150-163](../src/ServiceConnect/ServiceCollectionExtensions.cs)
- **What:** `BuildProcessingChain()` resolves middleware from the root `_serviceProvider` and is wrapped in `Lazy<>`, so a transient middleware instance is pinned for the bus lifetime. `ValidateSendMessageMiddlewareLifetimes` only validates the send pipeline — no equivalent check for `MessageProcessingMiddleware`.
- **Fix:** Resolve per-message from a scoped provider; extend startup validation to the inbound side.

### [ ] H4. `FilterPipeline` resolves filters from the root provider on every execution
- **File:** [FilterPipeline.cs:10,45-53](../src/ServiceConnect/Services/FilterPipeline.cs), [ServiceCollectionExtensions.cs:61](../src/ServiceConnect/ServiceCollectionExtensions.cs#L61)
- **What:** `FilterPipeline` registered as singleton; `ExecuteFiltersAsync` calls `serviceProvider.GetRequiredService(filterType)` against the root-captured provider. Scoped filters or filters depending on scoped services silently behave as root-scoped. No startup guard.
- **Fix:** Scope-aware resolution + startup validation.

### [ ] H5. Handler double-registration on pre-existing transient/scoped handlers
- **File:** [ServiceCollectionExtensions.cs:208-231](../src/ServiceConnect/ServiceCollectionExtensions.cs#L208-L231), [HandlerProcessor.cs:39-44](../src/ServiceConnect/Services/Processors/HandlerProcessor.cs#L39-L44)
- **What:** `RegisterHandlerType` guards only against existing singleton registrations. If the app pre-registered the handler transient or scoped, `AddTransient` adds a second registration and `HandlerProcessor.GetServices(...)` resolves + invokes both — one message handled twice.
- **Fix:** Skip if *any* registration exists for the closed handler interface, or use `TryAddEnumerable` with descriptor equality.

### [ ] H6. `ReplyProcessor` / `StreamProcessor` bypass inbound filters and middleware
- **File:** [MessageDispatcher.cs:80-92,133-134](../src/ServiceConnect/Services/MessageDispatcher.cs), [ReplyProcessor.cs:11-33](../src/ServiceConnect/Services/Processors/ReplyProcessor.cs), [StreamProcessor.cs:57-155](../src/ServiceConnect/Services/Processors/StreamProcessor.cs)
- **What:** Both processors set `RunBeforeDeserialization = true`; dispatcher runs the before-deser path before `ExecuteBeforeConsumingFiltersAsync`, and only post-deserialization dispatch is wrapped by `IMessageProcessingMiddleware`. Replies skip all middleware; completed stream messages skip filters + middleware.
- **Fix:** Wrap reply and stream-complete dispatch in the same middleware/filter chain, or document the asymmetry.

### [x] H7. Composite: reserved-header / reply-address spoofing reaches the dispatcher as truth
- **File:** Producer header population (C1) + ConsumeContext reply trust (C9)
- **What:** Even without malicious callers, absence of reserved-header protection means a buggy caller can quietly break reply routing for the whole bus. With C1 + C9 both open the attack surface is full end-to-end.
- **Fix:** Treat reserved headers as server-authoritative; lock reply routing to a verified producer identity.

### [ ] H8. `Producer.SendBytesAsync` does not validate `endPoint`
- **File:** [Producer.cs:328-350](../src/ServiceConnect.Client.RabbitMQ/Producer.cs#L328-L350)
- **What:** `SendAsync(string endPoint, ...)` validates `IsNullOrWhiteSpace(endPoint)` at line 296, but `SendBytesAsync` has no such check — blank endpoints publish to the default exchange with `mandatory:false` and are silently dropped.
- **Fix:** Same validation as `SendAsync`.

### [ ] H9. Consumer startup cancellation doesn't flow into channel/connection create
- **File:** [Consumer.cs:81,155-158](../src/ServiceConnect.Client.RabbitMQ/Consumer.cs), [Connection.cs:65-77](../src/ServiceConnect.Client.RabbitMQ/Connection.cs#L65-L77)
- **What:** `IServiceConnectConnection.CreateChannelAsync()` takes no `CancellationToken`; `StartConsumingAsync(CancellationToken)` passes no token. Broker/DNS/TCP stalls block the caller past the cancellation deadline.
- **Fix:** Plumb the token through.

### [ ] H10. Startup cancellation token is captured and reused for every later delivery callback
- **File:** [RabbitMqConsumerHost.cs:111](../src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs#L111)
- **What:** The `cancellationToken` passed to `StartConsumingAsync` is captured by the `ReceivedAsync` lambda and reused for the lifetime of the consumer. A startup token cancelled post-startup (e.g. scoped to the startup phase) breaks every future delivery.
- **Fix:** Use a consumer-lifetime token; do not capture the startup token.

### [ ] H11. Partial consumer startup leaks already-started hosts
- **File:** [Consumer.cs:142-160](../src/ServiceConnect.Client.RabbitMQ/Consumer.cs#L142-L160)
- **What:** Each host is started (line 155) before being added to `_clients` (line 160). If a later step throws, earlier hosts are running but untracked — `DisposeAsync` won't reach them.
- **Fix:** Add to `_clients` before starting, or track running hosts independently so cleanup sees them.

### [ ] H12. `Consumer` unconditionally disposes an externally-supplied connection
- **File:** [Consumer.cs:37-45,183-184](../src/ServiceConnect.Client.RabbitMQ/Consumer.cs#L37-L184)
- **What:** Constructor accepts an optional shared connection but `DisposeAsync` unconditionally disposes whatever is in `_connection` — including caller-owned instances.
- **Fix:** Track ownership; only dispose connections the Consumer created.

### [ ] H13. Aggregator flush can double-dispatch after cancellation between Execute and RemoveSnapshot
- **File:** [AggregatorProcessor.cs:169,173](../src/ServiceConnect/Services/Processors/AggregatorProcessor.cs#L169-L173)
- **What:** `InvokeExecute(aggregator, typedList)` runs before `RemoveSnapshotAsync(...)`. If cancellation hits between them, the snapshot remains persisted and can be flushed again on the next timer tick — same batch delivered twice.
- **Fix:** Remove-before-execute, or use a single atomic swap.

### [ ] H14. Malformed `RetryCount` header resets the retry budget
- **File:** [MessageRetryHandler.cs:38-47](../src/ServiceConnect.Client.RabbitMQ/MessageRetryHandler.cs#L38-L47)
- **What:** Non-parseable or out-of-range `RetryCount` values yield `candidate = -1`, fail the `>= 0` guard, and leave `retryCount = 0`. The handler then increments and republishes as if the message were on its first retry — a publisher or broker corruption can force infinite retries.
- **Fix:** Treat parse failure as "route to error" (or preserve a sane counter); never silently reset to 0.

### [ ] H16. Process-manager optimistic-concurrency retry replays handler side-effects
- **File:** [ProcessManagerProcessor.cs:74-94,126-158](../src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs#L74-L158)
- **What:** On `ConcurrencyException`, the entire find/invoke/update cycle is retried — so handler code and any side-effects (HTTP calls, enqueued messages, log lines) run again. Comment at lines 28-32 explicitly acknowledges it.
- **Fix:** Split side-effect-free state transitions from side-effecting work; retry only the state write.

### [ ] H17. `InMemoryAggregatorPersistor` owns a `CacheProvider` but is not `IDisposable`
- **File:** [InMemoryAggregatorPersistor.cs:8-21](../src/ServiceConnect.Persistence.InMemory/InMemoryAggregatorPersistor.cs#L8-L21)
- **What:** `CacheProvider` owns `ITimer` registrations and implements `IDisposable`; persistor has no dispose. Every DI rebuild (integration tests, host reload) leaks timers/handles.
- **Fix:** Implement `IAsyncDisposable` or accept `CacheProvider` via DI.

### [ ] H18. `InMemoryPersistenceState.SyncRoot` (ReaderWriterLockSlim) is never disposed
- **File:** [InMemoryPersistenceState.cs:11](../src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceState.cs#L11)
- **What:** RWSL holds kernel handles; no `Dispose`. Per-container leak on every DI rebuild.
- **Fix:** Make the state `IDisposable`.

### [ ] H19. In-memory persistence stores live references — mutation after persist corrupts state
- **File:** [InMemoryProcessManagerFinder.cs:219,261-265](../src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs), [InMemoryAggregatorPersistor.cs:41,57-60,77-80](../src/ServiceConnect.Persistence.InMemory/InMemoryAggregatorPersistor.cs), [CacheProvider.cs:62](../src/ServiceConnect.Persistence.InMemory/CacheProvider.cs#L62)
- **What:** Insert/update bind the caller's object directly. Process-manager reads do `MemberwiseClone()` with deep-copy only for `byte[]` properties — nested collections still alias. Aggregator reads return the raw stored reference with no cloning. `CacheProvider.Get()` returns the raw reference. Callers mutating nested collections mutate stored state.
- **Fix:** Deep-clone via serializer, or document stored types must be immutable.

### [ ] H20. Producer ignores configured heartbeat settings
- **File:** [Producer.cs:115](../src/ServiceConnect.Client.RabbitMQ/Producer.cs#L115), [Connection.cs:47-50](../src/ServiceConnect.Client.RabbitMQ/Connection.cs#L47-L50), [ConnectionFactoryBuilder.cs:21,38-39](../src/ServiceConnect.Client.RabbitMQ/ConnectionFactoryBuilder.cs)
- **What:** `Producer.CreateConnectionAsync` calls `Build(..., heartbeatInterval: null)`. Consumer-side connections correctly resolve `_heartbeatEnabled`/`_heartbeatTime`. Producer connections never honour configured heartbeat.
- **Fix:** Pass the resolved heartbeat settings from `ClientSettings`.

### [ ] H21. MongoDb optimistic-concurrency `Version` increment mutates caller object on some cancellation paths
- **File:** [MongoDbProcessManagerFinder.cs:188,199,205,221](../src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs)
- **What:** `versionData.Version` is incremented to `currentVersion + 1` before `ReplaceOneAsync`. Restore paths exist for `ModifiedCount == 0` and `MongoException`, but other failure paths (out-of-band cancellation between the pre-cancellation guard and an infrastructure failure that surfaces as neither) can still leave the caller with a bumped `Version`. (Scope is narrower than originally claimed — most failure paths do restore.)
- **Fix:** Use a local copy for the write; only update the caller's version on confirmed success.

---

## Medium

### [ ] M1. `SendAsync` silently ignores `SendOptions.EndPoint` when `EndPoints` is also populated
- **File:** [Bus.cs:117-127](../src/ServiceConnect/Bus.cs#L117-L127)
- **What:** If caller sets both `EndPoint` and `EndPoints`, the `foreach` branch is entered and `EndPoint` is silently ignored — no exception, no log.
- **Fix:** Validate-or-merge at the call site.

### [ ] M2. `QueueConfiguration.AddQueueMapping` doesn't validate list elements
- **File:** [QueueConfiguration.cs:48-64,118-132](../src/ServiceConnect/Configuration/QueueConfiguration.cs#L48-L132)
- **What:** The `IList<string>` overload validates the list reference but not individual elements — null/empty/whitespace queue names stored silently, unlike the single-queue overload which guards via `IsNullOrWhiteSpace`.
- **Fix:** Per-element validation.

### [ ] M3. Queue mappings keyed solely on `Type.FullName`
- **File:** [QueueConfiguration.cs:27,40](../src/ServiceConnect/Configuration/QueueConfiguration.cs)
- **What:** `_queueMappings` keyed by `messageType.FullName!`. Two types with the same namespace/name from different assemblies collide into one bucket.
- **Fix:** Key by `AssemblyQualifiedName` or full `Type` identity.

### [ ] M4. `MessageTypeRegistry` silently overwrites same-name registrations
- **File:** [MessageTypeRegistry.cs:30-36](../src/ServiceConnect/Services/MessageTypeRegistry.cs#L30-L36)
- **What:** `Register(Type)` unconditionally assigns `_registeredTypes[AssemblyQualifiedName] = type` and `[FullName] = type`. Later registrations overwrite earlier entries with no warning.
- **Fix:** Detect and fail (or at least log) collisions.

### [ ] M5. Flattened CLR type names can collide in RabbitMQ exchange/binding names
- **File:** [Producer.cs:226](../src/ServiceConnect.Client.RabbitMQ/Producer.cs#L226), [Bus.cs](../src/ServiceConnect/Bus.cs) consumer binding helpers
- **What:** Exchange/binding names are derived from `FullName.Replace(".", string.Empty)`. Two types whose names differ only in dot position collapse to the same transport name — cross-wired routing.
- **Fix:** Use a stronger sanitizer (e.g. hash suffix) or reject collisions.

### [ ] M6. Trace context extraction only handles concrete dictionaries
- **File:** [ServiceConnectActivitySource.cs:193-208](../src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L193-L208), [ConsumeContext.cs:47-48](../src/ServiceConnect/Services/ConsumeContext.cs#L47-L48)
- **What:** `ExtractTraceIdAndState` switches only on `Dictionary<string,object>` / `Dictionary<string,string>`. `ReadOnlyDictionary` (as wrapped by `ConsumeContext`) and other `IDictionary` implementations silently lose `traceparent`/`tracestate`.
- **Fix:** Iterate via the interface.

### [ ] M7. `send` span tagged as `publish` (OTel semconv mismatch)
- **File:** [ServiceConnectActivitySource.cs:140-153](../src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L140-L153)
- **What:** `Send()` starts the activity with operation `"publish"` and display name `"<endpoint> publish"`. OTel messaging semconv distinguishes `publish` (pub/sub) from `send` (point-to-point); backend dashboards mis-aggregate.
- **Fix:** `publish` for `PublishAsync`; `send` for `SendAsync`.

### [ ] M8. `IMessageHandler<T>.Context` typed nullable despite documented non-null
- **File:** [IMessageHandler.cs:15](../src/ServiceConnect.Interfaces/Handlers/IMessageHandler.cs#L15), [IProcessHandler.cs:18](../src/ServiceConnect.Interfaces/ProcessManagers/IProcessHandler.cs#L18)
- **What:** `IConsumeContext? Context { get; set; }` nullable in both interfaces with no framework null-guard before `HandleAsync` is invoked. NRT-aware handlers must sprinkle `!`.
- **Fix:** Commit to non-nullable (with `= null!;` initialiser) or throw in the dispatcher if `Context` is null after `SetContext`.

### [ ] M9. `PublishOptions` is a mutable sealed class
- **File:** [PublishOptions.cs:6-17](../src/ServiceConnect.Interfaces/Options/PublishOptions.cs#L6-L17)
- **What:** Inconsistent with `SendOptions` (`readonly record struct`). Concurrent `PublishAsync` calls sharing an instance can mutate each other's state mid-flight.
- **Fix:** Convert to `readonly record struct`.

### [ ] M10. `SendOptions` / `PublishOptions` `Headers` reference a mutable `Dictionary`
- **File:** [SendOptions.cs:11](../src/ServiceConnect.Interfaces/Options/SendOptions.cs#L11), [PublishOptions.cs:11](../src/ServiceConnect.Interfaces/Options/PublishOptions.cs#L11)
- **What:** `Headers` is `Dictionary<string,string>?` — concurrent caller mutation during `BuildHeadersDirect`'s `foreach` throws `Collection was modified`. Narrower than "always a crash" — requires concurrent caller misuse.
- **Fix:** Type as `IReadOnlyDictionary<string,string>?`; snapshot at construction.

### [ ] M11. `OutgoingEventArgs.Headers` exposes the internal mutable `Dictionary`
- **File:** [OutgoingEventArgs.cs:16-26](../src/ServiceConnect.Interfaces/Bus/OutgoingEventArgs.cs#L16-L26)
- **What:** Public `set` accessor; subscribers can rewrite `MessageId`, `DestinationAddress`, etc., after the event fires but before transport send.
- **Fix:** Expose as read-only or snapshot before raising.

### [ ] M12. Startup validation skips inbound pipelines
- **File:** [ServiceCollectionExtensions.cs:41,150-163](../src/ServiceConnect/ServiceCollectionExtensions.cs#L41-L163)
- **What:** Only `ValidateSendMessageMiddlewareLifetimes` runs. Inbound middleware (H3) and filter (H4) lifetime misconfigurations silently slip through.
- **Fix:** Extend validator; mirror the send-side checks for inbound.

### [ ] M13. Timeout store in-memory: batch-poll skips leased due rows
- **File:** [InMemoryTimeoutStore.cs:71-99](../src/ServiceConnect.Persistence.InMemory/InMemoryTimeoutStore.cs#L71-L99)
- **What:** `GetTimeoutsBatchAsync` iterates the time-sorted index and breaks at the first future entry, skipping currently-leased due rows. `NextQueryTime` is computed from future unlocked entries, potentially delaying re-query past the lease expiry.
- **Fix:** Include leased-due rows in `NextQueryTime` computation (or use `min(lock_expiry, next_future_row)`).

### [ ] M14. Mongo timeout store id-only remove/release overloads ignore lock owner
- **File:** [MongoDbTimeoutStore.cs:173-207](../src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs#L173-L207)
- **What:** Legacy two-arg overloads filter by `Id` + `Locked == true` but not by `LockedBy`. Any caller using them can act on another worker's leased row. Three-arg lockOwner overloads are correctly guarded.
- **Fix:** Remove the legacy overloads or add `LockedBy` to their filter.

### [ ] M15. Mongo timeout polling has no batch limit
- **File:** [MongoDbTimeoutStore.cs:112-117](../src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs#L112-L117)
- **What:** `UpdateManyAsync` claims every due unlocked row for the session, and the `Due` facet reads them all with no `.Limit(...)`. Under load a single poll can claim and return an unbounded set.
- **Fix:** Cap per-poll batch size with `.Limit(...)`.

### [ ] M16. Mongo timeout persistence does not enforce unique timeout IDs
- **File:** [MongoDbTimeoutStore.cs:278-280](../src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs#L278-L280)
- **What:** The `Id` index is created without `Unique = true`.
- **Fix:** Make it unique.

### [ ] M17. Mongo aggregator snapshots returned in non-deterministic order
- **File:** [MongoDbAggregatorPersistor.cs:100](../src/ServiceConnect.Persistence.MongoDb/MongoDbAggregatorPersistor.cs#L100)
- **What:** Snapshot reads use `Find(filter).ToListAsync(cancellationToken)` with no explicit sort before projecting messages — insertion order not guaranteed stable.
- **Fix:** Sort by a deterministic key (e.g. insertion timestamp / sequence).

### [ ] M18. Mongo timeout lease has no background reaper for crashes
- **File:** [MongoDbTimeoutStore.cs:28,110,260-266](../src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs)
- **What:** `LockLeaseDuration` (5 min) + `BuildDueTimeoutFilter` re-claims stale leases on the next `GetTimeoutsBatchAsync` poll. Works but entirely poll-driven — a crashed handler's lease is only recovered when the next poll fires.
- **Fix:** Short TTL on lease + background reaper for liveness guarantees.

### [ ] M19. Mongo TLS protocol and revocation settings skipped without cert path
- **File:** [MongoClientFactory.cs:50-63](../src/ServiceConnect.Persistence.MongoDb/MongoClientFactory.cs#L50-L63)
- **What:** `UseTls` and `AllowInsecureTls` set unconditionally, but `EnabledSslProtocols` and `CheckCertificateRevocation` are applied only inside the `CertPath` block. TLS users without a client cert silently use driver defaults.
- **Fix:** Apply protocol/revocation settings whenever TLS is enabled.

### [ ] M20. Mongo process-manager collection naming uses simple short `Name`
- **File:** [MongoDbProcessManagerFinder.cs:59,122,186,232,252,269-271](../src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs)
- **What:** Reads use `typeof(T).Name`; writes/deletes use `data.GetType().Name`. Both use simple `Name`, not `FullName` — two types with the same short name from different namespaces collide. Secondary inconsistency: if `T` is an interface/base and `data` is a subtype, the two paths disagree on the collection.
- **Fix:** Use `FullName` (or a namespace-qualified identifier) consistently.

### [ ] M21. `CacheProvider.Add` refreshes TTL without replacing value
- **File:** [CacheProvider.cs:184-208](../src/ServiceConnect.Persistence.InMemory/CacheProvider.cs#L184-L208)
- **What:** `_cache.TryAdd` keeps the original value on duplicate key, but `StartObserving` unconditionally installs a fresh timer. Re-add extends the TTL of the stale value.
- **Fix:** Either refuse duplicate add, or replace-and-reset atomically.

### [ ] M22. `CacheProvider.Remove` fires `KeyRemoved` when key was absent; `Clear`/`PurgeNormalPriorities` don't fire it
- **File:** [CacheProvider.cs:68-77,83-93,125-140](../src/ServiceConnect.Persistence.InMemory/CacheProvider.cs)
- **What:** `Remove` invokes `KeyRemoved` even when `TryRemove` returns false. `Clear` and `PurgeNormalPriorities` remove entries without invoking the event at all. Inconsistent semantics for subscribers.
- **Fix:** Fire only on actual removal; fire consistently across all paths.

### [ ] M23. Prefetch value cast assumes boxed `int`
- **File:** [RabbitMqConsumerHost.cs:77-79](../src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs#L77-L79)
- **What:** `Convert.ToUInt16((int)prefetchVal)` — direct `(int)` unbox cast. Boxed `long`, `ushort`, `string` etc. throw `InvalidCastException`.
- **Fix:** `Convert.ToUInt16(prefetchVal)` (which handles any numeric/string), or gate by type.

---

## Low

### [ ] L1. `ServiceConnectBuilder.ScanAssemblies` doesn't validate per-element nullability
- **File:** [ServiceConnectBuilder.cs:33-37](../src/ServiceConnect/ServiceConnectBuilder.cs#L33-L37)
- **What:** Array null is guarded; a `null` element inside the array slips through and later NREs in `HandlerScanner`.
- **Fix:** Per-element `ArgumentNullException.ThrowIfNull`.

### [ ] L2. `RequestOptions.Default` is a mutable singleton
- **File:** [RequestOptions.cs:16](../src/ServiceConnect.Interfaces/Options/RequestOptions.cs#L16)
- **What:** Static `Default` exposes settable properties; mutating it affects all callers using the fallback.
- **Fix:** Immutable shape (`readonly record struct` or frozen getter).

### [ ] L3. `ReadOnlySequenceStream` / `ReadOnlyMemoryStream` expose `Length` with `CanSeek=false`
- **File:** [ReadOnlyMemoryStream.cs:14-16](../src/ServiceConnect/Services/IO/ReadOnlyMemoryStream.cs), [ReadOnlySequenceStream.cs:17-19](../src/ServiceConnect/Services/IO/ReadOnlySequenceStream.cs)
- **What:** Violates `Stream` contract (`Length` should throw `NotSupportedException` unless `CanSeek`). Consumers branching on `CanSeek` then reading `Length` for progress reporting behave inconsistently.
- **Fix:** Implement `Seek`/`Position`, or throw on `Length`.

### [ ] L4. Sealed library exceptions missing `(string, Exception)` constructors
- **File:** `ServiceConnect.Interfaces.Exceptions.*` (`ConcurrencyException`, `PersistenceException`, `TransportException`, `SerializationException`)
- **What:** Style/shape — violates CA1032. Library-internal impact.
- **Fix:** Add the standard two-arg constructor.

### [ ] L5. `LogDebug` calls in hot paths without `IsEnabled` guards
- **File:** [ProcessManagerProcessor.cs:89](../src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs#L89), [AggregatorRegistry.cs:44](../src/ServiceConnect/Services/Processors/AggregatorRegistry.cs#L44), [StreamHandlerRegistry.cs:40](../src/ServiceConnect/Services/Processors/StreamHandlerRegistry.cs#L40), [MessageHandlerRegistry.cs:41](../src/ServiceConnect/Services/Processors/MessageHandlerRegistry.cs#L41)
- **What:** Allocations for structured-field binding still occur when `Debug` is disabled.
- **Fix:** Guard with `IsEnabled(LogLevel.Debug)` on hot paths.

### [ ] L6. `MessageAuditPublisher` routing key hardcoded
- **File:** [MessageAuditPublisher.cs:39](../src/ServiceConnect.Client.RabbitMQ/MessageAuditPublisher.cs#L39)
- **What:** Exchange name is configurable via `IQueueConfiguration.AuditQueueName`; routing key is hardcoded to `""`, no way to configure a non-default exchange type or binding key.
- **Fix:** Options-bound routing/binding key.

### [ ] L7. `IBus` API inconsistency: `PublishRequestAsync` parameter order
- **File:** [IBus.cs:13-41](../src/ServiceConnect.Interfaces/Bus/IBus.cs#L13-L41)
- **What:** `PublishAsync`/`SendAsync` use `options` as second param; `PublishRequestAsync` places `onReply` callback second, `options` third. Minor ergonomic inconsistency.
- **Fix:** Align on next major.

---

## Partial / narrower-than-reported items (already folded into the list above)

Each is tagged `(scope: …)` where the original review overstated the blast radius:
- **C5** (scope: reply traffic only; other inbound uses the registry)
- **C9** (scope: `ValidateReplyDestinations` defaults on; narrows the bypass)
- **H1** (scope: helper publishes lack confirms, but ack does sequence after the publish await — so the "ack before confirm" framing was wrong)
- **H14** (scope: failure-path behaviour specifically; golden path is fine)
- **H21** (scope: most failure paths restore `Version`; narrow window remains)
- **M10** (scope: requires concurrent caller mutation)
- **M5** (scope: collision pattern is `.→""`, not `.` vs `_`)
- **M20** (scope: simple-`Name` collisions and the reads-vs-writes inconsistency)

---

> **Note:** H15 is intentionally absent from the list — the original review had a numbering gap.

## Recommended fix order

1. **C1 + C5 + C9** (composite **H7**) — trust boundary on reserved headers, reply routing, untrusted type load. Single PR.
2. **C4** — audit publish failure flipping success into redelivery. Quick win.
3. **C2** — `HeaderDecoder` infinite-redelivery loop. Quick win.
4. **C3** — silent saga re-init from unsupported mapping expression.
5. **C6** — `NotHandled` observability.
6. **C7 + C8** — in-memory 2-day absolute expiry on live state.
7. **C10** — process-manager delete concurrency (both backends).
8. **C11, C12** — distributed tracing / correlation ID end-to-end.
9. **H-series** — reliability and lifetime fixes.
10. **M/L** — hardening.
