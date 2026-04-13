# Final Validated Issues — Consolidated Report

**Date:** 2026-04-13
**Branch:** `improvements-and-fixes` (HEAD `104bdf7`)
**Sources verified:**
- `docs/2026-04-13-code-review-master-report.md` (42 findings, R-038–R-079)
- `docs/2026-04-13-code-review-report.md` (85 findings, R-001–R-085, separate numbering)
- `docs/2026-04-13-performance-report.md` (80 findings)

Every finding below was verified by reading the current source at HEAD. IDs from the two prior code-review docs are disambiguated as **M-###** (master report) and **O-###** (older report). Performance findings are prefixed **P-##**.

## Executive Summary

| Review | Raw findings | Validated | Fixed/Stale | False-positive | Duplicate |
|---|---|---|---|---|---|
| Master code review | 42 | **42** | 0 | 0 | 0 |
| Older code review | 85 | 71 | 4 | 9 | 1 |
| Performance audit | 80 | 52 (8 re-graded) | 0 | 11 | 1 duplicated internally |
| **Unique after dedup** | — | **≈108** | — | — | — |

**Notable:** The master report is fully accurate. The older report contains a mix of still-valid findings, several that were already fixed by the C-1…C-5 series, and a handful of false-positives (positive/informational items mis-filed as defects, and incorrect claims like "double-checked locking is redundant"). The performance audit is largely accurate for hot-path allocations but labels several startup-only or error-path allocations as CRITICAL/HIGH — those are regraded here.

---

## Section 1 — Security

### S-01 [HIGH] Routing-slip destinations are attacker-controlled
- **Source:** M-038
- **File:** `src/ServiceConnect/Services/Processors/HandlerProcessor.cs:62-72`
- Comma-split routing slip values flow straight into `IBus.RouteAsync` with no allowlist. Any authenticated broker publisher can redirect messages to arbitrary queues.
- **Fix:** allowlist / regex-guard destinations; reject AMQP wildcards.

### S-02 [MEDIUM] TLS 1.2 hardcoded defaults (prevents TLS 1.3)
- **Source:** M-048, O-011 (partial)
- **Files:** `TransportConfiguration.cs:28`, `MongoDbSslOptions.cs:9`
- Both pin `SslProtocols.Tls12`. `SslProtocols.None` (runtime selection) is the modern-safe default.

### S-03 [MEDIUM] `AllowInsecureTls` missing SECURITY WARNING docs
- **Source:** M-049
- **File:** `MongoDbSslOptions.cs:10` — no XML `<remarks>` warning like the RabbitMQ equivalents have.

### S-04 [LOW] Hostname disclosure via `SourceMachine`/`DestinationMachine` headers
- **Source:** M-069, O-083
- **Files:** `Producer.cs:255`, `RabbitMqConsumerHost.cs:149` — unconditional `Environment.MachineName` injection.

### S-05 [LOW] Telemetry enrich callbacks receive raw payload
- **Source:** M-070
- **File:** `ServiceConnectInstrumentationOptions.cs:18,27` — no PII warning in docs.

### S-06 [LOW] Default MongoDB connection string
- **Source:** O-084
- **File:** `MongoDbPersistenceOptions.cs:5` — `"mongodb://localhost/"` default lets production accidentally use localhost.

### S-07 [LOW] Exception-chain message aggregation
- **Source:** O-036
- **File:** `HeaderHelpers.cs:16-27` — `GetErrorMessage` walks full inner-exception chain; can leak internal paths/connection strings.

### S-08 [LOW] Unvalidated Guid from message headers
- **Source:** O-085
- **File:** `filters/ServiceConnect.Filters.MessageDeduplication/.../IncomingDeduplicationFilter.cs:32` — `new Guid(...)` throws `FormatException` on malformed input (DoS vector). Use `Guid.TryParse`.

---

## Section 2 — Concurrency & Correctness

