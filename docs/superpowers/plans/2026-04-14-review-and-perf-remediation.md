# Code Review & Performance Remediation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolve all 170 findings from the code review report (`docs/code-review-report.md`) and performance audit report (`docs/performance-review-report.md`) in priority order across 6 phases.

**Architecture:** Each phase groups related findings by subsystem so changes are cohesive and testable together. Phases are ordered by risk: critical safety first, then throughput, security, architecture, and polish. Cross-report duplicates are merged (noted with both IDs).

**Tech Stack:** C# / .NET 8+10, xUnit, Moq, RabbitMQ.Client, MongoDB.Driver, System.Reactive, OpenTelemetry

**Reports:**
- Code review: 98 findings (7 Critical, 20 High, 30 Medium, 32 Low, 9 Info)
- Performance: 72 findings (8 Critical, 25 High, 24 Medium, 15 Low)
- Cross-report duplicates: ~15 (merged below, marked with both IDs)

---

## Phase 1: Critical Safety & Data Integrity

**Goal:** Fix bugs that can cause data loss, double-processing, crashes, or resource leaks in production.
**Estimated scope:** ~20 findings

### Task 1.1 — Aggregator Processor Reliability

*Related groups: CR-A, Perf-G*

| ID | Title | File |
|----|-------|------|
| R-001 | Non-atomic batch remove allows double-processing | `AggregatorProcessor.cs:101-107` |
| R-002 | `Dispose` races with in-flight flushes | `AggregatorProcessor.cs:115-125` |
| R-004 | `AggregatorDocument.Id` never assigned before MongoDB insert | `MongoDbAggregatorPersistor.cs:55-75` |
| R-028 | Double-flush window between timer swap | `AggregatorProcessor.cs:54-68` |
| P-013 | New `Timer` allocated per message (combine: use `Timer.Change`) | `AggregatorProcessor.cs:54-68` |

**What to do:**
- [ ] R-001: Replace one-by-one `RemoveDataAsync` loop with a bulk `RemoveAllAsync(name)` on `IAggregatorPersistor`. Add `RemoveAllAsync` to the interface and both implementations (InMemory + MongoDB). Wrap Execute+Remove in try/catch so partial failures log.
- [ ] R-002: Implement `IAsyncDisposable`. Use `CancellationTokenSource` to cancel in-flight flushes. `await` pending flushes before disposing semaphores. Use `Interlocked.Exchange` for disposed guard.
- [ ] R-028 + P-013: Replace `new Timer(...)` / `previous?.Dispose()` pattern with a single `Timer` per aggregator name, reused via `Timer.Change(dueTime, Timeout.InfiniteTimeSpan)`. Store timers in the `_flushTimers` dict. Wrap previous dispose in try/catch.
- [ ] R-004: Assign `Id = Guid.NewGuid()` in `InsertDataAsync` object initializer. Add unique index on `Id`. Remove dead `Version` property if aggregator docs don't use optimistic concurrency.
- [ ] Write/update tests for timer flush, dispose-during-flush, and bulk remove behavior.
- [ ] Commit.

---

### Task 1.2 — CacheProvider Memory Leak & Timer Lifecycle

*Related groups: CR (R-003, R-030), Perf-G (P-024, P-047)*

| ID | Title | File |
|----|-------|------|
| R-003 | `PurgeNormalPriorities` leaks `_slidingTime` entries | `CacheProvider.cs:104-108` |
| R-030 | Rx `Observable.Timer` with silent error swallowing | `CacheProvider.cs:136-156` |
| P-024 | Rx timer subscriptions never disposed (memory leak) | `CacheProvider.cs:134-142` |
| P-047 | `Remove+Add` causes timer churn on update | `InMemoryProcessManagerFinder.cs:183-193` |

**What to do:**
- [ ] P-024 + R-030: Replace `Observable.Timer` with `System.Threading.Timer`. Store `IDisposable`/`Timer` per key. Dispose on Remove/Update. Remove `System.Reactive` dependency if no other usages.
- [ ] R-003: In `PurgeNormalPriorities` and `Remove(key)`, also call `_slidingTime.TryRemove(key, out _)` after removing from `_cache`.
- [ ] P-047: Add `CacheProvider.Update(key, value)` method that replaces value in `_cache` without resetting the expiry timer.
- [ ] Write tests for: purge cleans up sliding time, remove cleans up timers, update doesn't churn timers.
- [ ] Commit.

---

### Task 1.3 — Process Manager Processor Safety

| ID | Title | File |
|----|-------|------|
| R-018 | New data inserted even when handler throws | `ProcessManagerProcessor.cs:64-72` |

**What to do:**
- [ ] Wrap handler invocation and `InsertDataAsync` so that if the handler throws, no data is persisted. Catch handler exceptions, log, and re-throw without writing the PM record.
- [ ] Write test: handler throws -> verify `InsertDataAsync` was NOT called.
- [ ] Commit.

---

### Task 1.4 — RabbitMQ Consumer Host Null Race

| ID | Title | File |
|----|-------|------|
| R-020 | `_model` null race during shutdown | `RabbitMqConsumerHost.cs:98-136` |
| R-012 | Channel not closed or disposed in `Consumer.DisposeAsync` | `Consumer.cs:122-133` |

**What to do:**
- [ ] R-020: Capture `_model` into a local variable at the start of `EventAsync`. Use the local in the `finally` block for `BasicAckAsync`/`BasicNackAsync`.
- [ ] R-012: In `Consumer.DisposeAsync`, close and dispose the channel before nulling: `if (_model is { IsOpen: true }) await _model.CloseAsync(); _model?.Dispose(); _model = null;`
- [ ] Write tests for dispose-during-processing scenario.
- [ ] Commit.

---

### Task 1.5 — RequestReplyManager Race Condition

| ID | Title | File |
|----|-------|------|
| R-021 | Race between timeout and reply snapshot in `SendRequestMultiAsync` | `RequestReplyManager.cs:93-121` |

**What to do:**
- [ ] Remove the `_pendingRequests` entry atomically when the timeout fires (inside the registration callback), not in the `finally` block.
- [ ] Write test: concurrent reply delivery with timeout firing simultaneously.
- [ ] Commit.

---

### Task 1.6 — Pipeline Configuration Mutability (Critical Encapsulation)

*Related groups: CR-D*

| ID | Title | File |
|----|-------|------|
| R-005 | `IPipelineConfiguration` exposes mutable `IList<Type>` | `IPipelineConfiguration.cs:5-9`, `PipelineConfiguration.cs:7-11` |
| R-006 | `ProcessManagerToMessageMap.PropertiesHierarchy` is mutable public dict | `ProcessManagerToMessageMap.cs:7` |
| P-068 | Mutable `List<Type>` for read-only runtime collections | `PipelineConfiguration.cs` |

**What to do:**
- [ ] R-005 + P-068: Change `IPipelineConfiguration` property types to `IReadOnlyList<Type>`. On the concrete `PipelineConfiguration`, keep internal `List<Type>` backing fields and expose mutation only through the builder's `Add*` methods. Freeze to arrays after builder completes.
- [ ] R-006: Change `PropertiesHierarchy` to `IReadOnlyDictionary<string, Type>` with `init`-only setter. Change `MessageProp` and `MessageType` to `{ get; init; }`.
- [ ] Update all call sites that add to pipeline lists to go through builder methods.
- [ ] Commit.

