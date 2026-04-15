# ServiceConnect-CSharp — Consolidated Code Review Report

**Date:** 2026-04-14
**Branch:** `improvements-and-fixes`
**Scope:** All production `.cs` files in `src/ServiceConnect.sln`
**Methodology:** 6 parallel review agents (Bugs & Correctness, Architecture & Design, CLEAN Code, C#/.NET Bad Practices, Technical Debt & Maintainability, Security)

---

## Summary

| Severity | Count |
|----------|-------|
| Critical | 7 |
| High | 20 |
| Medium | 30 |
| Low | 32 |
| Info | 9 |
| **Total** | **98** |

---

## Findings

### Critical

---

#### R-001 — `AggregatorProcessor`: Non-atomic batch remove allows double-processing

**Severity:** Critical
**Areas:** Bugs & Correctness
**File:** `ServiceConnect/Services/Processors/AggregatorProcessor.cs`, lines 101–107

`FlushAggregatorAsync` invokes `Execute` on the aggregator and then removes messages one-by-one in a loop via individual `RemoveDataAsync` calls. If the process crashes between `Execute` and the first removal, all messages replay on restart. If a single `RemoveDataAsync` fails mid-loop, remaining messages are included in the *next* batch — causing double-processing with no idempotency guard.

**Recommended Fix:** Use a bulk `RemoveAllAsync(name)` after `Execute` completes. Wrap in try/catch so partial failures are logged. Consider an outbox or idempotency-key pattern for the aggregator.

---

#### R-002 — `AggregatorProcessor.Dispose`: Races with in-flight flushes

**Severity:** Critical
**Areas:** Bugs & Correctness, Technical Debt
**File:** `ServiceConnect/Services/Processors/AggregatorProcessor.cs`, lines 115–125

`Dispose()` sets `_disposed = true` then iterates `_flushLocks` and disposes every `SemaphoreSlim` without waiting for in-flight `FlushAggregatorAsync` calls blocked on `flushLock.WaitAsync`. The awaiting `WaitAsync` throws `ObjectDisposedException` which is silently swallowed. The `_disposed` check-then-act is not atomic — concurrent `Dispose` calls can double-dispose.

**Recommended Fix:** Implement `IAsyncDisposable`. Use a `CancellationTokenSource` to cancel in-flight flushes, `await` any pending flush, then dispose semaphores. Use `Interlocked.Exchange` for the disposed guard.

---

#### R-003 — `CacheProvider.PurgeNormalPriorities` leaks `_slidingTime` entries

**Severity:** Critical
**Areas:** Technical Debt
**File:** `ServiceConnect.Persistence.InMemory/CacheProvider.cs`, lines 104–108

`PurgeNormalPriorities` removes expired items from `_cache` but never removes the corresponding key from `_slidingTime`. Over the lifetime of a long-running process, `_slidingTime` grows without bound — a memory leak proportional to total records ever created.

**Recommended Fix:** After removing from `_cache`, also call `_slidingTime.TryRemove(key, out _)`. Audit `Remove(key)` for the same issue.

---

#### R-004 — `AggregatorDocument.Id` never assigned before MongoDB insert

**Severity:** Critical
**Areas:** Technical Debt, CLEAN Code
**File:** `ServiceConnect.Persistence.MongoDb/MongoDbAggregatorPersistor.cs`, lines 55–75, 127–134

`AggregatorDocument.Id` is a `Guid` property that is never assigned in `InsertDataAsync`. Every document gets `Guid.Empty`. The collection has no unique index on `Id`, so duplicates accumulate. The `Version` property is set to `1` but never read or updated — dead code in a persistence path.

**Recommended Fix:** Assign `Id = Guid.NewGuid()` in the object initializer. Add a unique index on `Id`. Remove `Version` if aggregator docs don't use optimistic concurrency.

---

#### R-005 — `IPipelineConfiguration` exposes mutable `IList<Type>` collections

**Severity:** Critical
**Areas:** CLEAN Code (Encapsulation)
**File:** `ServiceConnect.Interfaces/Configuration/IPipelineConfiguration.cs`, lines 5–9
**File:** `ServiceConnect/Configuration/PipelineConfiguration.cs`, lines 7–11

Five filter/middleware lists are exposed as `IList<Type>`. Any caller can `Add`, `Remove`, `Clear`, or `Insert` at any time — including after the pipeline has been built and cached. This bypasses the builder pattern (`ServiceConnectBuilder.AddOutgoingFilter<T>()` etc.).

**Recommended Fix:** Change interface property types to `IReadOnlyList<Type>`. Expose mutation only through explicit `Add*` methods on the builder/concrete class.

---

#### R-006 — `ProcessManagerToMessageMap.PropertiesHierarchy` is a mutable public dictionary

**Severity:** Critical
**Areas:** CLEAN Code (Encapsulation)
**File:** `ServiceConnect.Interfaces/ProcessManagerToMessageMap.cs`, line 7

`public Dictionary<string, Type> PropertiesHierarchy { get; set; } = [];` — external code can replace or modify the hierarchy after construction, silently corrupting predicate-cache keying logic in both process manager finders.

**Recommended Fix:** Change to `IReadOnlyDictionary<string, Type>` with `init`-only setter. Same for `MessageProp` and `MessageType` — change `{ get; set; }` to `{ get; init; }`.

---

#### R-007 — `MongoDbProcessManagerFinder.InsertDataAsync` uses uncached `MakeGenericMethod` on every call

**Severity:** Critical
**Areas:** Technical Debt, CLEAN Code, C#/.NET Bad Practices
**File:** `ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs`, lines 125–128

`GetType().GetMethods().First(...).MakeGenericMethod(type).Invoke(...)` runs on every insert — uncached reflection on the hot path. The InMemory counterpart already uses a `ConcurrentDictionary<Type, Func<…>>` compiled-delegate cache.

**Recommended Fix:** Cache a compiled delegate per type in a `ConcurrentDictionary`, mirroring the pattern in `InMemoryProcessManagerFinder.BuildMemoryDataFactory`.

---

### High

---

#### R-008 — `Bus` constructor has 14 parameters; depends on concrete registry types

**Severity:** High
**Areas:** Architecture & Design (DIP), CLEAN Code (Coupling)
**File:** `ServiceConnect/Bus.cs`, lines 19–22, 30–60
**File:** `ServiceConnect/ServiceCollectionExtensions.cs`, lines 34–52

`Bus` takes four concrete registry types (`ProcessManagerHandlerRegistry`, `MessageHandlerRegistry`, `StreamHandlerRegistry`, `AggregatorRegistry`) solely to force DI to construct them eagerly. Lines 224–228 are no-op discards (`_ = _processManagerRegistry`). No abstraction exists for these registries.

**Recommended Fix:** Introduce `IRegistryInitialiser` or registry interfaces in `ServiceConnect.Interfaces`. Register interfaces in DI. Reduce `Bus` constructor to essential dependencies.

---

#### R-009 — `Bus.CreateStream<T>` silently ignores its `message` parameter

**Severity:** High
**Areas:** Technical Debt
**File:** `ServiceConnect/Bus.cs`, lines 188–194

`CreateStream<T>(T message, string endPoint)` takes a `message` argument but never reads, serializes, or sends it. Callers passing a populated message expecting it transmitted as a stream header are silently wrong.

**Recommended Fix:** Either serialize and send `message` as a stream-open envelope, or remove the parameter from both `IBus.CreateStream` and `Bus.CreateStream`.

---

#### R-010 — `Consumer` violates SRP: topology setup + client lifecycle + dual constructors

**Severity:** High
**Areas:** Architecture & Design (SRP), CLEAN Code (Cohesion/Non-redundant)
**File:** `ServiceConnect.Client.RabbitMQ/Consumer.cs`, lines 27–42, 65–120

Two constructors duplicate 4 field assignments. 100+ lines of topology setup (`ConfigureExchange/Queue/Retry/Error/Audit`) could be a standalone `RabbitMqTopologyProvisioner`. The second constructor defers connection creation via null-coalescing.

**Recommended Fix:** Extract topology provisioning. Chain constructors or use a single constructor with optional parameter. Add null guards.

---

#### R-011 — `Consumer` and `RabbitMqConsumerHost` store `CancellationToken` as a field

**Severity:** High
**Areas:** C#/.NET Bad Practices, CLEAN Code, Technical Debt
**File:** `ServiceConnect.Client.RabbitMQ/Consumer.cs`, lines 25, 49
**File:** `ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs`, line 33

Storing a `CancellationToken` as an instance field is a well-known anti-pattern. The token captured at `StartConsumingAsync` call time may be stale. In `Consumer`, the field `_consumingCt` is assigned but never read within the class — dead state.

**Recommended Fix:** Pass `CancellationToken` as a method parameter to every async method that needs it. Remove the stored fields.

---

#### R-012 — `Consumer.DisposeAsync`: Channel not closed or disposed

**Severity:** High
**Areas:** Bugs & Correctness
**File:** `ServiceConnect.Client.RabbitMQ/Consumer.cs`, lines 122–133

After disposing all `Client` instances, `_model = null` without closing or disposing the AMQP channel opened at line 63. The channel object is abandoned, potentially leaking server-side resources.

**Recommended Fix:** Close and dispose the channel before nulling: `if (_model is { IsOpen: true }) await _model.CloseAsync(); _model.Dispose(); _model = null;`

---

#### R-013 — `HandlerReference.RoutingKeys` is populated but never consumed

**Severity:** High
**Areas:** Architecture & Design, Technical Debt
**File:** `ServiceConnect.Interfaces/HandlerReference.cs`, line 7
**File:** `ServiceConnect/Services/HandlerScanner.cs`, lines 33, 48, 63, 78

`HandlerScanner` populates `RoutingKeys` for every handler. No processor, registry, or other component ever reads it. Any routing behaviour configured via `RoutingKeys` silently has no effect.

**Recommended Fix:** Either implement topic-based routing that respects `RoutingKeys`, or remove the property and stop populating it.

---

#### R-014 — `MessageBusWriteStream.WriteAsync` performs no bounds validation

**Severity:** High
**Areas:** Technical Debt
**File:** `ServiceConnect/Services/MessageBusWriteStream.cs`, lines 28–45

No validation of `offset >= 0`, `count >= 0`, or `offset + count <= buffer.Length`. A negative offset or out-of-range count produces unhelpful `Array.Copy` exceptions.

**Recommended Fix:** Add standard `ArgumentOutOfRangeException` / `ArgumentNullException` guards matching the `Stream.Write` contract.

---

#### R-015 — `MongoDbAggregatorPersistor` imports `ServiceConnect.Services` — layering violation

**Severity:** High
**Areas:** Architecture & Design, Technical Debt
**File:** `ServiceConnect.Persistence.MongoDb/MongoDbAggregatorPersistor.cs`, line 7

`using ServiceConnect.Services;` causes the persistence package to depend on the core services layer, inverting the intended dependency direction. Persistence should depend only on `ServiceConnect.Interfaces`.

**Recommended Fix:** Identify which type is needed from `ServiceConnect.Services`, move it to `ServiceConnect.Interfaces`, and remove the import.

---

#### R-016 — `MongoDbData<T>.Name` and `Locked` are never populated on insert

**Severity:** High
**Areas:** Technical Debt, CLEAN Code
**File:** `ServiceConnect.Persistence.MongoDb/MongoDbData.cs`, lines 12–13
**File:** `ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs` (InsertDataTypedAsync)

`Name` and `Locked` are declared on the process-manager persistence wrapper but never set or queried. `Locked` appears vestigial from an older timeout design.

**Recommended Fix:** Remove `Name` and `Locked` from `MongoDbData<T>`. If timeout locking is needed, it belongs on a dedicated `MongoDbTimeoutData` type.

---

#### R-017 — `MongoDbProcessManagerFinder` and `InMemoryProcessManagerFinder` implement two unrelated interfaces

**Severity:** High
**Areas:** Architecture & Design (SRP), CLEAN Code (Cohesion)
**File:** `ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs` (357 lines)
**File:** `ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs` (325 lines)

Both implement `IProcessManagerFinder` + `ITimeoutStore` — two distinct domain concerns (saga correlation vs. timeout scheduling). Both exceed the 300-line class threshold. This forces consumers to get timeout store functionality even if unwanted.

**Recommended Fix:** Extract `MongoDbTimeoutStore : ITimeoutStore` and `InMemoryTimeoutStore : ITimeoutStore` as separate classes. They can share the database/cache instance via DI.

---

#### R-018 — `ProcessManagerProcessor`: New data inserted even when handler throws

**Severity:** High
**Areas:** Bugs & Correctness
**File:** `ServiceConnect/Services/Processors/ProcessManagerProcessor.cs`, lines 64–72

If the handler completes but `InsertDataAsync` throws, handler side-effects are applied but the persistence record is never written. On retry, `FindDataAsync` returns null, `isNew` becomes true again, and the handler runs a second time — duplicating business side-effects.

**Recommended Fix:** Wrap handler invocation and persistence together. Catch handler exceptions to avoid inserting data on failure. Consider idempotency keys.

---

#### R-019 — `Producer.DisposeConnectionAsync` duplicates channel/connection teardown logic

**Severity:** High
**Areas:** CLEAN Code (Non-redundant)
**File:** `ServiceConnect.Client.RabbitMQ/Producer.cs`, lines 273–353

Three separate methods (`DisposeConnectionAsync`, `DisposeModelAsync`, `DisposeConnectionInstanceAsync`) contain the same try/catch/close/dispose/null-set pattern for channel and connection teardown.

**Recommended Fix:** Extract a shared `TearDownChannelAndConnectionAsync()` helper. Have all three methods delegate to it.

---

#### R-020 — `RabbitMqConsumerHost.EventAsync`: `_model` null race during shutdown

**Severity:** High
**Areas:** Bugs & Correctness
**File:** `ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs`, lines 98–136

`_model` is set to `null` in `CloseChannelAsync` (called from `DisposeAsync`). If `DisposeAsync` runs concurrently with an in-progress `EventAsync`, `_model` can be `null` at `BasicAckAsync`/`BasicNackAsync` in the `finally` block, producing a `NullReferenceException` that is swallowed. The delivery is then neither acked nor nacked.

**Recommended Fix:** Capture `_model` into a local variable at the start of `EventAsync` and use the local in the `finally` block.

---

#### R-021 — `RequestReplyManager.SendRequestMultiAsync`: Race between timeout and reply snapshot

**Severity:** High
**Areas:** Bugs & Correctness
**File:** `ServiceConnect/Services/RequestReplyManager.cs`, lines 93–121

When the timeout fires via `TrySetResult(null!)`, replies received between the timeout and the snapshot at line 114 are silently included. The `_pendingRequests` entry removal in `finally` (line 121) creates a TOCTOU race with `ProcessReply`.

**Recommended Fix:** Remove the entry from `_pendingRequests` atomically when the timeout fires (inside the registration callback), not in the `finally` block.

---

#### R-022 — Message size limit declared but never enforced on inbound path

**Severity:** High
**Areas:** Security
**File:** `ServiceConnect.Client.RabbitMQ/Producer.cs`, lines 12, 42, 189
**File:** `ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs`, lines 98–192

`Producer.MaximumMessageSize` is declared (default 64 KiB) but never checked in `PublishAsync`, `SendAsync`, `SendBytesAsync`, or the inbound `ProcessMessageAsync`. Arbitrarily large messages cause unbounded `args.Body.ToArray()` allocations — memory-exhaustion DoS.

**Recommended Fix:** Add a size check before publishing and in `ProcessMessageAsync` before `Body.ToArray()`. Nack messages exceeding the configured limit.

---

#### R-023 — `SourceAddress` header accepted without validation; enables reply hijacking

**Severity:** High
**Areas:** Security
**File:** `ServiceConnect/Services/ConsumeContext.cs`, lines 21–32

`ConsumeContext.ReplyAsync` reads `SourceAddress` verbatim from incoming headers and uses it as the reply destination. AMQP headers are not authenticated — a malicious sender can set `SourceAddress` to any queue name, redirecting reply messages.

**Recommended Fix:** Validate `sourceAddress` against `IQueueConfiguration.QueueMappings` or an allowlist. Alternatively, use `RequestMessageId` correlation through `RequestReplyManager` instead of trusting the wire-level header.

---

#### R-024 — `RoutingSlip` header trusted for routing with insufficient validation

**Severity:** High
**Areas:** Security
**File:** `ServiceConnect/Services/Processors/HandlerProcessor.cs`, lines 60–88

The character allowlist is permissive — many semantically meaningful queue-name characters (`.`, `:`, `/`) are not blocked. Any message with a crafted `RoutingSlip` header can cause the bus to route payloads to arbitrary queues, enabling exfiltration or privilege escalation.

**Recommended Fix:** Validate destinations against registered queue mappings or a startup-configured allowlist. Provide a config option to disable routing-slip processing entirely.

---

#### R-025 — `ServiceCollectionExtensions.AddServiceConnect` is 162 lines

**Severity:** High
**Areas:** CLEAN Code (Cohesion)
**File:** `ServiceConnect/ServiceCollectionExtensions.cs`, lines 12–174

Single method registers configuration, core services, registries, processors, dispatcher, handler scanning, DI handler loops, type registry, Bus factory, and hosted services.

**Recommended Fix:** Extract `RegisterConfiguration`, `RegisterCoreServices`, `RegisterProcessors`, `RegisterHandlers`, `RegisterBus` helpers. Keep `AddServiceConnect` as a ~10-line coordinator.

---

#### R-026 — `ServiceConnectActivitySource`: Mutable static `Options` + hardcoded RabbitMQ

**Severity:** High
**Areas:** C#/.NET Bad Practices, Architecture & Design (OCP)
**File:** `ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`, lines 10, 41–43

`Options` is a `public static` mutable property — any code can replace the instance at runtime (race condition). Additionally, `messaging.system = "rabbitmq"` is hardcoded, violating OCP if a non-AMQP transport is ever used.

**Recommended Fix:** Remove the public setter (use `internal` + startup-only configuration). Introduce an `IMessagingSystemAttributes` injectable for transport-specific metadata.

---

#### R-027 — `DateTime.UtcNow` scattered throughout production code

**Severity:** High
**Areas:** C#/.NET Bad Practices
**Files:** `MessageRetryHandler.cs:57`, `RabbitMqConsumerHost.cs:159,177`, `Producer.cs:244`, `InMemoryProcessManagerFinder.cs:136,192,235,246`, `SlidingDetails.cs:24,33`, `CacheProvider.cs:28`, `MongoDbProcessManagerFinder.cs:248`, `StreamProcessor.cs:69,127`

Direct `DateTime.UtcNow` calls make time-dependent behavior (cache expiry, retry timestamps, stream eviction, timeout locking) impossible to unit test.

**Recommended Fix:** Introduce .NET 8+ `TimeProvider` abstraction and inject it everywhere. `TimeProvider.System` for production; `FakeTimeProvider` for tests.

---

### Medium

---

#### R-028 — `AggregatorProcessor.ResetTimer`: Double-flush window between timer swap

**Severity:** Medium
**Areas:** Bugs & Correctness
**File:** `ServiceConnect/Services/Processors/AggregatorProcessor.cs`, lines 54–68

New `Timer` starts before old one is disposed. Both can fire concurrently. The semaphore prevents double-flush bodies, but `previous?.Dispose()` can throw if already disposed.

**Recommended Fix:** Wrap `previous?.Dispose()` in try/catch. Consider `Timer.Change` to reset an existing timer instead of allocating a new one.

---

#### R-029 — `Bus.DisposeAsync` hangs indefinitely without cancellation

**Severity:** Medium
**Areas:** Bugs & Correctness
**File:** `ServiceConnect/Bus.cs`, lines 278–291

`StopConsumingCoreAsync()` called with `CancellationToken.None`. If `_model.CloseAsync` hangs due to network issues, `Bus.DisposeAsync` hangs and `_lifecycleSemaphore.Dispose()` is never called.

**Recommended Fix:** Use a `CancellationTokenSource` with a disposal timeout in `StopConsumingCoreAsync`.

---

#### R-030 — `CacheProvider` uses Reactive Extensions `Observable.Timer` with silent error swallowing

**Severity:** Medium
**Areas:** CLEAN Code, C#/.NET Bad Practices
**File:** `ServiceConnect.Persistence.InMemory/CacheProvider.cs`, lines 136–156

`Observable.Timer` introduces a `System.Reactive` dependency for a simple delayed callback. The error handler is an empty lambda — items silently remain in cache forever if the timer faults.

**Recommended Fix:** Replace with `new Timer(callback, null, timeSpan, Timeout.InfiniteTimeSpan)`. Remove `System.Reactive` dependency. Log any timer errors.

---

#### R-031 — `Client.cs` is a redundant pass-through facade

**Severity:** Medium
**Areas:** Technical Debt
**File:** `ServiceConnect.Client.RabbitMQ/Client.cs`

Every method is a one-line delegation to `RabbitMqConsumerHost`. No added logic, error handling, or abstraction.

**Recommended Fix:** Make `RabbitMqConsumerHost` implement `IConsumer` directly. Remove `Client.cs`.

---

#### R-032 — `Consumer` broadly catches `Exception` in all topology declaration methods

**Severity:** Medium
**Areas:** C#/.NET Bad Practices, Technical Debt
**File:** `ServiceConnect.Client.RabbitMQ/Consumer.cs`, 8 catch blocks (lines 143–243)
**File:** `ServiceConnect.Client.RabbitMQ/Producer.cs`, lines 264–267

All `Configure*` methods catch `Exception` and log a warning, silently swallowing programming errors (`NullReferenceException`, `ObjectDisposedException`). On initial startup, declaration failures leave the consumer in a non-consuming state with no health-check failure.

**Recommended Fix:** Catch only `OperationInterruptedException` (AMQP PRECONDITION_FAILED). Re-throw on initial startup after logging.

---

#### R-033 — `IConsumeContext.CancellationToken` has a public setter

**Severity:** Medium
**Areas:** CLEAN Code (Encapsulation), Technical Debt
**File:** `ServiceConnect.Interfaces/IConsumeContext.cs`, line 22
**File:** `ServiceConnect/Services/ConsumeContext.cs`, line 10

Handlers can reassign the cancellation token mid-processing. The token is set by the dispatch pipeline and should not be mutable by handler code.

**Recommended Fix:** Change to `{ get; }` or `{ get; init; }`. The token is already passed into the constructor.

---

#### R-034 — `DefaultProcessManagerPropertyMapper` recreated per message

**Severity:** Medium
**Areas:** Architecture & Design (DIP), Technical Debt
**File:** `ServiceConnect/Services/Processors/ProcessManagerProcessor.cs`, line 41

`new DefaultProcessManagerPropertyMapper()` on every message dispatch. The handler's `ConfigureProcessManager` registration re-runs each time.

**Recommended Fix:** Inject `IProcessManagerPropertyMapper` via DI. Cache a `ConcurrentDictionary<Type, IProcessManagerPropertyMapper>` per handler type.

---

#### R-035 — `HandlerProcessor` routing slip uses uncached `MakeGenericMethod`

**Severity:** Medium
**Areas:** Technical Debt
**File:** `ServiceConnect/Services/Processors/HandlerProcessor.cs`, lines 85–86

`typeof(IBus).GetMethod("SendAsync").MakeGenericMethod(messageType)` on every routed message — uncached reflection on a latency-sensitive path.

**Recommended Fix:** Cache compiled delegates in a `ConcurrentDictionary<Type, Func<…>>`.

---

#### R-036 — `IBusConfiguration` exposes infrastructure sub-configurations (ISP violation)

**Severity:** Medium
**Areas:** Architecture & Design (ISP)
**File:** `ServiceConnect.Interfaces/Configuration/IBusConfiguration.cs`, lines 3–21

Consumers needing only one sub-configuration still depend on the full bus configuration interface exposing `Transport`, `Queues`, `Persistence`, and `Pipeline`.

**Recommended Fix:** Remove sub-configuration properties from `IBusConfiguration`. Consumers should take sub-configuration interfaces directly via DI.

---

#### R-037 — `InMemoryProcessManagerFinder.FindMatchingItem`: Reflection fallback under lock

**Severity:** Medium
**Areas:** Bugs & Correctness, CLEAN Code
**File:** `ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs`, lines 69–95

The fallback path uses `dataProp.GetValue(value)` via reflection on every cache miss. `versionProp.GetValue(value)` cast to `int` via `!` can throw `NullReferenceException` if `Version` is nullable on a non-`MemoryData<T>` type.

**Recommended Fix:** Add null check: `if (versionProp.GetValue(value) is int version)`. Introduce `IMemoryData` interface to avoid reflection.

---

#### R-038 — `InMemoryProcessManagerFinder` holds static mutable state

**Severity:** Medium
**Areas:** Architecture & Design (SRP)
**File:** `ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs`, lines 29–33

`CompiledPredicates` and `MemoryDataFactories` are `static` dictionaries shared across all instances, bypassing DI lifetime management and breaking test isolation.

**Recommended Fix:** Extract to a singleton `ProcessManagerPredicateCache` service registered in DI.

---

#### R-039 — `Message` base class is not `sealed`

**Severity:** Medium
**Areas:** C#/.NET Bad Practices
**File:** `ServiceConnect.Interfaces/Message.cs`, line 3

Not sealed and no mechanism to prevent arbitrary deep inheritance. If subclassing beyond one level is intentional, document it. If not, seal it.

**Recommended Fix:** Either seal or add explicit documentation about the inheritance contract.

---

#### R-040 — `MongoDbProcessManagerFinder.EnsureTimeoutIndex` / `EnsureCorrelationIdIndex`: Synchronous calls from async context

**Severity:** Medium
**Areas:** Bugs & Correctness, C#/.NET Bad Practices
**File:** `ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs`, lines 320–351

`collection.Indexes.CreateOne(...)` is synchronous, blocking a thread-pool thread inside async methods.

**Recommended Fix:** Use `collection.Indexes.CreateOneAsync(...)`. Propagate `CancellationToken`.

---

#### R-041 — `MongoDbProcessManagerFinder.InsertDataAsync`: `TargetInvocationException` not fully unwrapped

**Severity:** Medium
**Areas:** Bugs & Correctness
**File:** `ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs`, lines 129–139

The catch clause only catches `TargetInvocationException when (InnerException is MongoException)`. Non-Mongo inner exceptions propagate as opaque `TargetInvocationException`.

**Recommended Fix:** Add a general catch: `catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }`

---

#### R-042 — `MongoDbProcessManagerFinder`: Timeout index on `Id` only, not query fields

**Severity:** Medium
**Areas:** Technical Debt
**File:** `ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs`

`GetTimeoutsBatchAsync` filters on `Locked == false && Time <= utcNow` but the index is on `Id` only. Every poll does a full collection scan.

**Recommended Fix:** Add compound index `{ Locked: 1, Time: 1 }`.

---

#### R-043 — Non-`readonly` mutable fields in `Consumer` that should be immutable

**Severity:** Medium
**Areas:** C#/.NET Bad Practices
**File:** `ServiceConnect.Client.RabbitMQ/Consumer.cs`, lines 12–21

`_durable`, `_retryDelay`, `_exclusive`, `_autoDelete`, `_queueArguments` etc. are set once in `StartConsumingAsync` and never changed but are declared as plain mutable fields.

**Recommended Fix:** Move configuration extraction to the constructor (making fields `readonly`) or use a settings record.

---

#### R-044 — `OutgoingEventArgs.Headers` setter silently converts `null` to empty dict

**Severity:** Medium
**Areas:** CLEAN Code (Assertive)
**File:** `ServiceConnect.Interfaces/OutgoingEventArgs.cs`, lines 7–12

The setter coerces `null` to an empty dictionary, silently swallowing likely programming errors.

**Recommended Fix:** Add `ArgumentNullException.ThrowIfNull(value)` in the setter.

---

#### R-045 — `ProcessManagerToMessageMap` uses mutable `{ get; set; }` where `{ get; init; }` suffices

**Severity:** Medium
**Areas:** CLEAN Code (Encapsulation)
**File:** `ServiceConnect.Interfaces/ProcessManagerToMessageMap.cs`, lines 5–6

`MessageProp` and `MessageType` have full mutable setters. They are populated once by `DefaultProcessManagerPropertyMapper.ConfigureMapping` and should not be overwritable.

**Recommended Fix:** Change to `{ get; init; }`.

---

#### R-046 — `Producer.DisposeAsync` is not thread-safe

**Severity:** Medium
**Areas:** C#/.NET Bad Practices
**File:** `ServiceConnect.Client.RabbitMQ/Producer.cs`, lines 174–178

The check-and-set of `_disposed` is not atomic. Two concurrent callers can both pass the guard and double-dispose semaphores and connections.

**Recommended Fix:** Use `Interlocked.Exchange(ref _disposedInt, 1)` for an atomic guard.

---

#### R-047 — `SendEventArgs.EndPoints` encodes a list as bracket-wrapped CSV string

**Severity:** Medium
**Areas:** CLEAN Code (Encapsulation)
**File:** `ServiceConnect.Interfaces/SendEventArgs.cs`, lines 7–18

The init accessor serializes a list into `EndPoint` as `"[ep1,ep2]"` and the getter deserializes it — an undocumented private protocol that leaks from the type.

**Recommended Fix:** Store `EndPoint` and `EndPoints` independently. Document the format if backward compatibility is required.

---

#### R-048 — Sensitive configuration defaults: `localhost` MongoDB/RabbitMQ without auth

**Severity:** Medium
**Areas:** Security
**File:** `ServiceConnect/Configuration/PersistenceConfiguration.cs`, line 7
**File:** `ServiceConnect/Configuration/TransportConfiguration.cs`, line 19

Default `ConnectionString` is `"mongodb://localhost/"` (unauthenticated), default `Host` is `"localhost"` with `null` credentials. Forgetting to override in production silently connects without auth or TLS.

**Recommended Fix:** Log a warning at startup when localhost/null-credential defaults are detected. Consider requiring explicit configuration.

---

#### R-049 — `CertPassphrase` stored as plain `string` in DI-registered singleton

**Severity:** Medium
**Areas:** Security
**File:** `ServiceConnect/Configuration/TransportConfiguration.cs`, lines 40–41

The TLS certificate passphrase is held as a plain `string` in the long-lived `TransportConfiguration` singleton accessible via DI. If the configuration object is ever accidentally serialized (diagnostics, OTel enrichment), the passphrase leaks.

**Recommended Fix:** Use a factory/delegate pattern for the passphrase. Mark the property with XML doc explicitly noting it as sensitive.

---

#### R-050 — No inbound header count or total header size limit

**Severity:** Medium
**Areas:** Security
**File:** `ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs`, lines 144–152

All incoming AMQP headers are copied into a `Dictionary` without bounding count or data size. A malicious publisher with thousands of large headers causes resource exhaustion.

**Recommended Fix:** Add `MaxHeaderCount` (e.g., 64) and `MaxHeaderValueBytes` (e.g., 8192) guards. Nack messages that exceed.

---

#### R-051 — Exception details in error-queue headers may leak internal state

**Severity:** Medium
**Areas:** Security
**File:** `ServiceConnect.Client.RabbitMQ/MessageRetryHandler.cs`, lines 54–59

`ExceptionType` (full CLR type name) and `Message` (can include file paths, connection strings) are serialized into the `Exception` header on the error queue.

**Recommended Fix:** Provide a configurable `Func<Exception, object>` for error metadata. Document that exception messages should be sanitized at source.

---

#### R-052 — `IncludeMachineNameInHeaders` leaks infrastructure topology

**Severity:** Medium
**Areas:** Security
**File:** `ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs`, line 161
**File:** `ServiceConnect.Client.RabbitMQ/Producer.cs`, lines 244–246

When enabled, `Environment.MachineName` is stamped into every message, exposing internal hostnames to any consumer of audited messages.

**Recommended Fix:** Add `<remarks>` doc warning that this discloses internal host names. Should not be enabled where messages cross trust boundaries.

---

#### R-053 — `ServiceConnectActivitySource` repeats logic across 3 activity-start methods

**Severity:** Medium
**Areas:** CLEAN Code (Non-redundant)
**File:** `ServiceConnect.Telemetry/ServiceConnectActivitySource.cs` (253 lines)

`Publish`, `Consume`, and `Send` all repeat: listener check, activity start, null guard, 3 base tags, and the `EnrichWithMessage` try/catch pattern.

**Recommended Fix:** Extract `StartActivity(...)` and `TryEnrich(...)` helper methods.

---

#### R-054 — `StreamProcessor`: Non-atomic check-and-remove of completed streams

**Severity:** Medium
**Areas:** Bugs & Correctness, Technical Debt
**File:** `ServiceConnect/Services/Processors/StreamProcessor.cs`, lines 82–117

`IsComplete()` check and `_activeStreams.TryRemove` are not atomic. Concurrent `ProcessAsync` for the same `sequenceId` can race. `_activeStreams` and `_streamTimestamps` are removed in two separate operations — concurrent `EvictStaleStreams` can observe inconsistent state.

**Recommended Fix:** Use a single `ConcurrentDictionary` with a composite value. Or lock around paired removal.

---

#### R-055 — `ICacheProvider` is a fat interface (ISP violation)

**Severity:** Medium
**Areas:** Architecture & Design (ISP)
**File:** `ServiceConnect.Persistence.InMemory/ICacheProvider.cs`

Contains methods (`PurgeNormalPriorities`, `KeyRemoved` event, typed `Keys<TKey>()`) never used by the persistors. The Rx dependency in `CacheProvider` adds transitive weight.

**Recommended Fix:** Define a narrower `IKeyValueStore` interface with only the methods persistors actually use.

---

#### R-056 — `IProducer.DisconnectAsync` is semantically identical to `DisposeAsync`

**Severity:** Medium
**Areas:** Technical Debt
**File:** `ServiceConnect.Client.RabbitMQ/Producer.cs`, line ~171

The name implies a reconnectable operation, but it just delegates to `DisposeAsync`. Misleading API contract.

**Recommended Fix:** Remove `DisconnectAsync` from `IProducer`. Add `[Obsolete]` first if backward compatibility is needed.

---

#### R-057 — `ProcessManagerTimeoutService` resolves `IBus` via service locator on every poll

**Severity:** Medium
**Areas:** Technical Debt
**File:** `ServiceConnect/Services/ProcessManagerTimeoutService.cs`, line 65

`serviceProvider.GetService<IBus>()` on every tick instead of constructor injection. Silently returns `null` if `IBus` isn't registered.

**Recommended Fix:** Inject `IBus` (or `Lazy<IBus>`) via constructor. Add null guard.

---

### Low

---

#### R-058 — `MessageBusWriteStream.DisposeAsync`: `_closed` flag not thread-safe

**Severity:** Low
**Areas:** Bugs & Correctness
**File:** `ServiceConnect/Services/MessageBusWriteStream.cs`, lines 49, 68–71

Two concurrent calls to `CloseAsync` can both see `_closed == false`, sending the close packet twice.

**Recommended Fix:** Use `Interlocked.CompareExchange(ref _closedFlag, 1, 0)`.

---

#### R-059 — `CacheProvider.Add` silently returns on expired `absoluteExpiry`

**Severity:** Low
**Areas:** Bugs & Correctness
**File:** `ServiceConnect.Persistence.InMemory/CacheProvider.cs`, lines 27–34

If `absoluteExpiry < DateTime.UtcNow`, the item is silently not added. Callers proceed as if insertion succeeded.

**Recommended Fix:** Throw `ArgumentOutOfRangeException`.

---

#### R-060 — `InMemoryProcessManagerFinder.GetTimeoutsBatchAsync` scans all cache keys

**Severity:** Low
**Areas:** Bugs & Correctness
**File:** `ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs`, lines 248–260

Iterates all keys (process-manager data + timeout data) and type-checks each. O(n) over all PM data on every timeout poll.

**Recommended Fix:** Use a separate `CacheProvider` instance for timeouts.

---

#### R-061 — `MongoDbProcessManagerFinder.UpdateDataAsync`: Fragile version revert logic

**Severity:** Low
**Areas:** Bugs & Correctness
**File:** `ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs`, lines 176–198

Dual-revert paths (`PersistenceException` catch + outer `MongoException` catch) for `vd.Version`. While control flow prevents double-revert in practice, the logic is fragile.

**Recommended Fix:** Capture `currentVersion` as a local and use it in all revert paths.

---

#### R-062 — `AggregatorRegistry.CompileBuildTypedList`: Enumerator not disposed

**Severity:** Low
**Areas:** Bugs & Correctness
**File:** `ServiceConnect/Services/Processors/AggregatorRegistry.cs`, lines 100–131

Expression tree builds a loop using `GetEnumerator()` / `MoveNext()` with no `try/finally` to call `Dispose()`.

**Recommended Fix:** Wrap loop in `try/finally` expression calling `enumeratorVar.Dispose()`, or use an index-based loop.

---

#### R-063 — `ProcessManagerTimeoutService.StopAsync`: `_cts` not disposed

**Severity:** Low
**Areas:** Bugs & Correctness
**File:** `ServiceConnect/Services/ProcessManagerTimeoutService.cs`, lines 43–53

`StopAsync` calls `_cts.Cancel()` and awaits `_pollingTask` but never disposes `_cts`. Relies on `DisposeAsync` always being called.

**Recommended Fix:** Dispose `_cts` in `StopAsync` after awaiting, or document the reliance on `DisposeAsync`.

---

#### R-064 — `SendMessagePipeline`: Singleton lifetime constraint enforced only by comment

**Severity:** Low
**Areas:** Architecture & Design (OCP)
**File:** `ServiceConnect/Services/SendMessagePipeline.cs`, lines 13–16

Middleware instances are cached at first use. Scoped/transient registrations are silently promoted to singleton lifetime — a captive dependency.

**Recommended Fix:** Register middleware as singleton explicitly in the builder, or validate lifetimes at startup.

---

#### R-065 — `IRequestReplyManager` leaks pipeline delegate into interface

**Severity:** Low
**Areas:** Architecture & Design (DIP)
**File:** `ServiceConnect.Interfaces/IRequestReplyManager.cs`, lines 7–13

Accepts a `Func<Type, byte[], Dictionary<string, string>, string?, CancellationToken, Task>` parameter — the `ISendMessagePipeline` delegate shape.

**Recommended Fix:** Inject `ISendMessagePipeline` into `RequestReplyManager` via constructor. Remove `sendAction` from the interface.

---

#### R-066 — `Consumer._model` retained as long-lived field but only used during startup

**Severity:** Low
**Areas:** Architecture & Design
**File:** `ServiceConnect.Client.RabbitMQ/Consumer.cs`, lines 11–12, 63–64

The setup channel is used exclusively for topology provisioning. After `StartConsumingAsync`, it's never used again but kept alive.

**Recommended Fix:** Dispose the setup channel at the end of `StartConsumingAsync`.

---

#### R-067 — `CacheItem` has public parameterless constructor and mutable setters

**Severity:** Low
**Areas:** CLEAN Code (Encapsulation), C#/.NET Bad Practices
**File:** `ServiceConnect.Persistence.InMemory/CacheItem.cs`, lines 5–26

Allows creation of inconsistent `CacheItem` objects. Only the parameterized constructor is used.

**Recommended Fix:** Remove parameterless constructor. Change setters to `init`-only.

---

#### R-068 — `MemoryData<T>.Data` and `MongoDbData<T>.Data` use `default!`

**Severity:** Low
**Areas:** CLEAN Code (Encapsulation)
**File:** `ServiceConnect.Persistence.InMemory/MemoryData.cs`, line 9
**File:** `ServiceConnect.Persistence.MongoDb/MongoDbData.cs`, line 11

`= default!` silences nullable warning without meaningful safety.

**Recommended Fix:** Mark `Data` as `required` (C# 11) or add a guarded constructor.

---

#### R-069 — Magic number `11` in `Producer.GetHeaders`

**Severity:** Low
**Areas:** CLEAN Code (Non-redundant)
**File:** `ServiceConnect.Client.RabbitMQ/Producer.cs`, line 228

Capacity `callerCount + 11` where `11` is the expected stamped header count, explained only in a comment.

**Recommended Fix:** Extract to `private const int StampedHeaderCount = 11;`.

---

#### R-070 — `Consumer.ConfigureError*` and `ConfigureAudit*` methods are near-identical

**Severity:** Low
**Areas:** CLEAN Code (Non-redundant)
**File:** `ServiceConnect.Client.RabbitMQ/Consumer.cs`, lines 195–248

Four methods could be two. `ConfigureErrorExchangeAsync` / `ConfigureAuditExchangeAsync` are structurally identical. Same for the queue methods.

**Recommended Fix:** Extract `ConfigureDeclareExchangeAsync(name)` and `ConfigureDeclareUtilityQueueAsync(name)`.

---

#### R-071 — `MessageDispatcher.Dispatch`: `RunProcessors` local function obscures control flow

**Severity:** Low
**Areas:** CLEAN Code (Clarity)
**File:** `ServiceConnect/Services/MessageDispatcher.cs`, lines 69–85

16-line local function passed as a delegate makes the containing 87-line method harder to read.

**Recommended Fix:** Extract as a private instance method on `MessageDispatcher`.

---

#### R-072 — `HeaderDecoder.Decode` uses `value?.ToString()` as a catch-all

**Severity:** Low
**Areas:** CLEAN Code (Assertive)
**File:** `ServiceConnect.Interfaces/HeaderDecoder.cs`, lines 7–13

Silently converts arbitrary non-string types to strings. No warning when non-string/non-byte[] header values are decoded.

**Recommended Fix:** Add a `Debug.Assert` or log when the fallback branch is hit.

---

#### R-073 — `Bus.IsConnected` name doesn't match semantics

**Severity:** Low
**Areas:** CLEAN Code (Naming)
**File:** `ServiceConnect/Bus.cs`, line 62

`IBus.IsConnected` returns `_consuming` (logical state), while `IConsumer.IsConnected` returns transport connection state.

**Recommended Fix:** Rename `IBus.IsConnected` to `IBus.IsConsuming`.

---

#### R-074 — `SlidingDetails.CanExpire` uses inverted condition

**Severity:** Low
**Areas:** CLEAN Code (Clarity)
**File:** `ServiceConnect.Persistence.InMemory/SlidingDetails.cs`, lines 21–25

`return 0 > tryAfter.Ticks;` requires mental negation.

**Recommended Fix:** Rewrite as `return tryAfter.Ticks <= 0;`.

---

#### R-075 — `ServiceConnectBuilder.AdditionalRegistrations` is a fully mutable `List<Action<…>>`

**Severity:** Low
**Areas:** CLEAN Code (Encapsulation)
**File:** `ServiceConnect/ServiceConnectBuilder.cs`, line 11

External code can `.Remove()`, `.Clear()`, or `.Insert()`, removing registrations added by earlier extensions.

**Recommended Fix:** Expose as `IReadOnlyList<>` with an `AddRegistration(...)` method.

---

#### R-076 — `RabbitMqConsumerHost._autoDelete` mutable field overwritten by `StartConsumingAsync`

**Severity:** Low
**Areas:** CLEAN Code (Encapsulation)
**File:** `ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs`, lines 33, 80

Constructor sets `_autoDelete` from config, then `StartConsumingAsync` can overwrite it — hidden temporal coupling.

**Recommended Fix:** Use a local variable: `bool effectiveAutoDelete = autoDelete ?? _autoDelete;`.

---

#### R-077 — `InMemoryProcessManagerFinder` / `InMemoryAggregatorPersistor` constructors accept unused parameters

**Severity:** Low
**Areas:** CLEAN Code, C#/.NET Bad Practices, Technical Debt
**File:** `ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs`, line 15
**File:** `ServiceConnect.Persistence.InMemory/InMemoryAggregatorPersistor.cs`, line 8

Both accept `connectionString`, `databaseName` (+ `collectionName`) but do nothing with them.

**Recommended Fix:** Remove the parameters. Update `InMemoryPersistenceExtensions` to use parameterless constructors.

---

#### R-078 — `TimeoutData.Time` uses `DateTime` instead of `DateTimeOffset`

**Severity:** Low
**Areas:** C#/.NET Bad Practices
**File:** `ServiceConnect.Interfaces/TimeoutData.cs`, line 26
**File:** `ServiceConnect.Interfaces/TimeoutsBatch.cs`, line 12

`DateTime` has ambiguous `Kind`. Convention (UTC) is not enforced by the type system.

**Recommended Fix:** Change to `DateTimeOffset` and update all callers.

---

#### R-079 — `Retry.DoAsync` does not accept `CancellationToken`

**Severity:** Low
**Areas:** C#/.NET Bad Practices
**File:** `ServiceConnect.Client.RabbitMQ/Retry.cs`, lines 7, 13

`Task.Delay` cannot be cancelled during retry backoff. Shutdown blocks for up to 5 minutes.

**Recommended Fix:** Add `CancellationToken cancellationToken = default` and pass it to `Task.Delay`.

---

#### R-080 — `IMessageHandler<T>.Context` has a public setter on the interface

**Severity:** Low
**Areas:** CLEAN Code (Encapsulation)
**File:** `ServiceConnect.Interfaces/IMessageHandler.cs`, line 15
**File:** `ServiceConnect.Interfaces/IProcessHandler.cs`, line 18
**File:** `ServiceConnect.Interfaces/IStreamHandler.cs`, line 15

All three handler interfaces expose `Context`/`Stream` with public setters. Required for compiled-expression dispatch, but allows handler code to overwrite context.

**Recommended Fix:** Document as infrastructure-only, or pass `Context` as a parameter to `HandleAsync`.

---

#### R-081 — `ConnectionFactoryBuilder` hardcodes `VirtualHost = "/"`

**Severity:** Low
**Areas:** C#/.NET Bad Practices
**File:** `ServiceConnect.Client.RabbitMQ/ConnectionFactoryBuilder.cs`, lines 31, 52–53

Sets `VirtualHost = "/"` then conditionally overwrites. The first assignment is dead for any caller that provides a virtual host.

**Recommended Fix:** Extract to `private const string DefaultVirtualHost = "/"`.

---

#### R-082 — Security: No TLS enforcement; plain-text AMQP allowed by default

**Severity:** Low
**Areas:** Security
**File:** `ServiceConnect/Configuration/TransportConfiguration.cs`, line 31

`SslEnabled` defaults to `false`. No warning when TLS is disabled on a non-localhost host.

**Recommended Fix:** Log a warning when `SslEnabled == false && Host != "localhost"`.

---

#### R-083 — Security: `CertificateValidationCallback` can silently disable cert validation

**Severity:** Low
**Areas:** Security
**File:** `ServiceConnect/Configuration/TransportConfiguration.cs`, lines 48–54

A caller passing `(_, _, _, _) => true` silently bypasses all TLS validation.

**Recommended Fix:** Log a warning at startup when `CertificateValidationCallback` is non-null.

---

#### R-084 — Security: `AllowInsecureTls` on MongoDB can disable all cert validation

**Severity:** Low
**Areas:** Security
**File:** `ServiceConnect.Persistence.MongoDb/MongoDbSslOptions.cs`, line 19

No startup warning logged when enabled.

**Recommended Fix:** Log a warning when `AllowInsecureTls == true`.

---

#### R-085 — Security: `SequenceId` in stream protocol is unbounded; DoS via stream slot exhaustion

**Severity:** Low
**Areas:** Security
**File:** `ServiceConnect/Services/Processors/StreamProcessor.cs`, lines 53–55, 66

Any arbitrary string is accepted as `SequenceId`. No max-active-stream count. 5-minute eviction TTL allows accumulation.

**Recommended Fix:** Enforce `Guid.TryParse` on `SequenceId`. Add a maximum active-stream count. Consider shorter eviction TTL.

---

#### R-086 — Security: `LastPacketNumber` unbounded; `IsComplete` O(N) loop with attacker-controlled N

**Severity:** Low
**Areas:** Security
**File:** `ServiceConnect/Services/MessageBusReadStream.cs`, lines 59–66

`LastPacketNumber = long.MaxValue - 1` causes `IsComplete()` to loop effectively forever.

**Recommended Fix:** Add upper bound: `if (lastPacketNumber > MaxPacketNumber) throw`.

---

#### R-087 — Security: Retry exponential backoff can overflow at high attempt counts

**Severity:** Low
**Areas:** Security
**File:** `ServiceConnect.Client.RabbitMQ/Retry.cs`, lines 46–50

`baseInterval * 2^retryAttempt` overflows `double` at attempt ~60 (the default `_retryCount`).

**Recommended Fix:** Cap `retryAttempt` at 52 before the multiplication.

---

#### R-088 — Security: Header mutation race — `context.Headers` is a shared mutable dictionary

**Severity:** Low
**Areas:** Security
**File:** `ServiceConnect/Services/ConsumeContext.cs`

The `headers` dictionary is passed by reference through the entire pipeline and exposed to handlers. Handlers can inject arbitrary headers used for auditing.

**Recommended Fix:** Wrap `context.Headers` in `ReadOnlyDictionary<string, object>` before exposing to user code.

---

#### R-089 — Missing XML documentation on public API surface

**Severity:** Low
**Areas:** Technical Debt
**Files:** `Message.cs`, `Envelope.cs`, `HeaderKeys.cs`, `HandlerReference.cs`, `ConsumeEventResult.cs`, `ConsumeEventArgs.cs`, `ProcessManagerToMessageMap.cs`, `RequestOptions.cs`, `SendOptions.cs`, `PublishOptions.cs`, `ServiceCollectionExtensions.cs`, `MessageTypeRegistry.cs`, `FilterPipeline.cs`

These types form the primary public surface but lack `<summary>` XML documentation.

**Recommended Fix:** Add `/// <summary>` blocks to all public types, properties, and methods in listed files.

---

### Info

---

#### R-090 — `MessageDispatcher.Dispatch`: `messageType` parameter used only in error logger

**Severity:** Info
**Areas:** Bugs & Correctness
**File:** `ServiceConnect/Services/MessageDispatcher.cs`, lines 27, 102

If `TypeName` and `FullTypeName` headers disagree, the logged type name won't match the actual type dispatched.

---

#### R-091 — `ReplyProcessor.ProcessAsync`: `messageType` parameter is unused in `ProcessReply`

**Severity:** Info
**Areas:** Bugs & Correctness
**File:** `ServiceConnect/Services/Processors/ReplyProcessor.cs`, line 27

The actual deserialization type comes from `state.ReplyType` at registration time — the parameter is misleading but harmless.

---

#### R-092 — `CacheItemPriority` is a separate file for a two-value enum

**Severity:** Info
**Areas:** CLEAN Code
**File:** `ServiceConnect.Persistence.InMemory/CacheItemPriority.cs`

Could live alongside `CacheItem.cs`. Consider making `internal`.

---

#### R-093 — `IServiceConnectConnection` interface defined in RabbitMQ package, not Interfaces

**Severity:** Info
**Areas:** CLEAN Code (Loose coupling)
**File:** `ServiceConnect.Client.RabbitMQ/IServiceConnectConnection.cs`

If a second transport ever needs this abstraction, it would need to reference the RabbitMQ package.

---

#### R-094 — `ConsumeEventResult` and `ConsumeEventArgs` could be `record` types

**Severity:** Info
**Areas:** C#/.NET Bad Practices
**File:** `ServiceConnect.Interfaces/ConsumeEventResult.cs`, `ConsumeEventArgs.cs`

Both are data-transfer objects with mutable properties. `record` types would better express immutability intent.

---

#### R-095 — `CacheProvider` and `CacheItemPriority` are `public` but purely internal implementation details

**Severity:** Info
**Areas:** C#/.NET Bad Practices
**File:** `ServiceConnect.Persistence.InMemory/CacheProvider.cs`, `CacheItemPriority.cs`

Should be `internal sealed` / `internal` to narrow the public API surface.

---

#### R-096 — `MongoDbData<T>.Locked` is an infrastructure field exposed on `IPersistenceData<T>`

**Severity:** Info
**Areas:** C#/.NET Bad Practices
**File:** `ServiceConnect.Persistence.MongoDb/MongoDbData.cs`, line 13

Has no meaning to consumers of `IPersistenceData<T>`. Should be hidden or moved to an internal sub-type.

---

#### R-097 — `NewtonsoftJsonMessageSerializer` mutates the caller's `JsonSerializerSettings` instance

**Severity:** Info
**Areas:** Security
**File:** `ServiceConnect/Services/NewtonsoftJsonMessageSerializer.cs`, lines 9–15

Sets `TypeNameHandling = TypeNameHandling.None` on the passed-in settings object — correct for security, but mutates the caller's shared instance.

**Recommended Fix:** Clone the settings before mutating.

---

#### R-098 — Telemetry `Enrich*` callbacks can exfiltrate message payload to OTel backend

**Severity:** Info
**Areas:** Security
**File:** `ServiceConnect.Telemetry/ServiceConnectInstrumentationOptions.cs`, lines 23, 37

No framework-level guardrail prevents attaching full message bodies as span tags. Existing doc warnings are appropriate.

---

## Test Coverage Gaps

The following critical paths lack dedicated test coverage (identified by the Technical Debt review):

| Area | Missing Tests |
|------|--------------|
| `MongoDbProcessManagerFinder` | Zero unit tests. All coverage through E2E only. |
| `MongoDbAggregatorPersistor` | Zero unit tests. |
| `AggregatorProcessor` timer flush | `ResetTimer`/`OnTimerFired`/`FlushAggregatorAsync` entirely untested. |
| `AggregatorProcessor.Dispose` | Dispose behavior untested. |
| `StreamProcessor` completion/eviction | Last-packet handler invocation, stale-stream eviction, `MaxTotalStreamSize` untested. |
| `HandlerProcessor` routing slip validation | Security validation (forbidden characters) has no test. |
| `RequestReplyManager` concurrency | Concurrent reply delivery, post-timeout replies untested. |
| `SendMessagePipeline` post-dispose | `ObjectDisposedException` guard untested. |
| `ProcessManagerTimeoutService` null `IBus` | Service-locator null path untested. |
| `ConsumeContext.ReplyAsync` | `InvalidOperationException` when `SourceAddress` absent untested. |

---

## Related Finding Groups

Findings that should be fixed together:

**Group A — Aggregator Processor Reliability:** R-001, R-002, R-004, R-028
**Group B — Process Manager Finder Split (SRP):** R-017, R-060 (+ related R-038, R-042)
**Group C — Consumer Topology/Lifecycle Cleanup:** R-010, R-012, R-032, R-043, R-066, R-070
**Group D — Pipeline Configuration Immutability:** R-005, R-006, R-033, R-045, R-075
**Group E — MongoDB Persistence Quality:** R-007, R-016, R-040, R-041, R-042
**Group F — Security: Message/Header Validation:** R-022, R-023, R-024, R-050, R-085, R-086, R-088
**Group G — Time Abstraction:** R-027, R-078
**Group H — Producer Cleanup/Thread Safety:** R-019, R-046, R-056
**Group I — Telemetry Hardening:** R-026, R-053

---

*Generated by Claude Code — 6 parallel review agents, consolidated and deduplicated.*