### C-01 [MEDIUM] Aggregator flush race between timer and batch-size paths
- **Source:** M-039, O-003
- **File:** `AggregatorProcessor.cs:65-93` — `_flushLock` wraps only timer removal; `GetDataAsync`/`InvokeExecute`/`RemoveDataAsync` run outside it, enabling double-flush.

### C-02 [MEDIUM] `Producer` doesn't dispose its `SemaphoreSlim` instances
- **Source:** M-050
- **File:** `Producer.cs:20,26,201-205` — `_publishLock` and `_connectionSemaphore` leaked when `AvailableWaitHandle` has been accessed.

### C-03 [MEDIUM] `MessageBusReadStream.Write` — size check + dictionary write not atomic
- **Source:** O-002
- **File:** `MessageBusReadStream.cs:17-22` — concurrent writers can slip past the size check before the first write lands; size limit can be exceeded.

### C-04 [MEDIUM] `QueueConfiguration.AddQueueMapping` mutates inner `List` without lock + fragile cast
- **Source:** M-058
- **File:** `QueueConfiguration.cs:17-32` — casts public-settable `IDictionary` back to `ConcurrentDictionary` (`InvalidCastException` if a plain `Dictionary` is assigned); mutates the `List` inside the ConcurrentDictionary value without synchronization.

### C-05 [MEDIUM] `StreamProcessor` write + timestamp update not atomic
- **Source:** O-075
- **File:** `StreamProcessor.cs:62-63` — `stream.Write(...)` and `_streamTimestamps[sequenceId] = DateTime.UtcNow` are two separate operations. `EvictStaleStreams` could remove an entry between these lines.

### C-06 [MEDIUM] `MongoDbProcessManagerFinder` index-creation race
- **Source:** O-024, P-39
- **File:** `MongoDbProcessManagerFinder.cs:315-323` — `_indexedCollections.TryAdd` records success before `CreateOne` runs; on failure the index won't exist and won't be retried.

### C-07 [MEDIUM] `BusHostedService` swallows `InvalidOperationException`
- **Source:** O-080
- **File:** `BusHostedService.cs:22-26` — reports success to the host even when consumer startup failed.

### C-08 [MEDIUM] `MessageDeduplicationPersistorMongoDb` fire-and-forget index creation
- **Source:** P-69
- **File:** `MessageDeduplicationPersistorMongoDb.cs:53-56` — `CreateOneAsync` called without await; exceptions silently swallowed.

### C-09 [LOW] `RabbitMqConsumerHost` busy-wait during dispose
- **Source:** O-025, M-051
- **File:** `RabbitMqConsumerHost.cs:184-188` — 50-ms polling for up to 5 s. Also hardcoded 5000 ms deadline with no config surface (M-051).

### C-10 [LOW] Unbounded `_pendingRequests` dictionary
- **Source:** O-026
- **File:** `RequestReplyManager.cs:10` — no cap or back-pressure; timeout cleans individual entries but no global limit.

### C-11 [LOW] `CacheProvider` Rx subscription never disposed
- **Source:** O-077
- **File:** `CacheProvider.cs:133-141` — `Observable.Timer(...).Subscribe(...)` return value discarded; continues firing post-dispose. (Tied to P-13 — replace Rx with `System.Threading.Timer`.)

### C-12 [LOW] `Consumer.ConfigureQueueAsync` doesn't catch like siblings
- **Source:** M-047
- **File:** `Consumer.cs:148-151` — broker errors propagate as unhandled; inconsistent with the rest of the `Configure*Async` family.

### C-13 [LOW] `Consumer` uses `_model!` inside broad-catch methods
- **Source:** M-052
- **File:** `Consumer.cs:135-248` — NRE on null channel swallowed by catch-all; add an assertion after `CreateChannelAsync`.

### C-14 [INFO] Fire-and-forget timer callback with `OnlyOnFaulted`
- **Source:** M-079, O-003
- **File:** `AggregatorProcessor.cs:72-77` — logger exception would be lost; awareness only.

### C-15 [INFO] `MessageBusWriteStream.CloseAsync` empty-packet terminator
- **Source:** M-071
- **File:** `MessageBusWriteStream.cs:45-58` — future change to the close packet body would corrupt reassembly; document the invariant.