---

### Task 1.7 — MongoDB Uncached Reflection (Critical Hot Path)

*Related groups: CR-E, Perf-E*
*Duplicate: R-007 = P-006*

| ID | Title | File |
|----|-------|------|
| R-007 / P-006 | Uncached `MakeGenericMethod` per `InsertDataAsync` call | `MongoDbProcessManagerFinder.cs:125-128` |

**What to do:**
- [ ] Cache compiled delegates in a `ConcurrentDictionary<Type, Func<...>>`, mirroring `InMemoryProcessManagerFinder.BuildMemoryDataFactory`.
- [ ] Write test: insert with multiple types hits cache on second call.
- [ ] Commit.

---

## Phase 2: Critical & High Performance — Hot Path

**Goal:** Address the top throughput-limiting allocations and I/O on the message hot path. Expected result: 3-5x reduction in per-message allocations.
**Estimated scope:** ~30 findings

### Task 2.1 — Middleware Chain Caching

*Perf Group A*

| ID | Title | File |
|----|-------|------|
| P-001 | Middleware chain rebuilt with closures per message | `MessageDispatcher.cs:69-97` |

**What to do:**
- [ ] Build and cache the middleware chain once at startup in a `Lazy<MessageProcessingDelegate>`, mirroring `SendMessagePipeline._publishChain`.
- [ ] Write test: dispatch two messages -> verify middleware chain delegate is same instance.
- [ ] Commit.

---

### Task 2.2 — JSON Serializer Optimization

*Perf Group B*

| ID | Title | File |
|----|-------|------|
| P-002 | Intermediate string allocation on every serialize/deserialize | `NewtonsoftJsonMessageSerializer.cs:26-28, 44-45` |

