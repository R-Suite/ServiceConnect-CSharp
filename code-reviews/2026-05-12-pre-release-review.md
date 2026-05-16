# Pre-release code review — v7-clean-architecture

**Date**: 2026-05-12
**Branch**: `v7-clean-architecture` (50 commits ahead of master, 215 files modified WIP)
**Method**: 6 parallel reviewers, each scoped to one subsystem.
**Top-line verdict**: **No Critical findings**. Surface is in good shape after the recent fix burst. There are ~14 Important issues spread across subsystems that should be addressed before release, plus a tail of Minor robustness / documentation gaps. Two themes recur and are worth a session of their own: **mutable state escaping to callers** (configuration, handler list, validator arrays) and **trace/observability correctness** (body size always 0, sampling drops trace continuity, enrichment exception kills the handler).

---

## Important — fix before release

### Telemetry correctness

#### T1. `messaging.message.body.size` always reports 0 unless caller wires an enricher
**File**: [src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs:41-46](src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs#L41-L46) (stamped at [ServiceConnectActivitySource.cs:193](src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L193))
**What**: Middleware materialises an empty `byte[]` for `ConsumeEventArgs.Message` when `EnrichWithMessageBytes is null` (default). `Consume()` then stamps `messaging.message.body.size = eventArgs.Message.Length` → `0`. Real body length is on `envelope.Body.Length` but never threaded through.
**Why it matters**: Standard OTel attribute is wrong for every consume span — breaks payload-size dashboards/SLOs and parity with other OTel messaging instrumentations.
**Fix**: Add `BodySize` to `ConsumeEventArgs` (set unconditionally to `envelope.Body.Length`), stamp the tag from that.

#### T2. Consume sampling silently breaks downstream trace propagation
**File**: [src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs:33-67](src/ServiceConnect.Telemetry/TelemetryProcessingMiddleware.cs#L33-L67)
**What**: The inbound-trace AsyncLocal fallback is in the `else if` branch — only stashed when consume telemetry is **disabled**. When consume telemetry is enabled and the sampler drops a span, `Consume()` returns null and no fallback is stashed. A subsequent `Bus.Publish` becomes a fresh trace root and the cross-broker trace graph breaks at every sampled-out hop.
**Why it matters**: Head-based sampling + consume telemetry on = silent trace discontinuity. Exactly what the fallback was added to prevent.
**Fix**: Always stash the fallback when publish/send telemetry is enabled, regardless of whether `Consume()` returned non-null.

#### T3. Enrichment exception inside `Consume()` blocks handler dispatch
**File**: [src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs:153-203](src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L153-L203) (also Publish / Send)
**What**: After `StartActivityWithParent` succeeds, the `try { … } catch { activity.Dispose(); throw; }` block re-throws on any failure during header decode, truncate, or tag-set. Re-throw escapes `TelemetryProcessingMiddleware.ProcessAsync` *before* `next(...)` is awaited — handler never runs. Worst case: a poison header (malformed AMQP x-table at depth 32) crashes every consumer pulling it.
**Why it matters**: Telemetry is best-effort — a malformed header should produce a degraded span, not block consume.
**Fix**: Wrap post-`StartActivity` enrichment in a catch that stamps `telemetry.exception=...` and returns the activity, instead of rethrowing.

### Configuration / mutable state escaping

#### B1. `IBusConfiguration` is registered by-reference; post-`AddServiceConnect` mutations are silently honoured
**File**: [src/ServiceConnect/DependencyInjection/ServiceCollectionExtensions.cs:79](src/ServiceConnect/DependencyInjection/ServiceCollectionExtensions.cs#L79)
**What**: `TryAddSingleton<IBusConfiguration>(builder.BusConfig)` registers the same mutable instance the builder holds. Any code that still has the builder (a second feature module, an extension method) can mutate `DisposeTimeout`, `MaxRoutingSlipHops`, etc. after AddServiceConnect ran — bypassing the builder validators that prevent runtime AOORE.
**Fix**: Freeze the configuration at the end of `AddServiceConnect` (locked-flag setters throw), or deep-copy into immutable records before registration.

#### B2. Handler-list registered as mutable `IList<HandlerReference>`
**File**: [src/ServiceConnect/DependencyInjection/ServiceCollectionExtensions.cs:147](src/ServiceConnect/DependencyInjection/ServiceCollectionExtensions.cs#L147)
**What**: `Bus` ctor accepts a mutable `IList<HandlerReference>`. Anyone with the singleton can `Add()` post-construction. `StartConsumingAsync` enumerates this list to build the subscription set; a mid-startup mutation expands dispatch without re-running topology declaration → consumer may dispatch a type the broker never bound.
**Fix**: Register `IReadOnlyList<HandlerReference>` or wrap in `ReadOnlyCollection<>` at registration time.

#### B3. `RoutingSlipDestinationValidator.ForbiddenChars` is a public mutable array
**File**: [src/ServiceConnect/Services/RoutingSlipDestinationValidator.cs:17](src/ServiceConnect/Services/RoutingSlipDestinationValidator.cs#L17)
**What**: `public static readonly char[]` — reference is readonly, elements aren't. A test or plugin can mutate elements and weaken global validation. The validator is the only defence against attacker-controlled routing-slip headers redirecting traffic.
**Fix**: Use `SearchValues<char>.Create(...)` or expose as `ReadOnlySpan<char>`.

### RabbitMQ transport

#### R1. `ReceivedAsync` lambda never unsubscribed in `DisposeAsync`
**File**: [src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs:226](src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L226) (subscribe), `:723-736` (unsubscribe block)
**What**: `DisposeAsync` unsubscribes 6 other handlers but not `ReceivedAsync`. The lambda captures `this` and the delivery token. A late prefetched delivery between `BasicCancelAsync` and channel close can fire `EventAsync` on a disposed host.
**Fix**: Capture the lambda to a field at subscribe time; `_consumer.ReceivedAsync -= savedDelegate;` alongside the existing unsubscribes.

#### R2. OCE on cooperative cancellation logged at Error and emits `error` outcome
**File**: [src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs:106-115](src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L106-L115), [RabbitMqDispatchPipeline.cs:127-133](src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqDispatchPipeline.cs#L127-L133)
**What**: `ProcessWithMetricsAsync` has a broad `catch (Exception)` that doesn't filter OCE. Every graceful shutdown logs Error and produces an `error.type=OperationCanceledException` metric point. Dashboards/alerts will treat each shutdown as handler failures.
**Fix**: Add `catch (OperationCanceledException) { processed = false; throw; }` before the broad catch.

#### R3. `Consumer.DisposeAsync` races with in-flight `StartConsumingAsync` on `_model`
**File**: [src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs:115-189](src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs#L115-L189) (start), `:294-345` (dispose)
**What**: Start writes `_model = setupChannel` then clears in `finally`. Dispose reads/closes/disposes `_model` with no `_started` check. Concurrent dispose-during-startup can close+dispose the same `IChannel` and tear down the connection from under the still-running setup loop.
**Fix**: Gate `DisposeAsync` on a startup-completion `TaskCompletionSource`, or share a `SemaphoreSlim` between start and dispose.

#### R4. Audit `mandatory:true` collapses misconfig + transport failure into one counter
**File**: [src/ServiceConnect.Client.RabbitMQ/Audit/MessageAuditPublisher.cs:83-89](src/ServiceConnect.Client.RabbitMQ/Audit/MessageAuditPublisher.cs#L83-L89)
**What**: A stale audit binding (operator misconfig) produces `PublishException` on every audit publish, tagged the same as transient transport failures. Operators have nothing to alert on except an undifferentiated drop counter.
**Fix**: Tag `error.type=unroutable` distinctly; or split counter into `audit.drops.unroutable` vs `audit.drops.transient`.

#### R5. `Producer.DisposeAsync` releases `_publishLock` then can double-release the permit
**File**: [src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs:670-678](src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs#L670-L678)
**What**: Dispose releases the semaphore before `CloseAsync`. A publisher already inside the try-block then runs its `finally { _publishLock.Release(); }` after dispose's release — `CurrentCount` exceeds initial capacity. Permits leak; benign today because `_disposed=1` short-circuits new waiters, but the invariant is violated.
**Fix**: Don't release the lock during dispose; rely on `_disposed` short-circuit. Stop documenting "in-flight publishers may call Release on a disposed semaphore" since the semaphore is never disposed.

#### R6. `Connection.CreateChannelAsync` racy `_disposed` check can surface `InvalidOperationException`
**File**: [src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs:146-169](src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs#L146-L169)
**What**: After `ConnectAsync`, reads `_disposed` then `_connection`. A concurrent `DisposeAsync` (sets `_disposed=1` then nulls `_connection`) can leave the caller passing the `_disposed` check, then reading null `_connection`, then hitting the `InvalidOperationException("Connection was not initialized.")` branch instead of the canonical `ObjectDisposedException`.
**Fix**: Read `_connection` first, then re-check `_disposed` and throw ODE if disposed.

#### R7. `Connection.DisposeAsync` has no time budget for `conn.CloseAsync()`
**File**: [src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs:174-223](src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs#L174-L223)
**What**: `Producer.DisposeAsync` uses a shared stopwatch budget for both phases; plain `Connection` does not. A stalled broker that swallows close frames hangs DisposeAsync indefinitely after the semaphore wait. Host gets SIGKILL after grace period.
**Fix**: Mirror Producer pattern — shared stopwatch budget; wrap `conn.CloseAsync()` in a CT or use the `CloseAsync(TimeSpan)` overload.

### Processors / sagas

#### P1. Saga lock-key fallback to `msg.CorrelationId` doesn't actually serialise
**File**: [src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs:153-188](src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs#L153-L188)
**What**: When `mapping.MessageProp.Invoke(msg)` throws/returns null, `BuildLockKey` falls back to `(DataType, msg.CorrelationId)`. Two messages for the same saga but different CorrelationIds (in this fallback path) take different locks — they don't serialise. The intended `FindData` rethrow doesn't gate the serialisation.
**Fix**: Reject dispatch immediately with `PersistenceException` when the mapping fails — don't take a degraded lock and proceed.

#### P2. `DefaultProcessManagerPropertyMapper` rejects nested property chains at per-delivery construction
**File**: [src/ServiceConnect/Services/Processors/DefaultProcessManagerPropertyMapper.cs:28-35](src/ServiceConnect/Services/Processors/DefaultProcessManagerPropertyMapper.cs#L28-L35)
**What**: Rejects `d => d.Address.OrderId`. The throw lands at per-delivery construction wrapped in `PersistenceException` → retry loop. Poison message that never escapes retry without operator intervention.
**Fix**: Either support nested chains (walk `MemberExpression`s recursively) or validate mappings at handler-registration time so the throw lands at startup.

#### P3. `MessageHandlerRegistry.TryGetOrBuild` grows unbounded for unknown subtypes
**File**: [src/ServiceConnect/Services/Processors/MessageHandlerRegistry.cs:65-90](src/ServiceConnect/Services/Processors/MessageHandlerRegistry.cs#L65-L90)
**What**: Builds + caches a descriptor for any unknown subclass (polymorphic dispatch, dynamic deserialisation). Each entry includes a compiled `Expression.Lambda(...).Compile()` — permanent dynamic method per type. Cache grows for the life of the process.
**Fix**: Cache `null` for types where no `IMessageHandler<T>` is registered, so subsequent unknown subtypes short-circuit without compiling.

#### P4. `StreamProcessor` doesn't validate `PacketNumber` against `MaxPacketNumber`
**File**: [src/ServiceConnect/Services/Processors/StreamProcessor.cs:115-120,194](src/ServiceConnect/Services/Processors/StreamProcessor.cs#L115-L120)
**What**: `LastPacketNumber` is clamped at 100,000 but the per-packet `PacketNumber` is only checked for parse / `>= 0`. An attacker can park up to 100MB per sequence under `long.MaxValue`-ish keys, breaking the contiguous-fill design and making subsequent `Read()` iterate from 0 to LastPacketNumber.
**Fix**: Reject `packetNumber > MaxPacketNumber` or `< 0` at the top of `StreamProcessor.ProcessAsync`.

### Persistence

#### M1. InMemory aggregator `GetSnapshotAsync` doesn't claim a lease — diverges from Mongo
**File**: [src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs:95-128](src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs#L95-L128)
**What**: Returns an `AggregatorSnapshot` with no `LeaseSessionId` and doesn't mark entries as claimed. Two concurrent calls return the same Ids → both dispatch the same messages. Mongo persistor stamps `LockedBy` + `LockExpiresAt`; `RemoveSnapshot` filters on `LockedBy`. InMemory filters on Id only.
**Why it matters**: Tests against the InMemory persistor pass while the Mongo persistor's tests guard the real duplicate-dispatch hazard — staging-vs-prod divergence.
**Fix**: Add `LeasedAggregatorSnapshot` to InMemory, stamp `LockedBy`/`LockExpiresAt`, require matching lease on `RemoveSnapshot`.

#### M2. `MongoDbAggregatorPersistor.RemoveDataAsync` ignores lease
**File**: [src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs:432-480](src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs#L432-L480)
**What**: Deletes the row without checking `LockedBy`. No production caller today (tests only), but the interface is on `IAggregatorPersistor` and any future per-message ack handler will race with snapshot flush.
**Fix**: Remove from `IAggregatorPersistor` (no production caller), or gate on `LockedBy IS NULL OR LockExpiresAt <= $$NOW`.

#### M3. `CacheProvider.Update` bypasses `_addLock` and generation bump
**File**: [src/ServiceConnect.Persistence.InMemory/Cache/CacheProvider.cs:275-302](src/ServiceConnect.Persistence.InMemory/Cache/CacheProvider.cs#L275-L302)
**What**: Replaces via CAS on `_cache.TryUpdate` but doesn't take `_addLock`, doesn't bump `_generations`, reuses the original `RelativeExpiry`. A sliding-TTL entry's timer firing post-Update will match generation and call `Remove(key)` → silently evicts the just-updated value. Mitigated today because all first-party callers use no-expiry overload; a future caller storing sliding-TTL will be bitten.
**Fix**: Take `_addLock` in `Update`; bump `_generations` so stale timer callbacks bail.

#### M4. `MongoDbAggregatorPersistor`: `dataType.FullName!` is null for open generics / array element types
**File**: [src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs:195](src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs#L195)
**What**: `dataType.FullName!` will NRE for legal-but-obscure reflection types. NRE isn't caught by the outer `catch (BsonException)` so it propagates uncaught. Other call sites use `?? t.Name` correctly.
**Fix**: `dataType.FullName ?? dataType.Name`.

---

## Minor — defer or fix opportunistically

Grouped by area. Each is a brief one-liner; see agent reports for the full reasoning.

**RabbitMQ consumer**
- Validator-publish-then-shutdown-timeout can re-publish on broker redelivery ([RabbitMqConsumerHost.cs:341-345](src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L341-L345))
- `_consumerCancelledByBroker` flips on intentional publish-channel close from peer error ([RabbitMqConsumerHost.cs:468-484](src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L468-L484))
- `OnConsumerTagChangedAfterRecoveryAsync` is TOCTOU on `_consumerTag` ([RabbitMqConsumerHost.cs:520-540](src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L520-L540)) — use `CompareExchange`
- Retry-handler malformed RetryCount routes through error exchange without a distinct counter tag ([MessageRetryHandler.cs:57-67](src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs#L57-L67))
- Dispatch swallows transport-class exceptions at Error level → log noise during broker outages ([RabbitMqDispatchPipeline.cs:127-133](src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqDispatchPipeline.cs#L127-L133))

**RabbitMQ producer / topology**
- `RabbitMqTopologyProvisioner.ConfigureDeclareUtilityQueueAsync` swallows bind failure when not initial setup — contradicts class summary ([RabbitMqTopologyProvisioner.cs:131-142](src/ServiceConnect.Client.RabbitMQ/Topology/RabbitMqTopologyProvisioner.cs#L131-L142))
- `ConnectionFactoryBuilder.Build` doesn't enforce min heartbeat — `HeartbeatTime=0` silently disables ([ConnectionFactoryBuilder.cs:110-136](src/ServiceConnect.Client.RabbitMQ/Connection/ConnectionFactoryBuilder.cs#L110-L136))
- `BasicPropertiesCopier.CreateCopy` aliases `Headers` instead of deep-copying — undocumented in xmldoc ([BasicPropertiesCopier.cs:34-51](src/ServiceConnect.Client.RabbitMQ/Configuration/BasicPropertiesCopier.cs#L34-L51))
- `PublishTimeout = TimeSpan.Zero` with acks-on permanently breaks publish ([RabbitMQSettingKeys.cs:62-66](src/ServiceConnect.Client.RabbitMQ/Configuration/RabbitMQSettingKeys.cs#L62-L66))

**Core bus**
- `ServiceConnectMeter.Shutdown()` publicly callable, irreversibly kills metrics ([ServiceConnectMeter.cs:114](src/ServiceConnect/Diagnostics/ServiceConnectMeter.cs#L114))
- `BuildHeadersDirect` doesn't stamp `TypeName`/`FullTypeName` while `CreateEnvelope` does — outgoing-filter presence affects send-middleware visibility ([Bus.cs:1048-1082](src/ServiceConnect/Bus.cs#L1048-L1082))
- `Bus.RouteAsync` log-forge defence-in-depth: interpolates `snapshot[i]` unsanitised after validator ([Bus.cs:467](src/ServiceConnect/Bus.cs#L467))
- `TimeoutHeaderPersistence` base64-encodes binary header values but no decode path documented ([TimeoutHeaderPersistence.cs:103](src/ServiceConnect/Services/TimeoutHeaderPersistence.cs#L103))

**Processors**
- `AggregatorProcessor.FlushAggregatorAsync` disposes timer inside `flushLock` racing `ResetTimer` ([AggregatorProcessor.cs:281-284](src/ServiceConnect/Services/Processors/AggregatorProcessor.cs#L281-L284))
- `HandlerProcessor.RouteAsyncDelegateCache` grows unbounded with closed generics ([HandlerProcessor.cs:146-166](src/ServiceConnect/Services/Processors/HandlerProcessor.cs#L146-L166))
- `Aggregator<T>.Timeout()` default `Timeout.InfiniteTimeSpan` fails registry validation ([Aggregator.cs:16-22](src/ServiceConnect.Interfaces/Aggregation/Aggregator.cs#L16-L22))
- `ProcessManagerProcessor.PersistAsync` re-finds for delete-check on every success path ([ProcessManagerProcessor.cs:369-375](src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs#L369-L375))
- `StreamProcessor` cancellation doesn't refresh `LastSeenUtc` — long-running cancelled handlers risk eviction before redelivery ([StreamProcessor.cs:384-391](src/ServiceConnect/Services/Processors/StreamProcessor.cs#L384-L391))
- `MessageBusReadStream._packets` unbounded by packet count (only by total bytes) ([MessageBusReadStream.cs:16,80-140](src/ServiceConnect/Services/MessageBusReadStream.cs#L16))
- `AggregatorProcessor.ExtractIdempotencyKey` returns fresh GUID per delivery when MessageId missing — silently disables retry-redelivery dedupe ([AggregatorProcessor.cs:40-56](src/ServiceConnect/Services/Processors/AggregatorProcessor.cs#L40-L56))

**Persistence**
- `MongoDbTimeoutStore.InsertTimeoutAsync` no idempotency protection on `Id` duplicate — bubbles as generic `PersistenceException` instead of `ConcurrencyException` for parity ([MongoDbTimeoutStore.cs:104-126](src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs#L104-L126))
- `InMemoryAggregatorPersistor.InsertDataAsync` O(N) idempotency-key scan per insert ([InMemoryAggregatorPersistor.cs:58-65](src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs#L58-L65))
- `CacheProvider.Clear` doesn't bump generation, leaks `KeyRemoved` event races ([CacheProvider.cs:162-189](src/ServiceConnect.Persistence.InMemory/Cache/CacheProvider.cs#L162-L189))
- `MongoDbProcessManagerFinder.UpdateDataAsync` not in causally-consistent session — read-after-write divergence on SecondaryPreferred ([MongoDbProcessManagerFinder.cs:389](src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs#L389))
- `InMemoryPersistenceState` `ReaderWriterLockSlim` not `SupportsRecursion` — future callback reentrancy → deadlock ([InMemoryPersistenceState.cs:53](src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceState.cs#L53))
- `DeepClone` JSON round-trip silently drops explicit-interface auto-properties — document in release notes
- `InMemoryTimeoutStore` boundary tick `LockExpiresAt == now` semantics — consistent across stores but document the contract ([InMemoryTimeoutStore.cs:117](src/ServiceConnect.Persistence.InMemory/Timeout/InMemoryTimeoutStore.cs#L117))

**HealthChecks / Telemetry / Interfaces**
- `MaxTagValueLength <= 0` silently disables truncation rather than emitting empty strings — docs say "use int.MaxValue" ([ServiceConnectActivitySource.cs:513-526](src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L513-L526))
- InboundTraceFallback writes only `traceparent`/`tracestate`, dropping baggage ([ServiceConnectActivitySource.cs:419-438](src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L419-L438))
- `IProducer.GetHealthSnapshot()` default-interface-method race for third-party implementations ([IProducer.cs:92-94](src/ServiceConnect.Interfaces/Bus/IProducer.cs#L92-L94))
- `HealthCheckRecoveryState.LastHealthyTicks` public mutable field — invites torn-write reintroduction ([HealthCheckRecoveryState.cs:22](src/ServiceConnect.HealthChecks/HealthCheckRecoveryState.cs#L22))
- `IBus.RequestTimeoutAsync` DIM throws `NotSupportedException`; first-party `Bus` throws `InvalidOperationException` ([IBus.cs:273-274](src/ServiceConnect.Interfaces/Bus/IBus.cs#L273-L274))
- `RequestTimeoutException.PartialReplies` typed as `IReadOnlyList<object>` — caller must cast ([RequestTimeoutException.cs:70](src/ServiceConnect.Interfaces/Exceptions/RequestTimeoutException.cs#L70))
- `HeaderKeys` PascalCase doesn't populate AMQP basic-properties `correlation-id`/`message-id` — interop gap with non-ServiceConnect consumers
- `ServiceConnectActivitySource.Shutdown()` is publicly callable, irreversibly disposes process-lifetime singleton

---

## Themes worth a session

1. **Mutable-state escape**: B1, B2, B3, M2 (sort of) all share a pattern — a mutable object is registered/exposed by reference, allowing callers to bypass validation or change runtime behaviour. A pre-release sweep for `public … (List|IList|array|builder)` that's actually exposing mutable shared state would be high-leverage.

2. **Telemetry correctness**: T1, T2, T3 are all telemetry-only — body-size wrong, sampling drops continuity, enrichment can kill dispatch. The telemetry middleware deserves a focused pass with a "what happens if every header is malformed and every sampler drops the span" scenario.

3. **InMemory ↔ Mongo persistor parity**: M1 (lease) and M3 (cache Update) both involve an InMemory implementation missing a defence the Mongo implementation has. Worth running a parity audit — for each interface method, both implementations should make the same hazardous-input throw and the same successful-input return.

---

## What was NOT covered

- E2E test suite (`src/ServiceConnect.EndToEndTests/`) — agents only checked diffs for context
- Examples (`examples/`)
- Docs site (`website/`)
- Unit test gap-fill (`src/ServiceConnect.UnitTests/`)
- Serialization compatibility tests (`src/ServiceConnect.SerializationCompatTests/`)
- Filter implementations (deduplication etc. — only the surface was checked)

If any of these need a pass, ask for a focused review.