---

## Section 3 — Architecture & Cohesion

### A-01 [MEDIUM] `Bus` is a god class
- **Source:** O-008, O-018, O-020
- **File:** `Bus.cs:30-60` — constructor takes 14 dependencies; handles publish/send/request-reply/routing-slips/streams/lifecycle. Exposes internal registry types directly (`ProcessManagerHandlerRegistry`, etc.) — leaks implementation details.

### A-02 [MEDIUM] `Consumer` mixes topology provisioning + lifecycle
- **Source:** M-040, O-013
- **File:** `Consumer.cs:11-25, 46-249` — 7 `Configure*Async` topology methods + lifecycle in one 249-line class; 9 non-readonly instance fields set post-construction.

### A-03 [MEDIUM] `ConnectionFactory` construction duplicated in `Connection` & `Producer`
- **Source:** M-041, O-058
- **Files:** `Connection.cs:39-76`, `Producer.cs:73-118`. `Producer` also bypasses `IServiceConnectConnection`. `ConfigureExchangeAsync` duplicated at `Consumer.cs:135-146` / `Producer.cs:268-280`.

### A-04 [MEDIUM] Duplicate event-args classes across `Interfaces` and `Telemetry`
- **Source:** M-042
- `ConsumeEventArgs.cs`, `OutgoingEventArgs.cs`, `PublishEventArgs.cs`, `SendEventArgs.cs` exist in both `ServiceConnect.Interfaces` and `ServiceConnect.Telemetry`. Causes ambiguous-reference issues.

### A-05 [MEDIUM] `InMemoryProcessManagerFinder` — reflection + `dynamic` + per-call `Compile`
- **Source:** M-043, O-031, O-072, P-11, P-40, P-58
- **File:** `InMemoryProcessManagerFinder.cs:59-171`
  - Line 122: `GetMethods().First(...) + MakeGenericMethod + Invoke` per insert
  - Line 170: `dynamic storedData = ...; (int)storedData.Version` — the only `dynamic` in the codebase
  - Line 110: `lambda.Compile()` per query (not cached)
  - Line 47: blanket `catch (Exception) → return null` swallows mapping bugs
  - Line 144: `GetMemoryData` `public` only to support self-reflection
- **Fix:** introduce `IVersioned` for the version cast; cache compiled delegates per type (matches C-4 registry pattern); `private` for `GetMemoryData`; add a logger and log on catch.

### A-06 [MEDIUM] `IFilter.Bus` property is a trap
- **Source:** M-053, O-019
- **File:** `IFilter.cs:11` — property declared but never set by `FilterPipeline`. Accessing `this.Bus` returns null.

### A-07 [MEDIUM] `MessageDispatcher.Dispatch` does too much
- **Source:** O-012, O-020
- **File:** `MessageDispatcher.cs:27-113` — 87-line method handling header extraction, envelope build, pre-deserialization processors, deserialization, filters, middleware chain, post-deserialization processors, and exception handling.

### A-08 [MEDIUM] `ITransportConfiguration` fat interface
- **Source:** O-009, M-057
- **File:** `ITransportConfiguration.cs:7-27` — ~18 members mixing connection/SSL/retry/settings. Also all `{ get; set; }` on singleton config.

### A-09 [MEDIUM] Config interfaces all expose mutable setters on singletons
- **Source:** M-057
- **Files:** `IBusConfiguration.cs`, `IQueueConfiguration.cs`, `IPersistenceConfiguration.cs`, `ITransportConfiguration.cs`. Split into read-only (runtime) + builder (configuration-time).

### A-10 [MEDIUM] `ITimeoutStore.TimeoutInserted` event has no production subscriber
- **Source:** M-044, M-077
- **File:** `ITimeoutStore.cs:5` + `InMemoryProcessManagerFinder.cs:234` + `MongoDbProcessManagerFinder.cs:229`. Fires on every insert with no consumer (only a unit test listens). `TimeoutInsertedDelegate` also declared in the wrong file (`IProcessManagerFinder.cs:3`).