**What to do:**
- [ ] Create and cache `JsonSerializer.Create(_settings)` as a field.
- [ ] Serialize: Use `JsonTextWriter` over `MemoryStream` to write UTF-8 directly, avoiding intermediate string.
- [ ] Deserialize: Use `JsonTextReader` over `StreamReader` wrapping a `MemoryStream` from the byte array.
- [ ] Clone settings before mutating (R-097 info fix — do it here since we're touching the file).
- [ ] Write benchmarks or tests verifying byte[] round-trip still works correctly.
- [ ] Commit.

---

### Task 2.3 — Message Body Buffer Pooling

| ID | Title | File |
|----|-------|------|
| P-003 | `args.Body.ToArray()` on every message | `RabbitMqConsumerHost.cs:174` |

**What to do:**
- [ ] Short-term: Use `ArrayPool<byte>.Shared.Rent()` to get a buffer, copy `args.Body.Span` into it, and return the buffer after processing completes.
- [ ] Ensure the rented buffer is returned in a `finally` block.
- [ ] Write test: message processing returns buffer to pool.
- [ ] Commit.

---

### Task 2.4 — Exchange Declare Caching & Producer Type Caching

*Perf Group C*

| ID | Title | File |
|----|-------|------|
| P-004 | Exchange declare round-trip on every publish | `Producer.cs:110` |
| P-016 | Uncached `type.FullName` and `type.AssemblyQualifiedName` | `Producer.cs:248-251` |
| P-017 | Exchange name `type.FullName.Replace(".", "")` per publish | `Producer.cs:110` |
| P-049 | `Assembly.GetEntryAssembly()` + `Process.GetCurrentProcess()` on reconnect | `Producer.cs:82-83` |

**What to do:**
- [ ] P-004 + P-017: Cache declared exchange names in a `ConcurrentDictionary<string, bool>` (or `HashSet<string>` with lock). Skip `ExchangeDeclareAsync` after first success for each name. Cache the computed exchange name (`type.FullName.Replace(".", "")`) alongside.
- [ ] P-016: Cache `type.FullName` and `type.AssemblyQualifiedName` in a `ConcurrentDictionary<Type, (string FullName, string AQN)>`.
- [ ] P-049: Cache `Assembly.GetEntryAssembly()?.GetName().Name` and `Process.GetCurrentProcess().Id` as `static readonly` fields.
- [ ] Write test: two publishes of same type -> verify only one exchange declare.
- [ ] Commit.

---

### Task 2.5 — Process Manager Mapper & Expression Caching

*Perf Group D, duplicate: R-034 = P-005*

| ID | Title | File |
|----|-------|------|
| P-005 / R-034 | `new DefaultProcessManagerPropertyMapper()` per message | `ProcessManagerProcessor.cs:41` |
| P-007 | Uncached Expression tree built per `FindDataAsync` call | `MongoDbProcessManagerFinder.cs:80-100` |
| P-046 | Double `FirstOrDefault` LINQ scan in `FindDataAsync` | `InMemoryProcessManagerFinder.cs:40`, `MongoDbProcessManagerFinder.cs:48` |

**What to do:**
- [ ] P-005 / R-034: Cache `IProcessManagerPropertyMapper` per handler type in a `ConcurrentDictionary<Type, IProcessManagerPropertyMapper>`. Build once on first use.
- [ ] P-007: Cache the compiled filter expression or `FilterDefinition<MongoDbData<T>>` keyed by `(Type, PropertiesHierarchy)`.
- [ ] P-046: Replace double `FirstOrDefault` with single-pass `foreach` with type cache.
- [ ] Write tests for mapper caching and filter expression reuse.
- [ ] Commit.

---

### Task 2.6 — Telemetry Header Decode Optimization

*Perf Group J*

| ID | Title | File |
|----|-------|------|
| P-008 | Full header decode dictionary per consumed message | `ServiceConnectActivitySource.cs:110-114` |
| P-031 | `ConsumeContext` properties re-decode headers on every access | `ConsumeContext.cs:12, 16` |
| P-033 | `ConsumeEventArgs.Headers` default dictionary allocated then replaced | `ConsumeEventArgs.cs:9-15` |

**What to do:**
- [ ] P-008: Decode only the specific headers needed (`DestinationAddress`, `MessageId`) via targeted `TryGetValue` calls instead of building a full dictionary.
- [ ] P-031: Cache decoded `MessageId` and `CorrelationId` with lazy initialization: `_messageId ??= Decode(...)`.
- [ ] P-033: Initialize `Headers` to `null!`. Use `??=` in getter if needed.
- [ ] Write test: ConsumeContext property access returns cached value on second call.
- [ ] Commit.

---

### Task 2.7 — Envelope & Filter Fast Path

| ID | Title | File |
|----|-------|------|
| P-009 | Envelope allocated per outgoing message even when no filters | `Bus.cs:298-335` |

**What to do:**
- [ ] Check filter count before constructing `Envelope`. If no outgoing filters are registered, skip Envelope creation and go straight to send.
- [ ] Write test: publish with no filters -> verify no Envelope allocation (can check via mock or allocation counter).
- [ ] Commit.

---

### Task 2.8 — Transport Header Handling

*Perf Group H*

| ID | Title | File |
|----|-------|------|
| P-014 | LINQ `Select` header copy per message | `Producer.cs:191-215` |
| P-015 | `DateTime.UtcNow.ToString("O")` per message | `Producer.cs:244`, `RabbitMqConsumerHost.cs:159,177` |
| P-018 | Header dictionary copy per message (wrong capacity) | `RabbitMqConsumerHost.cs:144-152` |
| P-019 | Double dictionary lookup on type name | `RabbitMqConsumerHost.cs:164` |

**What to do:**
- [ ] P-014: Replace LINQ `Select` with `foreach` loop into pre-sized `Dictionary(count, StringComparer.Ordinal)`.
- [ ] P-015: Use `stackalloc` buffer with `DateTime.TryFormat` (or `Span<char>`) to avoid 28-char string allocation. (Note: this interacts with R-027 TimeProvider — for now, just optimize the formatting; TimeProvider comes in Phase 4.)
- [ ] P-018: Size hint as `(sourceHeaders?.Count ?? 4) + 3` to account for consumer-added headers.
- [ ] P-019: Replace `ContainsKey` + indexer with `TryGetValue`.
- [ ] Write tests verifying header dictionary has correct capacity and lookup works.
- [ ] Commit.

---

### Task 2.9 — DI Resolution Caching

*Perf Group F*

| ID | Title | File |
|----|-------|------|
| P-012 | `IBus` re-resolved from DI per message | `HandlerProcessor.cs:39`, `ProcessManagerProcessor.cs:59` |
| P-020 | `GetServices` allocates new enumerable per hierarchy level | `HandlerProcessor.cs:27-32` |
| P-048 | `IAggregatorPersistor` and aggregator re-resolved per message | `AggregatorProcessor.cs:32, 90, 98` |

**What to do:**
- [ ] P-012: Inject `IBus` into `HandlerProcessor` and `ProcessManagerProcessor` constructors instead of resolving per message.
- [ ] P-020: Cache resolved handler instances on descriptors at startup for singletons. For transient handlers, still resolve per message but cache the type list.
- [ ] P-048: Inject `IAggregatorPersistor` at construction instead of resolving per message.
- [ ] Update DI registrations in `ServiceCollectionExtensions`.
- [ ] Write tests verifying processors work with injected dependencies.
- [ ] Commit.

---

### Task 2.10 — Routing Slip Reflection Cache

*Duplicate: R-035 = P-010*

| ID | Title | File |
|----|-------|------|
| P-010 / R-035 | Uncached `MakeGenericMethod` in `ForwardRoutingSlipAsync` | `HandlerProcessor.cs:85-86` |

**What to do:**
- [ ] Cache compiled delegates in a `ConcurrentDictionary<Type, Func<...>>`.
- [ ] Write test: two routing slip forwards of same type -> second uses cached delegate.
- [ ] Commit.

---

### Task 2.11 — Task.FromResult Caching & Misc High Perf

| ID | Title | File |
|----|-------|------|
| P-011 | Uncached `Task.FromResult` at 8+ return sites | `StreamProcessor.cs:47-122` |
| P-027 | O(N^2) `IsComplete()` linear scan per packet | `MessageBusReadStream.cs:59-67` |
| P-028 | `MemoryStream` + `ToArray()` doubles memory | `MessageBusReadStream.cs:50-57` |
| P-029 | Full LINQ `.ToDictionary()` copy in `ToNullableHeaders` | `HeaderHelpers.cs:14` |
| P-030 | Per-endpoint header dict inside `_publishLock` | `Producer.cs:126-131` |
| P-032 | Boxing + string round-trip for retry count | `MessageRetryHandler.cs:33-35` |

**What to do:**
- [ ] P-011: Add `private static readonly Task<ProcessResult> NotHandledTask = Task.FromResult(ProcessResult.NotHandled);` (and `HandledTask`). Replace all `Task.FromResult` return sites.
- [ ] P-027: Track received count via `Interlocked.Increment` on a `_receivedCount` field. `IsComplete` becomes O(1): `_receivedCount == LastPacketNumber + 1`.
- [ ] P-028: Pre-size `MemoryStream` with `_totalBytesWritten`. Or allocate `byte[_totalBytesWritten]` and `BlockCopy` directly.
- [ ] P-029: Replace LINQ `.ToDictionary()` with `foreach` loop into pre-sized `Dictionary(count, StringComparer.Ordinal)`.
- [ ] P-030: Build base headers once outside the per-endpoint loop. Clone only `DestinationAddress` per endpoint.
- [ ] P-032: Direct unbox: `raw is int i ? i : int.TryParse(raw?.ToString(), out var parsed) ? parsed : 0`.
- [ ] Write tests for IsComplete O(1), MemoryStream sizing, retry count unboxing.
- [ ] Commit.

---

### Task 2.12 — MongoDB Persistence Performance

*Perf Group E*

| ID | Title | File |
|----|-------|------|
| P-021 | Multiple `MongoClient` instances (3x connection pools) | `MongoDbPersistenceExtensions.cs:13-28` |
| P-022 / R-042 | Missing timeout collection indexes | `MongoDbProcessManagerFinder.cs:335-351` |
| P-023 / R-040 | Synchronous `CreateOne` from async context | `MongoDbProcessManagerFinder.cs:335-351` |
| P-053 | Missing indexes on aggregator `Name` and `DataBson.CorrelationId` | `MongoDbAggregatorPersistor.cs` |
| P-055 | No projection in `GetTimeoutsBatchAsync` (full doc fetch) | `MongoDbProcessManagerFinder.cs:261-277` |
| P-057 | `DeleteManyAsync` for unique key (should be `DeleteOneAsync`) | `MongoDbProcessManagerFinder.cs:214` |

**What to do:**
- [ ] P-021: Inject the DI-registered `IMongoClient` into `MongoDbProcessManagerFinder` and `MongoDbAggregatorPersistor` constructors. Remove internal `MongoClientFactory.Create()` calls.
- [ ] P-022 / R-042: Add compound indexes `{ Locked: 1, Time: 1 }` and `{ LockedBy: 1, Locked: 1 }` at startup.
- [ ] P-023 / R-040: Replace synchronous `collection.Indexes.CreateOne(...)` with `CreateOneAsync(...)`. Propagate `CancellationToken`. Move to startup initialization.
- [ ] P-053: Add compound indexes on `Name` and `DataBson.CorrelationId` at startup.
- [ ] P-055: Use projection for `GetTimeoutsBatchAsync` — project out large fields like `Headers`.
- [ ] P-057: Replace `DeleteManyAsync` with `DeleteOneAsync` for unique-key deletes.
- [ ] Commit.

---

## Phase 3: Security Hardening

**Goal:** Validate inputs at trust boundaries, enforce size limits, prevent routing/reply hijacking.
**Estimated scope:** ~15 findings

### Task 3.1 — Message Size Enforcement

| ID | Title | File |
|----|-------|------|
| R-022 | Message size limit declared but never enforced | `Producer.cs:12,42,189`, `RabbitMqConsumerHost.cs:98-192` |

**What to do:**
- [ ] Add size check in `Producer.PublishAsync`, `SendAsync`, `SendBytesAsync` before publishing. Throw `InvalidOperationException` if body exceeds `MaximumMessageSize`.
- [ ] Add size check in `RabbitMqConsumerHost.ProcessMessageAsync` before `Body.ToArray()`. Nack messages exceeding the limit.
- [ ] Write tests: oversized message on publish throws; oversized inbound message is nacked.
- [ ] Commit.

---

### Task 3.2 — Reply & Routing Destination Validation

| ID | Title | File |
|----|-------|------|
| R-023 | `SourceAddress` header accepted without validation (reply hijacking) | `ConsumeContext.cs:21-32` |
| R-024 | `RoutingSlip` header trusted with insufficient validation | `HandlerProcessor.cs:60-88` |

**What to do:**
- [ ] R-023: Validate `sourceAddress` against `IQueueConfiguration.QueueMappings` or a configurable allowlist before using it as reply destination. Log and reject if not in allowlist.
- [ ] R-024: Validate routing slip destinations against registered queue mappings or a startup-configured allowlist. Add config option `EnableRoutingSlipProcessing` (default true) to allow disabling entirely.
- [ ] Write tests: invalid source address rejected; invalid routing slip destination rejected; routing slip disabled by config.
- [ ] Commit.

---

### Task 3.3 — Header Count & Size Limits

| ID | Title | File |
|----|-------|------|
| R-050 | No inbound header count or total header size limit | `RabbitMqConsumerHost.cs:144-152` |
| R-088 | Header mutation race — shared mutable dictionary | `ConsumeContext.cs` |

**What to do:**
- [ ] R-050: Add `MaxHeaderCount` (default 64) and `MaxHeaderValueBytes` (default 8192) to `ITransportConfiguration`. Check in `ProcessMessageAsync` before copying. Nack messages that exceed.
- [ ] R-088: Wrap `context.Headers` in `ReadOnlyDictionary<string, object>` before exposing to user handler code.
- [ ] Write tests: excessive headers nacked; handler cannot mutate headers.
- [ ] Commit.

---

### Task 3.4 — Stream Protocol Validation

| ID | Title | File |
|----|-------|------|
| R-085 | `SequenceId` unbounded; no max active streams | `StreamProcessor.cs:53-55, 66` |
| R-086 | `LastPacketNumber` unbounded; O(N) loop with attacker-controlled N | `MessageBusReadStream.cs:59-66` |

**What to do:**
- [ ] R-085: Enforce `Guid.TryParse` on `SequenceId`. Add `MaxActiveStreams` config (default 1000). Reject new streams when limit reached.
- [ ] R-086: Add `MaxPacketNumber` upper bound (e.g., 100_000). Throw if `lastPacketNumber > MaxPacketNumber`.
- [ ] Write tests: non-GUID sequence ID rejected; max streams enforced; oversized packet number rejected.
- [ ] Commit.

---

### Task 3.5 — Remaining Security Findings

| ID | Title | File |
|----|-------|------|
| R-048 | Sensitive config defaults: localhost without auth | `PersistenceConfiguration.cs:7`, `TransportConfiguration.cs:19` |
| R-049 | `CertPassphrase` stored as plain string | `TransportConfiguration.cs:40-41` |
| R-051 | Exception details in error-queue headers may leak state | `MessageRetryHandler.cs:54-59` |
| R-052 | `IncludeMachineNameInHeaders` leaks topology | `RabbitMqConsumerHost.cs:161`, `Producer.cs:244-246` |
| R-082 | No TLS enforcement; plain-text AMQP allowed by default | `TransportConfiguration.cs:31` |
| R-083 | `CertificateValidationCallback` can disable cert validation | `TransportConfiguration.cs:48-54` |
| R-084 | `AllowInsecureTls` on MongoDB can disable cert validation | `MongoDbSslOptions.cs:19` |
| R-087 | Retry exponential backoff can overflow | `Retry.cs:46-50` |

**What to do:**
- [ ] R-048: Log a warning at startup when localhost/null-credential defaults are detected in production.
- [ ] R-049: Add XML doc on `CertPassphrase` noting it as sensitive. Consider factory/delegate pattern.
- [ ] R-051: Provide configurable `Func<Exception, object>` for error metadata sanitization. Default: type name + message (no stack trace).
- [ ] R-052: Add `<remarks>` doc warning that `IncludeMachineNameInHeaders` discloses internal host names.
- [ ] R-082: Log warning when `SslEnabled == false && Host != "localhost"`.
- [ ] R-083: Log warning at startup when `CertificateValidationCallback` is non-null.
- [ ] R-084: Log warning when `AllowInsecureTls == true`.
- [ ] R-087: Cap `retryAttempt` at 52 before the multiplication in `CalculateBackoff`.
- [ ] Write tests for: warning logs emitted, backoff cap.
- [ ] Commit.

---

## Phase 4: Architecture, Design & Testability

**Goal:** Improve code structure, enforce SRP, introduce TimeProvider, clean up DI patterns, and make the codebase more testable.
**Estimated scope:** ~25 findings

### Task 4.1 — Bus Constructor & Registry Abstraction

| ID | Title | File |
|----|-------|------|
| R-008 | `Bus` constructor has 14 parameters; depends on concrete registry types | `Bus.cs:19-22, 30-60` |
| R-009 | `Bus.CreateStream<T>` silently ignores `message` parameter | `Bus.cs:188-194` |
| R-073 | `Bus.IsConnected` name doesn't match semantics | `Bus.cs:62` |

**What to do:**
- [ ] R-008: Introduce `IRegistryInitialiser` (or individual registry interfaces) in `ServiceConnect.Interfaces`. Register as interfaces in DI. Reduce `Bus` constructor to essential dependencies. Remove no-op discard lines.
- [ ] R-009: Either serialize and send `message` as stream-open envelope, or remove the parameter from `IBus.CreateStream<T>` and `Bus.CreateStream<T>`.
- [ ] R-073: Rename `IBus.IsConnected` to `IBus.IsConsuming` to match the actual semantics.
- [ ] Update all call sites. Write test verifying `IsConsuming` reflects correct state.
- [ ] Commit.

---

### Task 4.2 — Consumer Topology & Lifecycle Cleanup

*Related group: CR-C*

| ID | Title | File |
|----|-------|------|
| R-010 | `Consumer` violates SRP: topology + lifecycle + dual constructors | `Consumer.cs:27-42, 65-120` |
| R-011 | `Consumer` and `RabbitMqConsumerHost` store `CancellationToken` as field | `Consumer.cs:25,49`, `RabbitMqConsumerHost.cs:33` |
| R-032 | Broad `Exception` catch in all topology declarations | `Consumer.cs (8 catch blocks)`, `Producer.cs:264-267` |
| R-043 | Non-`readonly` mutable fields in `Consumer` | `Consumer.cs:12-21` |
| R-066 | `Consumer._model` retained but only used during startup | `Consumer.cs:11-12, 63-64` |
| R-070 | `ConfigureError*` and `ConfigureAudit*` are near-identical | `Consumer.cs:195-248` |

**What to do:**
- [ ] R-010 + R-070: Extract topology provisioning into a `RabbitMqTopologyProvisioner` class. Deduplicate exchange/queue declaration methods into `ConfigureDeclareExchangeAsync(name)` and `ConfigureDeclareUtilityQueueAsync(name)`. Chain/merge constructors.
- [ ] R-011: Pass `CancellationToken` as method parameter. Remove stored fields `_consumingCt`.
- [ ] R-032: Catch only `OperationInterruptedException` (AMQP PRECONDITION_FAILED). Re-throw on initial startup.
- [ ] R-043: Move configuration extraction to the constructor, making fields `readonly`. Or use a settings record.
- [ ] R-066: Dispose the setup channel at the end of `StartConsumingAsync`.
- [ ] Write tests for topology provisioning, initial startup failure propagation.
- [ ] Commit.

---

### Task 4.3 — Process Manager Finder SRP Split

*Related group: CR-B*

| ID | Title | File |
|----|-------|------|
| R-017 | Both finders implement `IProcessManagerFinder` + `ITimeoutStore` | `MongoDbProcessManagerFinder.cs`, `InMemoryProcessManagerFinder.cs` |
| R-016 | `MongoDbData<T>.Name` and `Locked` never populated on insert | `MongoDbData.cs:12-13` |
| R-038 | `InMemoryProcessManagerFinder` holds static mutable state | `InMemoryProcessManagerFinder.cs:29-33` |

**What to do:**
- [ ] R-017: Extract `MongoDbTimeoutStore : ITimeoutStore` and `InMemoryTimeoutStore : ITimeoutStore`. They can share DB/cache via DI.
- [ ] R-016: Remove `Name` and `Locked` from `MongoDbData<T>`. If timeout locking is needed, add a dedicated `MongoDbTimeoutData` type.
- [ ] R-038: Extract static `CompiledPredicates` and `MemoryDataFactories` to a singleton `ProcessManagerPredicateCache` registered in DI.
- [ ] Update DI registrations. Write tests for split classes.
- [ ] Commit.

---

### Task 4.4 — TimeProvider Abstraction

*Related group: CR-G*

| ID | Title | File |
|----|-------|------|
| R-027 | `DateTime.UtcNow` scattered throughout production code | Multiple files (12+ locations) |
| R-078 | `TimeoutData.Time` uses `DateTime` instead of `DateTimeOffset` | `TimeoutData.cs:26`, `TimeoutsBatch.cs:12` |

**What to do:**
- [ ] R-027: Introduce .NET 8+ `TimeProvider` abstraction. Inject it into all classes that use `DateTime.UtcNow`: `MessageRetryHandler`, `RabbitMqConsumerHost`, `Producer`, `InMemoryProcessManagerFinder`, `SlidingDetails`, `CacheProvider`, `MongoDbProcessManagerFinder`, `StreamProcessor`.
- [ ] R-078: Change `TimeoutData.Time` and `TimeoutsBatch.NextQueryTime` to `DateTimeOffset`. Update all callers.
- [ ] Register `TimeProvider.System` in DI. Use `FakeTimeProvider` in tests.
- [ ] Write tests using `FakeTimeProvider` for cache expiry, timeout scheduling.
- [ ] Commit.

---

### Task 4.5 — ServiceCollectionExtensions Decomposition

| ID | Title | File |
|----|-------|------|
| R-025 | `AddServiceConnect` is 162 lines | `ServiceCollectionExtensions.cs:12-174` |

**What to do:**
- [ ] Extract `RegisterConfiguration`, `RegisterCoreServices`, `RegisterProcessors`, `RegisterHandlers`, `RegisterBus` helpers.
- [ ] Keep `AddServiceConnect` as a ~10-line coordinator calling each helper.
- [ ] Write test: verify all expected services are registered.
- [ ] Commit.

---

### Task 4.6 — Producer Cleanup & Thread Safety

*Related group: CR-H*

| ID | Title | File |
|----|-------|------|
| R-019 | `DisposeConnectionAsync` duplicates channel/connection teardown | `Producer.cs:273-353` |
| R-046 | `Producer.DisposeAsync` not thread-safe | `Producer.cs:174-178` |
| R-056 | `IProducer.DisconnectAsync` identical to `DisposeAsync` | `Producer.cs:~171` |

**What to do:**
- [ ] R-019: Extract shared `TearDownChannelAndConnectionAsync()` helper. All three methods delegate to it.
- [ ] R-046: Use `Interlocked.Exchange(ref _disposedInt, 1)` for atomic guard.
- [ ] R-056: Deprecate `DisconnectAsync` with `[Obsolete]` attribute pointing to `DisposeAsync`.
- [ ] Write test: concurrent dispose doesn't throw.
- [ ] Commit.

---

### Task 4.7 — Telemetry Hardening

*Related group: CR-I*

| ID | Title | File |
|----|-------|------|
| R-026 | Mutable static `Options` + hardcoded RabbitMQ system | `ServiceConnectActivitySource.cs:10, 41-43` |
| R-053 | Repeated logic across 3 activity-start methods | `ServiceConnectActivitySource.cs (253 lines)` |
| P-071 | Enable*Telemetry flags never checked in activity sources | Every message with telemetry |

**What to do:**
- [ ] R-026: Remove public setter on `Options` (use `internal`). Introduce injectable `IMessagingSystemAttributes` for transport-specific metadata.
- [ ] R-053: Extract `StartActivity(...)` and `TryEnrich(...)` helper methods to deduplicate Publish/Consume/Send.
- [ ] P-071: Gate activity creation on both `HasListeners()` AND the `Enable*Telemetry` flag.
- [ ] Write tests for: flag gating, no activity when disabled.
- [ ] Commit.

---

### Task 4.8 — Interface & DI Cleanup

| ID | Title | File |
|----|-------|------|
| R-013 | `HandlerReference.RoutingKeys` populated but never consumed | `HandlerReference.cs:7`, `HandlerScanner.cs:33,48,63,78` |
| R-033 | `IConsumeContext.CancellationToken` has public setter | `IConsumeContext.cs:22`, `ConsumeContext.cs:10` |
| R-036 | `IBusConfiguration` exposes sub-configs (ISP violation) | `IBusConfiguration.cs:3-21` |
| R-065 | `IRequestReplyManager` leaks pipeline delegate into interface | `IRequestReplyManager.cs:7-13` |
| R-057 | `ProcessManagerTimeoutService` resolves `IBus` via service locator | `ProcessManagerTimeoutService.cs:65` |

**What to do:**
- [ ] R-013: Either implement topic-based routing respecting `RoutingKeys`, or remove the property and stop populating it in `HandlerScanner`.
- [ ] R-033: Change `CancellationToken` to `{ get; }` or `{ get; init; }`.
- [ ] R-036: Remove sub-configuration properties from `IBusConfiguration`. Consumers take sub-configs directly via DI.
- [ ] R-065: Inject `ISendMessagePipeline` into `RequestReplyManager` constructor. Remove `sendAction` parameter from interface.
- [ ] R-057: Inject `IBus` (or `Lazy<IBus>`) via constructor. Add null guard.
- [ ] Update all call sites. Write tests.
- [ ] Commit.

---

## Phase 5: Medium Priority — Correctness, Performance & Quality

**Goal:** Address remaining medium-severity bugs, performance improvements, and code quality issues.
**Estimated scope:** ~35 findings

### Task 5.1 — Bus & Dispose Improvements

| ID | Title | File |
|----|-------|------|
| R-029 | `Bus.DisposeAsync` hangs indefinitely without cancellation | `Bus.cs:278-291` |
| R-058 | `MessageBusWriteStream.DisposeAsync` `_closed` flag not thread-safe | `MessageBusWriteStream.cs:49, 68-71` |
| R-063 | `ProcessManagerTimeoutService._cts` not disposed in `StopAsync` | `ProcessManagerTimeoutService.cs:43-53` |

**What to do:**
- [ ] R-029: Use `CancellationTokenSource` with disposal timeout (e.g., 30s) in `StopConsumingCoreAsync`.
- [ ] R-058: Use `Interlocked.CompareExchange(ref _closedFlag, 1, 0)` for thread-safe close.
- [ ] R-063: Dispose `_cts` in `StopAsync` after awaiting `_pollingTask`.
- [ ] Write tests for timeout-based dispose, double-close safety.
- [ ] Commit.

---

### Task 5.2 — MongoDB Persistence Quality

*Related group: CR-E (remaining)*

| ID | Title | File |
|----|-------|------|
| R-015 | `MongoDbAggregatorPersistor` imports `ServiceConnect.Services` (layering violation) | `MongoDbAggregatorPersistor.cs:7` |
| R-041 | `InsertDataAsync` `TargetInvocationException` not fully unwrapped | `MongoDbProcessManagerFinder.cs:129-139` |
| R-061 | Fragile version revert logic in `UpdateDataAsync` | `MongoDbProcessManagerFinder.cs:176-198` |

**What to do:**
- [ ] R-015: Move the needed type from `ServiceConnect.Services` to `ServiceConnect.Interfaces`. Remove the import.
- [ ] R-041: Add general catch: `catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }`
- [ ] R-061: Capture `currentVersion` as a local. Use in all revert paths.
- [ ] Write tests for unwrapped exceptions, version revert.
- [ ] Commit.

---

### Task 5.3 — InMemory Persistence Improvements

| ID | Title | File |
|----|-------|------|
| R-037 | `FindMatchingItem` reflection fallback under lock (null risk) | `InMemoryProcessManagerFinder.cs:69-95` |
| P-025 | Coarse `_memoryCacheLock` serializes all operations | `InMemoryProcessManagerFinder.cs:40-41` |
| P-026 | Uncached reflection in `FindMatchingItem` fallback | `InMemoryProcessManagerFinder.cs:84-90` |
| P-050 | `CacheProvider.Count()` snapshots all keys | `CacheProvider.cs:96-99` |
| P-051 | `PurgeNormalPriorities` double-pass LINQ | `CacheProvider.cs:104-108` |
| P-052 | `InMemoryAggregatorPersistor.RemoveDataAsync` double O(N) scan | `InMemoryAggregatorPersistor.cs:54-66` |

**What to do:**
- [ ] R-037: Add null check: `if (versionProp.GetValue(value) is int version)`. Consider `IMemoryData` interface to avoid reflection.
- [ ] P-025: Use striped locks or `ReaderWriterLockSlim`. Separate timeout store from PM data.
- [ ] P-026: Cache `PropertyInfo` or compiled getters in `ConcurrentDictionary<Type, (Func, Func)>`.
- [ ] P-050: Use `_cache.Count` instead of `_cache.Keys.Count`.
- [ ] P-051: Replace double-pass LINQ `Count(predicate)` with single-pass `foreach` loop.
- [ ] P-052: Use index-based `RemoveAt` instead of LINQ + `List.Remove`.
- [ ] Write tests for all changes.
- [ ] Commit.

---

### Task 5.4 — Stream Processing Improvements

*Perf Group I*

| ID | Title | File |
|----|-------|------|
| R-054 | Non-atomic check-and-remove of completed streams | `StreamProcessor.cs:82-117` |
| P-038 | Dual `ConcurrentDictionary` for stream state | `StreamProcessor.cs:14-15` |
| P-039 | Sync dispatch + serializer re-resolved from DI | `StreamProcessor.cs:115, 119` |
| P-045 | Defensive `byte[]` copy per packet in `WriteAsync` | `MessageBusWriteStream.cs:32-33` |

**What to do:**
- [ ] R-054 + P-038: Merge `_activeStreams` and `_streamTimestamps` into a single `ConcurrentDictionary` with composite value (stream + timestamp). Atomic check-and-remove.
- [ ] P-039: Inject `IMessageSerializer` at construction. Wrap sync `Execute` in `Task.Run` if blocking.
- [ ] P-045: Pass `ReadOnlyMemory<byte>` instead of defensive copy if transport copies.
- [ ] Write tests for concurrent stream completion, eviction.
- [ ] Commit.

---

### Task 5.5 — RequestReply & Misc Medium Perf

| ID | Title | File |
|----|-------|------|
| P-041 | Two `CancellationTokenSource` per request | `RequestReplyManager.cs:31-32, 90-91` |
| P-042 | `Bus.RouteAsync` LINQ `Skip(1)` allocation | `Bus.cs:182` |
| P-043 | `SendOptions` heap allocation per reply | `ConsumeContext.cs:19-32` |
| P-044 | `QueueConfiguration.AddQueueMapping` O(N) `ImmutableList.Contains` | `QueueConfiguration.cs:32-34` |

**What to do:**
- [ ] P-041: Use single `CreateLinkedTokenSource` + `CancelAfter`.
- [ ] P-042: Replace `destinations.Skip(1)` with `string.Join(",", destinations, 1, destinations.Count - 1)` or a span-based approach.
- [ ] P-043: Make `SendOptions` a struct or use a static default instance.
- [ ] P-044: Use `ImmutableHashSet<string>` instead of `ImmutableList<string>`.
- [ ] Write tests for each change.
- [ ] Commit.

---

### Task 5.6 — Connection & Configuration Improvements

| ID | Title | File |
|----|-------|------|
| P-056 | Non-volatile `_connection` in double-checked lock | `Connection.cs:18, 23` |
| R-044 | `OutgoingEventArgs.Headers` setter silently converts null | `OutgoingEventArgs.cs:7-12` |
| R-047 | `SendEventArgs.EndPoints` encodes list as bracket-wrapped CSV | `SendEventArgs.cs:7-18` |
| R-055 | `ICacheProvider` is a fat interface (ISP violation) | `ICacheProvider.cs` |
| R-064 | `SendMessagePipeline` singleton constraint enforced only by comment | `SendMessagePipeline.cs:13-16` |

**What to do:**
- [ ] P-056: Use `Volatile.Read(ref _connection)` in the double-checked lock.
- [ ] R-044: Add `ArgumentNullException.ThrowIfNull(value)` in `OutgoingEventArgs.Headers` setter.
- [ ] R-047: Store `EndPoint` and `EndPoints` independently. Document format if backward compat needed.
- [ ] R-055: Define narrower `IKeyValueStore` interface with only the methods persistors use.
- [ ] R-064: Validate middleware lifetimes at startup, or register explicitly as singleton.
- [ ] Write tests.
- [ ] Commit.

---

### Task 5.7 — Remaining Medium Code Quality

| ID | Title | File |
|----|-------|------|
| R-031 | `Client.cs` is redundant pass-through facade | `Client.cs` |
| R-039 | `Message` base class is not sealed | `Message.cs:3` |
| R-045 | `ProcessManagerToMessageMap` mutable setters | `ProcessManagerToMessageMap.cs:5-6` |
| P-034 | `ConsumeContext` heap allocation per message | `HandlerProcessor.cs:40`, `ProcessManagerProcessor.cs:62` |
| P-035 | `MessageTypeRegistry._types` should be `FrozenDictionary` | `MessageTypeRegistry.cs:8` |
| P-036 | `MessageHandlerRegistry._descriptors` should be `FrozenDictionary` | `MessageHandlerRegistry.cs:11` |
| P-037 | `AggregatorProcessor.FlushAggregatorAsync` sequential await loop | `AggregatorProcessor.cs:103-107` |
| P-040 | `IMessageSerializer` `byte[]` contract blocks span adoption | `IMessageSerializer.cs:5-7` |

**What to do:**
- [ ] R-031: Make `RabbitMqConsumerHost` implement `IConsumer` directly. Remove `Client.cs`.
- [ ] R-039: Seal `Message` or document the inheritance contract.
- [ ] R-045: Change to `{ get; init; }`.
- [ ] P-034: Pool `ConsumeContext` via `ObjectPool<ConsumeContext>`.
- [ ] P-035: Convert to `FrozenDictionary` after registration completes.
- [ ] P-036: Use `FrozenDictionary` for known types + `ConcurrentDictionary` for lazy-built.
- [ ] P-037: Use `Task.WhenAll` for batch removal (leveraging `RemoveAllAsync` from Phase 1).
- [ ] P-040: Add `IBufferWriter<byte>` / `ReadOnlySpan<byte>` overloads to `IMessageSerializer`.
- [ ] Write tests.
- [ ] Commit.

---

## Phase 6: Low Priority & Polish

**Goal:** Address low-severity findings, info items, naming, documentation, and minor optimizations.
**Estimated scope:** ~45 findings

### Task 6.1 — Startup & Registry Optimizations

| ID | Title | File |
|----|-------|------|
| P-054 | Triple `GetInterfaces()` per handler at startup | `ServiceCollectionExtensions.cs:88-130` |
| P-058 | `GetInterfaces().FirstOrDefault` LINQ allocation | `MessageHandlerRegistry.cs:65`, `ProcessManagerHandlerRegistry.cs:26`, `StreamHandlerRegistry.cs:53` |
| P-059 | Intermediate `ToDictionary` before `ToFrozenDictionary` | `AggregatorRegistry`, `StreamHandlerRegistry` |
| P-060 | `DefaultProcessManagerPropertyMapper.ConfigureMapping` dict without capacity | Per saga message |
| R-062 | `AggregatorRegistry.CompileBuildTypedList` enumerator not disposed | `AggregatorRegistry.cs:100-131` |

**What to do:**
- [ ] P-054: Call `GetInterfaces()` once and reuse the array.
- [ ] P-058: Replace `GetInterfaces().FirstOrDefault(...)` with `foreach` + break.
- [ ] P-059: Call `ToFrozenDictionary` with key/value selectors directly, skip intermediate.
- [ ] P-060: `new Dictionary<string, Type>(1)` with capacity hint.
- [ ] R-062: Wrap loop in `try/finally` calling `enumeratorVar.Dispose()`, or use index-based loop.
- [ ] Commit.

---

### Task 6.2 — Inlining & Micro-Optimizations

| ID | Title | File |
|----|-------|------|
| P-061 | `IsValidRoutingSlipDestination` missing `[AggressiveInlining]` | `HandlerProcessor.cs` |
| P-062 | `HeaderDecoder.Decode` missing `[AggressiveInlining]` | `HeaderDecoder.cs` |
| P-063 | `HeaderHelpers.SetHeader` missing `[AggressiveInlining]` | `HeaderHelpers.cs` |
| P-064 | `Guid.ToString()` / `long.ToString()` allocations | `RequestReplyManager.cs`, `MessageBusWriteStream.cs` |
| P-065 | `Guid.ToString()` for cache keys | `InMemoryProcessManagerFinder.cs` |
| P-066 | `new EventArgs()` instead of `EventArgs.Empty` | `CacheProvider.cs` |
| P-067 | `List<Exception>` allocated even on success path | `Retry.cs` |
| P-069 | `RequestOptions.Default` allocated per request | `RequestOptions` |
| P-070 | `SendEventArgs.EndPoints` `Trim` + `Split` allocations | `SendEventArgs.cs` |
| P-072 | `Producer.PublishWithRetryAsync` trivial async wrapper | `Producer.cs` |

**What to do:**
- [ ] P-061/P-062/P-063: Add `[MethodImpl(MethodImplOptions.AggressiveInlining)]` attributes.
- [ ] P-064: Use `TryFormat` with `stackalloc` buffer for `Guid` and `long` formatting.
- [ ] P-065: Use `Guid` as dictionary key directly instead of `Guid.ToString()`.
- [ ] P-066: Use `EventArgs.Empty` instead of `new EventArgs()`.
- [ ] P-067: Lazy init: `List<Exception>? exceptions = null;` — only allocate on first failure.
- [ ] P-069: Use `static readonly RequestOptions Default = new()`.
- [ ] P-070: Use `AsSpan()` to trim without allocation.
- [ ] P-072: Return `ValueTask` directly without `async`/`await` wrapper.
- [ ] Commit.

---

### Task 6.3 — Encapsulation & Naming

| ID | Title | File |
|----|-------|------|
| R-059 | `CacheProvider.Add` silently returns on expired expiry | `CacheProvider.cs:27-34` |
| R-067 | `CacheItem` public parameterless ctor + mutable setters | `CacheItem.cs:5-26` |
| R-068 | `MemoryData<T>.Data` and `MongoDbData<T>.Data` use `default!` | `MemoryData.cs:9`, `MongoDbData.cs:11` |
| R-069 | Magic number `11` in `Producer.GetHeaders` | `Producer.cs:228` |
| R-072 | `HeaderDecoder.Decode` catch-all `ToString()` | `HeaderDecoder.cs:7-13` |
| R-074 | `SlidingDetails.CanExpire` inverted condition | `SlidingDetails.cs:21-25` |
| R-075 | `ServiceConnectBuilder.AdditionalRegistrations` fully mutable | `ServiceConnectBuilder.cs:11` |
| R-076 | `RabbitMqConsumerHost._autoDelete` mutable field overwritten | `RabbitMqConsumerHost.cs:33, 80` |

**What to do:**
- [ ] R-059: Throw `ArgumentOutOfRangeException` when `absoluteExpiry < DateTime.UtcNow`.
- [ ] R-067: Remove parameterless constructor. Change setters to `init`-only.
- [ ] R-068: Mark `Data` as `required` (C# 11) or add guarded constructor.
- [ ] R-069: Extract to `private const int StampedHeaderCount = 11;`.
- [ ] R-072: Add `Debug.Assert` or log when fallback branch is hit.
- [ ] R-074: Rewrite as `return tryAfter.Ticks <= 0;`.
- [ ] R-075: Expose as `IReadOnlyList<>` with `AddRegistration(...)` method.
- [ ] R-076: Use local variable: `bool effectiveAutoDelete = autoDelete ?? _autoDelete;`.
- [ ] Commit.

---

### Task 6.4 — Dead Code, Unused Parameters & Cleanup

| ID | Title | File |
|----|-------|------|
| R-060 | `InMemoryProcessManagerFinder.GetTimeoutsBatchAsync` scans all cache keys | `InMemoryProcessManagerFinder.cs:248-260` |
| R-071 | `RunProcessors` local function obscures control flow | `MessageDispatcher.cs:69-85` |
| R-077 | `InMemoryProcessManagerFinder` / `InMemoryAggregatorPersistor` accept unused params | `InMemoryProcessManagerFinder.cs:15`, `InMemoryAggregatorPersistor.cs:8` |
| R-079 | `Retry.DoAsync` does not accept `CancellationToken` | `Retry.cs:7, 13` |
| R-080 | `IMessageHandler<T>.Context` has public setter | `IMessageHandler.cs:15`, `IProcessHandler.cs:18`, `IStreamHandler.cs:15` |
| R-081 | `ConnectionFactoryBuilder` hardcodes `VirtualHost = "/"` | `ConnectionFactoryBuilder.cs:31, 52-53` |
| R-014 | `MessageBusWriteStream.WriteAsync` no bounds validation | `MessageBusWriteStream.cs:28-45` |

**What to do:**
- [ ] R-060: Use a separate `CacheProvider` instance for timeouts (may already be handled by Task 4.3 SRP split).
- [ ] R-071: Extract `RunProcessors` as a private instance method on `MessageDispatcher`.
- [ ] R-077: Remove unused `connectionString`, `databaseName`, `collectionName` parameters. Update `InMemoryPersistenceExtensions`.
- [ ] R-079: Add `CancellationToken cancellationToken = default` parameter. Pass to `Task.Delay`.
- [ ] R-080: Document `Context`/`Stream` setters as infrastructure-only via XML doc.
- [ ] R-081: Extract `private const string DefaultVirtualHost = "/";`.
- [ ] R-014: Add `ArgumentOutOfRangeException` / `ArgumentNullException` guards matching `Stream.Write` contract.
- [ ] Commit.

---

### Task 6.5 — Info-Level Items

| ID | Title | File |
|----|-------|------|
| R-090 | `MessageDispatcher.Dispatch` `messageType` only used in error logger | `MessageDispatcher.cs:27, 102` |
| R-091 | `ReplyProcessor.ProcessAsync` `messageType` unused in `ProcessReply` | `ReplyProcessor.cs:27` |
| R-092 | `CacheItemPriority` separate file for two-value enum | `CacheItemPriority.cs` |
| R-093 | `IServiceConnectConnection` defined in RabbitMQ package, not Interfaces | `IServiceConnectConnection.cs` |
| R-094 | `ConsumeEventResult` and `ConsumeEventArgs` could be records | `ConsumeEventResult.cs`, `ConsumeEventArgs.cs` |
| R-095 | `CacheProvider` and `CacheItemPriority` are public but internal-only | `CacheProvider.cs`, `CacheItemPriority.cs` |
| R-096 | `MongoDbData<T>.Locked` infrastructure field on public interface | `MongoDbData.cs:13` |
| R-097 | `NewtonsoftJsonMessageSerializer` mutates caller's settings | `NewtonsoftJsonMessageSerializer.cs:9-15` |
| R-098 | Telemetry `Enrich*` callbacks can exfiltrate payload | `ServiceConnectInstrumentationOptions.cs:23, 37` |

**What to do:**
- [ ] R-090: Document the mismatch or remove `messageType` parameter from `Dispatch`.
- [ ] R-091: Remove unused `messageType` parameter from `ProcessReply`, or document.
- [ ] R-092: Move `CacheItemPriority` enum into `CacheItem.cs`. Make `internal`.
- [ ] R-093: Move `IServiceConnectConnection` to `ServiceConnect.Interfaces` if multi-transport is planned.
- [ ] R-094: Convert to `record` types.
- [ ] R-095: Change to `internal sealed` / `internal`.
- [ ] R-096: Hide `Locked` from `IPersistenceData<T>` or move to internal sub-type (may already be handled by R-016 in Task 4.3).
- [ ] R-097: Clone settings before mutating (may already be handled in Task 2.2).
- [ ] R-098: No code change needed. Add `<remarks>` doc warning.
- [ ] Commit.

---

## Cross-Reference: Duplicate Findings

These findings appear in both reports and are merged into a single task above:

| Code Review | Performance | Merged Into |
|-------------|-------------|-------------|
| R-007 | P-006 | Task 1.7 |
| R-034 | P-005 | Task 2.5 |
| R-035 | P-010 | Task 2.10 |
| R-042 | P-022 | Task 2.12 |
| R-040 | P-023 | Task 2.12 |
| R-028 | P-013 | Task 1.1 |
| R-030 | P-024 | Task 1.2 |

---

## Test Coverage Gaps

The code review identified these test gaps (from the Test Coverage Gaps section of `docs/code-review-report.md`). These should be addressed as part of the relevant phase tasks:

| Area | Phase |
|------|-------|
| `MongoDbProcessManagerFinder` unit tests | Phase 2 (Task 2.12) |
| `MongoDbAggregatorPersistor` unit tests | Phase 1 (Task 1.1) |
| `AggregatorProcessor` timer flush | Phase 1 (Task 1.1) |
| `StreamProcessor` completion/eviction | Phase 5 (Task 5.4) |
| `HandlerProcessor` routing slip validation | Phase 3 (Task 3.2) |
| `RequestReplyManager` concurrency | Phase 1 (Task 1.5) |
| `SendMessagePipeline` post-dispose | Phase 5 (Task 5.6) |
| `ProcessManagerTimeoutService` null IBus | Phase 4 (Task 4.8) |
| `ConsumeContext.ReplyAsync` | Phase 3 (Task 3.2) |

---

## Phase Summary

| Phase | Focus | Tasks | ~Findings |
|-------|-------|-------|-----------|
| 1 | Critical Safety & Data Integrity | 7 tasks | ~20 |
| 2 | Critical & High Performance | 12 tasks | ~30 |
| 3 | Security Hardening | 5 tasks | ~15 |
| 4 | Architecture, Design & Testability | 8 tasks | ~25 |
| 5 | Medium Correctness, Perf & Quality | 7 tasks | ~35 |
| 6 | Low Priority & Polish | 5 tasks | ~45 |
| **Total** | | **44 tasks** | **~170** |