### A-11 [MEDIUM] `ServiceCollectionExtensions` uses `AppDomain.CurrentDomain.GetAssemblies()`
- **Source:** O-039, O-082
- **File:** `ServiceCollectionExtensions.cs:74` — hard to test; makes registration implicit.

### A-12 [MEDIUM] Registry classes not `FrozenDictionary`
- **Source:** P-46
- **Files:** `MessageHandlerRegistry.cs:11`, `AggregatorRegistry.cs:12`, etc. Populated once at startup, read on every message — ideal for `FrozenDictionary`.

### A-13 [LOW] `ReplyProcessor` is `public` while siblings are `internal`
- **Source:** M-045
- **File:** `ReplyProcessor.cs:5`.

### A-14 [LOW] `InMemoryAggregatorPersistor.RemoveAll` missing from `IAggregatorPersistor`
- **Source:** M-046
- **File:** `InMemoryAggregatorPersistor.cs:66-75` — only tests call it; `MongoDbAggregatorPersistor` has no equivalent.

### A-15 [LOW] `IMessageHandlerProcessor` unused
- **Source:** M-054 — delete it.

### A-16 [LOW] `IMessageBusReadStream.CompleteEventHandler` & `HandlerCount` dead
- **Source:** M-055, O-068 — never assigned, read, or invoked in production.

### A-17 [LOW] `IServiceConnectConnection.ConnectAsync` unused externally
- **Source:** M-056 — only `Connection.CreateChannelAsync` uses it internally.

### A-18 [LOW] `CacheProvider.Default` static singleton unused in production
- **Source:** M-075 — only a unit test touches it.

### A-19 [LOW] `CacheProvider.Contains(object)` non-generic
- **Source:** M-076 — siblings (`Add/Get/Remove/Keys`) are generic.

### A-20 [LOW] MongoDB persistor `AssemblyQualifiedName` lookup is fragile
- **Source:** O-078
- **File:** `MongoDbAggregatorPersistor.cs:54,74` — assembly version changes break lookup. Use `FullName`.

### A-21 [LOW] Layering violation: infra projects reference core
- **Source:** O-006, O-007, M-078
- RabbitMQ / Persistence projects reference `ServiceConnect.csproj` for builder extensions. Pragmatic today; cleaner if builder lived in a `ServiceConnect.DependencyInjection` assembly.

---

## Section 4 — Encapsulation / API Hygiene

### E-01 [MEDIUM] `Envelope.Headers`/`Body` public setters
- **Source:** M-060, O-044 — allow replacement mid-pipeline.

### E-02 [MEDIUM] `DefaultProcessManagerPropertyMapper.Mappings` public settable `List<T>`
- **Source:** M-059 — external code can replace or mutate post-config.

### E-03 [MEDIUM] `HandlerReference.RoutingKeys` mutable `IList<string>`
- **Source:** O-021.

### E-04 [MEDIUM] `MessageBusReadStream` publicly-settable state
- **Source:** O-022 — `SequenceId`, `LastPacketNumber` settable externally.

### E-05 [LOW] Missing `sealed` on concrete DTOs
- **Source:** M-061, O-071 — `CacheItem`, `SlidingDetails`, `MemoryData<T>`, `MongoDbData<T>`, `ConsumeEventResult`, `ProcessManagerToMessageMap`, `TimeoutsBatch`, `ConsumeEventArgs`, `Envelope`, `CacheProvider`.

### E-06 [LOW] `ServiceConnectException` should be `abstract`
- **Source:** M-062 — avoids bypassing typed subclasses.

### E-07 [LOW] Missing `QueueName` validation
- **Source:** M-065
- **File:** `Bus.cs:228` + `QueueConfiguration.cs:8`. Empty default + no validation leads to confusing broker errors.

### E-08 [LOW] Aggregator virtual zero-defaults with no validation
- **Source:** M-064
- **File:** `Aggregator.cs:15-27` + `AggregatorRegistry` — dual-zero (BatchSize=0 && Timeout=0) buffers forever silently.

---

## Section 5 — Magic Strings / Numbers / Configurables

### G-01 [LOW] Magic strings scattered across RabbitMQ + Telemetry
- **Source:** M-063
- `".Retries"`, `".Retries.DeadLetter"`, `"fanout"`, `"direct"` (use `ExchangeType.Fanout/Direct`), `"MessageId"`, `"DestinationAddress"` (use `HeaderKeys`).

### G-02 [LOW] Magic numbers in `TransportConfiguration`
- **Source:** O-029 — `3000`, `3`, `1` defaults as inline literals.

### G-03 [LOW] Magic numbers in `Producer`
- **Source:** O-030 — `65536`, `60`, `10` as private consts with no documentation.

### G-04 [LOW] `MongoDbProcessManagerFinder` 1-minute magic
- **Source:** O-057
- **File:** `MongoDbProcessManagerFinder.cs:285` — `utcNow.AddMinutes(1)`.

### G-05 [LOW] Hardcoded values that should be configurable
- **Source:** O-059
- `MaxTotalStreamSize = 100 * 1024 * 1024` (`MessageBusReadStream.cs:8`)
- `DefaultPollInterval = TimeSpan.FromSeconds(30)` (`ProcessManagerTimeoutService.cs:15`)
- `ExpiryDuration = TimeSpan.FromDays(2)` (`InMemoryProcessManagerFinder.cs:22`)
- `DefaultNextQueryInterval = TimeSpan.FromMinutes(1)` (`InMemoryProcessManagerFinder.cs:23`)

### G-06 [LOW] Unit inconsistency: `RetryDelay` ms vs `RetrySeconds`
- **Source:** O-023
- `TransportConfiguration.RetryDelay` is ms; `RabbitMQSettingKeys.RetrySeconds` is s.

### G-07 [LOW] Missing input validation on `TransportConfiguration`
- **Source:** O-035 — `Host` default `"localhost"` not validated; `RetryDelay` accepts negative values.

---

## Section 6 — Documentation

### D-01 [LOW] Missing XML docs on handler interfaces + telemetry public API
- **Source:** M-066, O-033
- `IMessageHandler<T>`, `IProcessHandler<,>`, `IStreamHandler<T>`, `IConsumeContext`, `Envelope`, `Message`; `ServiceConnectActivitySource.Publish/Consume/Send/TryGetExistingContext`.

### D-02 [LOW] French XML comments + copy-pasted doc in InMemory persistence
- **Source:** M-067
- `CacheItem.cs:11` and `SlidingDetails.cs:6` in French; `SlidingDetails.CanExpire` doc is a copy from elsewhere.

### D-03 [LOW] Message type-name lookup by string replace is undocumented
- **Source:** O-069
- `Bus.cs:213` — `FullName!.Replace(".", string.Empty)` naming convention unstated.

---

## Section 7 — CLEAN (method/class length, nesting, duplication)

These are stable, functioning code but above CLEAN thresholds. Low priority; batch at team's discretion.

| ID | File / Method | Size |
|---|---|---|
| L-01 | `MessageDispatcher.Dispatch` | 87 lines |
| L-02 | `StreamProcessor.ProcessAsync` | 84 lines, nesting >3 |
| L-03 | `Consumer.StartConsumingAsync` | 75 lines |
| L-04 | `ProcessManagerProcessor.ProcessAsync` | 64 lines |
| L-05 | `Producer.cs` | 363 lines total |
| L-06 | `Producer.CreateConnectionAsync` | 46 lines |
| L-07 | `AggregatorProcessor.ProcessAsync` | 43 lines, nesting >3 |
| L-08 | `Bus.StartConsumingAsync` | 41 lines |
| L-09 | `ProcessManagerTimeoutService.PollOnceAsync` | 41 lines |
| L-10 | `Producer.GetHeaders` | 27 lines |
| L-11 | `Bus.SendAsync` | 24 lines |
| L-12 | `Bus.RouteAsync` | 23 lines |
| L-13 | `Producer.EnsureConnectedAsync` | 23 lines |
| L-14 | `HandlerProcessor.ProcessAsync` | nesting depth = 4 |
| L-15 | `Retry.cs` `DoAsync` + `DoAsync<T>` | duplicated 30-line bodies |
| L-16 | `Consumer` — 6 `Configure*Async` try/catch copies | helper candidate |
| L-17 | `ProcessManagerHandlerRegistry` — 7 `Compile*` near-duplicates | generic helper candidate |
| L-18 | `AggregatorRegistry.CompileBuildTypedList` | 31 lines of dense expression-tree construction |
| L-19 | `ServiceConnectActivitySource.Publish/Consume/Send` | near-duplicate tag setup |
| L-20 | `HandlerScanner` four-branch interface matching + `ServiceCollectionExtensions:82-121` DI-registration mirror | parallel structures |

---

## Section 8 — Performance (Validated)

### P-crit [CRITICAL] Per-message hot-path allocations

| ID | File:line | Problem | Fix |
|---|---|---|---|
| P-01 | `MessageBusWriteStream.cs:37-40` | New `Dictionary<string,string>` per packet | Reuse dict with mutated packet-number |
| P-03 | `Bus.cs:316-324` | New dict per outgoing message in `ExtractHeaders` | Pool or direct Envelope access |
| P-04 | `SendMessagePipeline.cs:54-58` | `BuildChain` rebuilt on every send, closure allocation per middleware | Cache chain once at config time |
| P-05 | `HandlerProcessor.cs:20` | New `List<>` per message dispatch (even empty handler case) | Check registry first, use `[]` when empty |
| P-08 | `RabbitMqConsumerHost.cs:162` | `args.Body.ToArray()` per consumed message | Propagate `ReadOnlyMemory<byte>` through pipeline (requires delegate change) |
| P-09 | `RabbitMqConsumerHost.cs:134-140` | New `Dictionary<string,object>` per consumed message | Pool-rent + clear |
| P-11 | `InMemoryProcessManagerFinder.cs:110` | `lambda.Compile()` per query (≈1000× slower than cached delegate) | Cache compiled delegate per `(T, mapping)` |
| P-12 | `InMemoryProcessManagerFinder.cs:84-108` | O(n) scan of entire cache per correlation lookup | Partition cache by type |
| P-14 | `MongoDbProcessManagerFinder.cs:248-269` | `FindOneAndUpdateAsync` loop — N round-trips per polling cycle | Bulk `UpdateMany` + single `Find` |

### P-high [HIGH] Per-message/hot-path perf concerns

| ID | File:line | Problem |
|---|---|---|
| P-07 | `SendEventArgs.cs:7-11` | `EndPoints` getter `Trim+Split` on every access (duplicated at P-41) |
| P-15 | `NewtonsoftJsonMessageSerializer.cs:26-27,45-46` | UTF8→string→UTF8 double conversion per serialize/deserialize |
| P-17 | `FilterPipeline.cs:32` | `GetRequiredService(filterType)` per filter per message (if filters are transient) |
| P-18 | `RequestReplyManager.cs:71` | `new ConcurrentBag<>()` per multi-reply request |
| P-28 | `Producer.cs:265` | LINQ `ToDictionary` per publish |
| P-31 | `Producer.cs:248,254` | `Guid.NewGuid().ToString()` + `DateTime.UtcNow.ToString("O")` per publish |
| P-32 | `RabbitMqConsumerHost.cs:148,165` | `DateTime.UtcNow.ToString("O")` × 2 per consumed message |
| P-36 | `InMemoryAggregatorPersistor.cs:44` | `ToList()` defensive copy per retrieval |
| P-46 | Registry classes (`MessageHandlerRegistry.cs:11` etc.) | `ConcurrentDictionary` where `FrozenDictionary` would serve |
| P-70 | `MessageDeduplicationPersistorInMemory.cs:19,25` | Repeated `Guid.ToString()` allocations |

### P-med [MEDIUM] Real but lower impact

| ID | File:line | Problem |
|---|---|---|
| P-02 | `MessageBusReadStream.cs:29-35` | Double MemoryStream allocation in `Read()` (regraded from CRITICAL — once per stream) |
| P-10 | `MessageRetryHandler.cs:54-59` | Newtonsoft on retry failure path (regraded — error path) |
| P-13 | `CacheProvider.cs:133-141` | `Observable.Timer` per sliding item (plus C-11 disposal) |
| P-19 | `HandlerProcessor.cs:62-64` | `.Split().Select().ToList()` on routing slip (only when header present) |
| P-25 | `ProcessManagerToMessageMap.cs:5` | `Func<object,object>` boxes return value |
| P-30 | `Consumer.cs:66-69` | Sequential `ConfigureExchangeAsync` — startup-only |
| P-33/34 | `Consumer.cs:87-119` | Sequential configure awaits + sequential client creation — startup-only |
| P-35 | `MongoDbProcessManagerFinder.cs:44-45` | Linear `FirstOrDefault` over `mapper.Mappings` — small n |
| P-37 | `CacheProvider.cs:79-82` | LINQ materializing all keys filtered by type |
| P-43 | `AggregatorProcessor.cs:56-58` | New `Timer` per incoming aggregator message |
| P-44 | `ReplyProcessor.cs:16,21,24` | `Task.FromResult` boxes enum |
| P-50 | `IRequestReplyManager.cs:16-23` | `Task<IList<TReply>>` forces materialization |
| P-57 | `Retry.cs:33,63` | `new AggregateException` on all-retries-exhausted (failure path) |
| P-61 | `CacheItem.cs:45` | `Nullable<TimeSpan>` boxes when stored as object |
| P-62 | `InMemoryProcessManagerFinder.cs:32-113` | Entire `FindDataAsync` body (incl. `Compile()`) under `_memoryCacheLock` |
| P-63 | `MongoDbProcessManagerFinder.cs:121-124` | Reflection `GetMethod + MakeGenericMethod` per insert |
| P-64 | `ServiceConnectActivitySource.cs:97` | `eventArgs.Headers.ToList()` before foreach (unnecessary copy) |
| P-66 | `ServiceConnectActivitySource.cs:43` | `routingKey + " publish"` string concat (only when listeners active) |
| P-67/68 | Gzip filters | Two `MemoryStream` + `ToArray()` per message |
| P-74 | `ProcessManagerProcessor.cs:41` | New `DefaultProcessManagerPropertyMapper` per message |
| P-76 | `CacheProvider.cs:87-90` | `[.. _cache.Keys]` spread materializes on every call |
| P-77 | `MessageDeduplicationPersistorInMemory.CacheItem.Value` | Dead `object` field (unused storage) |

### P-low [LOW] Real but negligible

- P-72 `StreamProcessor.cs:119-131` — modify-while-iterate (thread-safe, but a code smell).
- P-53 Missing `ConfigureAwait(false)` on ~22/24 Consumer awaits (`Producer`/`Connection` already good).
- P-56 `HeaderHelpers.cs:18-26` StringBuilder on error path (arguably correct usage).
- P-59/60 Dictionary/List capacity hints (MongoDb persistors; trivial).

---

## Rejected (False-Positives)

| ID | Why rejected |
|---|---|
| O-016 (characterization) | `ProcessManagerProcessor.ProcessAsync` SRP claim no longer accurate after C-3 registry thinning; length is similar but mixed-responsibility critique doesn't apply. |
| O-037 | `HandlerScanner` reflection labeled "problematic" — it's standard `GetInterfaces()`; already cleanly sectioned. |
| O-045 | `Message.CorrelationId` is `private set` in primary-ctor shape — effectively immutable to external callers. |
| O-064/O-065/O-066/O-073/O-081 | Positive findings mis-filed as defects. |
| O-074 | "Remove first check before lock" is wrong — that's the standard double-checked-locking optimization. |
| O-076 | Claim "FilterPipeline creates new filter instance per call" depends on DI lifetime; not inherently broken. |
| O-079 | `Expression.New(dataType)` throwing `ArgumentException` is desirable startup-fail-fast behavior. |
| P-06 | `HeaderDecoder.Decode(object?)` claim of boxing — values are already boxed in the RabbitMQ header dictionary; `Decode` adds no boxing. |
| P-20 | `DefaultProcessManagerPropertyMapper` closure — compiled at startup, not per message. |
| P-21 | Claim QueueConfiguration `List` lacks capacity hint — cited lines don't contain the pattern; startup-only anyway. |
| P-23 | `HeaderKeys.cs` boxing — file is `const string` only; no boxing possible. |
| P-24 | `IProcessManagerPropertyMapper` "expression trees in hot path" — expressions are passed at configuration time. |
| P-42 | "Concurrent modification during enumeration" of `ConcurrentDictionary` — documented thread-safe. |
| P-52 | Missing `IAsyncDisposable` on `ISendMessagePipeline` — correctness/lifecycle concern, not perf. |
| P-54 | Settings dictionary lookups in `Connection.cs:12-13` are ctor-time `readonly` initialization, not per-call. |
| P-73 | `MessageHandlerRegistry.TryGetOrBuild` DOES cache null misses at line 50. |
| P-78 | `Random.Shared` is contention-free in .NET 6+. |
| P-80 | Boxed boolean lookup is ctor-time unbox, not hot path. |

## Already Fixed (per C-1…C-5 remediation series)

| ID | Status | Evidence |
|---|---|---|
| O-001 | FIXED | `RequestReplyManager` now removes via `finally` block in both single/multi-reply paths. |
| O-028 | FIXED | `ServiceConnectActivitySource` no longer has the null-collection return pattern at the cited lines. |
| O-032 | FIXED | `ProcessManagerTimeoutService` cancellation catches now include `break`/intent — no longer "empty swallows". |
| O-056 | FIXED | All checked files use file-scoped namespaces (`namespace X;`). |

## Partial

| ID | Status |
|---|---|
| O-011 | Documentation warnings added for RabbitMQ insecure-TLS options, but not removed; MongoDb side still has `AllowInsecureTls` with no warning (see S-03). |
| O-034 | Most persistence exception handling standardized on `PersistenceException`, but `InMemoryProcessManagerFinder.cs:43-51` still has a blanket `catch (Exception) → return null`. |
| O-055 | `HandlerProcessor`, `ProcessManagerProcessor`, `AggregatorProcessor` mostly have `ConfigureAwait(false)`. One remaining gap: `HandlerProcessor.cs:73` `await task` lacks it. |
| O-027 | The four fields are set in `StartConsumingAsync`, so `readonly` is not applicable as stated; but the underlying "fields should be immutable after init" point is still valid — see A-02. |

---

## Suggested sequencing

Two ways to attack this list depending on appetite:

1. **Security & correctness first (narrow, high-value):** S-01, S-02/03, C-01, C-02, C-03, C-06, C-07, C-08.
2. **Refactor waves (broader, higher churn):**
   - Wave 1 (low-risk cleanup): A-10 (remove unused event), A-14..A-19 (dead-code/cohesion), D-01..D-03 (docs), G-01..G-07 (constants), L-15..L-20 (duplication collapse).
   - Wave 2 (focused refactors): A-05 (InMemoryProcessManagerFinder + P-11/12/40/58/62/63), A-04 (event-args dedup), A-03/A-02 (RabbitMQ adapter split + shared ConnectionFactoryBuilder).
   - Wave 3 (breaking changes, queue for next major): A-01 (Bus god class split), A-06 (IFilter.Bus removal), A-08/A-09 (config interface splits), E-01..E-08 (API immutability), P-hot-path allocation work (SendMessagePipeline caching, dictionary pooling, System.Text.Json + source generators, FrozenDictionary registries, ReadOnlyMemory pipelining).

No finding in this report blocks shipping the current branch. **S-01 (routing-slip injection)** is the only one I'd call out for near-term action given the deployment model.
