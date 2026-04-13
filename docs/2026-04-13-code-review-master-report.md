# Code Review — Master Findings Report (Phase 1 & 2)

**Date:** 2026-04-13
**Branch:** `improvements-and-fixes` (HEAD `104bdf7`)
**Scope:** Full solution except test projects (test projects inspected only for coverage-gap detection)
**Baseline:** This review follows the C-1…C-5 remediation series (R-001…R-037). Findings already marked Done in `docs/remaining-issues.md` are excluded.

**Summary counts:** 1 High · 14 Medium · 19 Low · 8 Info = **42 findings**

---

## HIGH (1)

### R-038 — Routing-slip destinations are fully attacker-controlled
**File:** [src/ServiceConnect/Services/Processors/HandlerProcessor.cs:52-74](src/ServiceConnect/Services/Processors/HandlerProcessor.cs#L52-L74)
**Category:** Security / Missing-validation
**Description:** When a consumed message carries a `RoutingSlip` header, `ForwardRoutingSlipAsync` splits the value on commas and calls `IBus.RouteAsync` with each resulting string as a queue/exchange name — with no whitelist or format check. Any AMQP principal able to publish to a listened exchange can inject arbitrary queue names (including internal infra queues, other services' queues, or queues outside the intended topology) and cause the service to forward the live message payload there.
**Attacker capability:** Any authenticated broker user able to publish to an exchange the service listens on.
**Fix:** Validate each destination against an operator-configured allowlist, OR enforce a queue-name regex/prefix check. At minimum, reject destinations containing AMQP wildcards or exceeding a reasonable length.

---

## MEDIUM (14)

### R-039 — Aggregator flush race between timer and batch-size paths
**File:** [src/ServiceConnect/Services/Processors/AggregatorProcessor.cs:65-77](src/ServiceConnect/Services/Processors/AggregatorProcessor.cs#L65-L77)
**Category:** Concurrency / Bug
**Description:** `OnTimerFired` does `ContainsKey` then calls `FlushAggregatorAsync` fire-and-forget. A concurrent `ProcessAsync` hitting the batch-size threshold can call `FlushAggregatorAsync` at the same time. `_flushLock` only wraps timer removal — `GetDataAsync` / `InvokeExecute` / `RemoveDataAsync` run outside it. Two concurrent flushes can each read the same batch, execute the handler twice, and race on remove.
**Fix:** Hold a per-aggregator lock (`ConcurrentDictionary<string, SemaphoreSlim>` keyed by aggregator name) across the whole flush body.

### R-040 — `Consumer` class mixes topology and lifecycle + 9 mutable fields set post-construction
**Files:** [src/ServiceConnect.Client.RabbitMQ/Consumer.cs:11-25, 46-249](src/ServiceConnect.Client.RabbitMQ/Consumer.cs)
**Category:** SRP / Cohesion / Encapsulation
**Description:** `Consumer` (249 lines) carries two distinct responsibilities: (a) RabbitMQ topology provisioning via 7 `Configure*Async` methods; (b) consumer lifecycle management. The class also declares 9 non-readonly instance fields that are not set in the constructor — they're assigned during `StartConsumingAsync`, creating a partially-initialized window. Most of those fields (e.g., `_durable`, `_exclusive`, `_autoDelete`, queue argument dicts) are config values that never change after init.
**Fix:** Extract `RabbitMqTopologyProvisioner` holding the 7 `Configure*Async` methods. Read configuration in the `Consumer` constructor and mark fields `readonly`.

### R-041 — `ConnectionFactory` construction duplicated between `Connection` and `Producer`; `Producer` bypasses `IServiceConnectConnection`
**Files:** [src/ServiceConnect.Client.RabbitMQ/Connection.cs:39-76](src/ServiceConnect.Client.RabbitMQ/Connection.cs#L39-L76) and [src/ServiceConnect.Client.RabbitMQ/Producer.cs:73-118](src/ServiceConnect.Client.RabbitMQ/Producer.cs#L73-L118); `ConfigureExchangeAsync` duplicated in `Consumer.cs:135-146` and `Producer.cs:268-280`
**Category:** DRY / DIP
**Description:** Both `Connection.BuildConnectionFactory` and `Producer.CreateConnectionAsync` build a `ConnectionFactory` with identical SSL/vhost/credential/recovery logic. `Consumer` and `Producer` also have near-identical `ConfigureExchangeAsync` methods. `Producer` maintains its own `IConnection` bypassing the `IServiceConnectConnection` abstraction `Consumer` uses.
**Fix:** Extract `ConnectionFactoryBuilder` reused by both `Connection` and `Producer`. Have `Producer` depend on `IServiceConnectConnection` (or a publisher-scoped equivalent). Move exchange-declare to a shared `BrokerTopologyHelper`.

### R-042 — Duplicate event-args classes across `ServiceConnect.Interfaces` and `ServiceConnect.Telemetry`
**Files:** `ConsumeEventArgs.cs`, `OutgoingEventArgs.cs`, `PublishEventArgs.cs`, `SendEventArgs.cs` exist identically in both `src/ServiceConnect.Interfaces/` and `src/ServiceConnect.Telemetry/`
**Category:** DRY / Layering
**Description:** Telemetry already references Interfaces. The duplicate types cause ambiguous-reference problems for consumers of both assemblies and force any fix to be applied in two places. The R-037 unit tests already had to use `using` aliases to resolve the ambiguity.
**Fix:** Delete the four event-args classes from `ServiceConnect.Telemetry` and have `ServiceConnectActivitySource` use the `ServiceConnect.Interfaces` versions.

### R-043 — `InMemoryProcessManagerFinder` uses reflection + `dynamic` to dispatch generics
**File:** [src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs:59-171](src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs#L59-L171)
**Category:** Inconsistency / Bad-practice (anti-pattern regressed after C-4)
**Description:** `InsertDataAsync` at line 122 uses `GetType().GetMethods().First(m => m.Name == "GetMemoryData" ...)` + `MakeGenericMethod` + `Invoke` on every call — the exact anti-pattern the C-4 registry refactor eliminated from the processors. `UpdateDataAsync` at line 168 uses `dynamic storedData = ...; int currentVersion = (int)storedData.Version;` — the only `dynamic` in the codebase. `FindDataAsync` compiles an `Expression.Lambda` per call. `GetMemoryData` is only `public` so it can be reflected against.
**Fix:** Introduce an internal `IVersioned { int Version { get; } }` interface on `MemoryData<T>` for the UpdateDataAsync cast. Replace the reflection-to-self in InsertDataAsync with a `ConcurrentDictionary<Type, Func<...>>` of compiled delegates (matches the registry pattern used elsewhere). Change `GetMemoryData` to `private`.

### R-044 — `ITimeoutStore.TimeoutInserted` event has no production subscriber
**Files:** [src/ServiceConnect.Interfaces/ITimeoutStore.cs:5](src/ServiceConnect.Interfaces/ITimeoutStore.cs#L5); fired from `InMemoryProcessManagerFinder.cs:234` and `MongoDbProcessManagerFinder.cs:229`
**Category:** Dead-code / Inconsistency
**Description:** `ProcessManagerTimeoutService` uses periodic polling via `GetTimeoutsBatchAsync`, not this event. The only subscriber is a unit test. Both persistence implementations fire the event every time a timeout row is inserted, producing overhead for no consumer.
**Fix:** Remove `TimeoutInserted` from `ITimeoutStore`, drop the fires from both implementations, and delete `TimeoutInsertedDelegate` from `IProcessManagerFinder.cs` (which is also a wrong home for the delegate — see R-077).

### R-045 — `ReplyProcessor` is `public` while sibling processors are `internal`
**File:** [src/ServiceConnect/Services/Processors/ReplyProcessor.cs:5](src/ServiceConnect/Services/Processors/ReplyProcessor.cs#L5)
**Category:** Inconsistency / Encapsulation
**Description:** `HandlerProcessor`, `ProcessManagerProcessor`, `AggregatorProcessor`, `StreamProcessor` are all `internal sealed`. `ReplyProcessor` is `public sealed` but is only registered by the DI extension — nothing external consumes the type.
**Fix:** Change to `internal sealed class ReplyProcessor`.

### R-046 — `InMemoryAggregatorPersistor.RemoveAll` is public but absent from `IAggregatorPersistor`
**File:** [src/ServiceConnect.Persistence.InMemory/InMemoryAggregatorPersistor.cs:66-75](src/ServiceConnect.Persistence.InMemory/InMemoryAggregatorPersistor.cs#L66-L75)
**Category:** Inconsistency / Encapsulation
**Description:** Only unit tests call `RemoveAll`. `MongoDbAggregatorPersistor` has no equivalent. The method is public on the concrete type, leaking an implementation detail and breaking symmetry.
**Fix:** Mark `internal` and expose to the test assembly via `InternalsVisibleTo` (already in place), or promote to the interface and add to the MongoDB implementation.

### R-047 — `Consumer.ConfigureQueueAsync` doesn't catch like its siblings
**File:** [src/ServiceConnect.Client.RabbitMQ/Consumer.cs:148-151](src/ServiceConnect.Client.RabbitMQ/Consumer.cs#L148-L151)
**Category:** Inconsistency / Error handling
**Description:** Every other `Configure*Async` in `Consumer` wraps broker calls in `try/catch(Exception) { _logger.LogWarning(...) }`. `ConfigureQueueAsync` does not. If intentional (queue declaration is critical), the distinction should be documented; if unintentional, a broker error during queue declaration propagates as an unhandled exception from `StartConsumingAsync`.
**Fix:** Document the intent inline, or add consistent handling that re-throws a typed `TransportException` after logging.

### R-048 — `SslProtocols.Tls12` hardcoded (excludes TLS 1.3)
**Files:** [src/ServiceConnect/Configuration/TransportConfiguration.cs:28](src/ServiceConnect/Configuration/TransportConfiguration.cs#L28); [src/ServiceConnect.Persistence.MongoDb/MongoDbSslOptions.cs:9](src/ServiceConnect.Persistence.MongoDb/MongoDbSslOptions.cs#L9)
**Category:** Security / TLS
**Description:** Pinning to TLS 1.2 prevents automatic upgrade to TLS 1.3. Operators who want 1.3 must explicitly override. `SslProtocols.None` (delegating protocol selection to the runtime) is the safer modern default.
**Fix:** Default both to `SslProtocols.None`. Update tests that assert a specific protocol. Document the change.

### R-049 — `AllowInsecureTls` missing SECURITY WARNING XML doc
**File:** [src/ServiceConnect.Persistence.MongoDb/MongoDbSslOptions.cs:10](src/ServiceConnect.Persistence.MongoDb/MongoDbSslOptions.cs#L10)
**Category:** Security / Docs
**Description:** `TransportConfiguration.AcceptablePolicyErrors` and `CertificateValidationCallback` carry prominent `SECURITY WARNING` XML doc comments. `MongoDbSslOptions.AllowInsecureTls` has none. Setting it to `true` disables all TLS validation against the MongoDB endpoint.
**Fix:** Add matching `/// <remarks>SECURITY WARNING: ...</remarks>` doc; optionally log a warning at `MongoClientFactory.Create` time when the flag is true.

### R-050 — `Producer` does not dispose its `SemaphoreSlim` instances
**File:** [src/ServiceConnect.Client.RabbitMQ/Producer.cs:26-27, 194-205](src/ServiceConnect.Client.RabbitMQ/Producer.cs#L26-L27)
**Category:** Resource management
**Description:** `_publishLock` and `_connectionSemaphore` are never disposed in `DisposeAsyncCore`. A resource leak when `AvailableWaitHandle` has been accessed.
**Fix:** Call `_publishLock.Dispose()` and `_connectionSemaphore.Dispose()` at the end of `DisposeAsyncCore`.

### R-051 — Hardcoded 5000ms graceful-drain deadline in `RabbitMqConsumerHost`
**File:** [src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs:184](src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs#L184)
**Category:** Hardcoded
**Description:** Deadline is `Environment.TickCount64 + 5000`. No configuration surface; may be wrong for slow consumers or high-throughput scenarios.
**Fix:** Expose as `TransportConfiguration.GracefulShutdownTimeout` (default 5 s) and pass through to the host.

### R-052 — `Consumer` uses `_model!` null-forgiving in broad-catch Configure methods
**File:** [src/ServiceConnect.Client.RabbitMQ/Consumer.cs:135-248](src/ServiceConnect.Client.RabbitMQ/Consumer.cs#L135-L248)
**Category:** Defensive coding
**Description:** If `_model` is null (e.g., channel creation silently returned null), `_model!` throws `NullReferenceException` inside the catch-all, which swallows and continues — partial infrastructure silently set up.
**Fix:** After `CreateChannelAsync`, add `if (_model == null) throw new InvalidOperationException("Failed to create RabbitMQ channel.");` so any future regression is loud.

---

## LOW (19)

### R-053 — `IFilter.Bus` property defined but never set by pipeline
**Files:** [src/ServiceConnect.Interfaces/IFilter.cs:11](src/ServiceConnect.Interfaces/IFilter.cs#L11); `FilterPipeline.ExecuteFiltersAsync`
**Description:** Property is a trap for filter implementors — accessing `this.Bus` returns null. Constructor injection is the established pattern everywhere else.
**Fix:** Remove the property (breaking change) or set it in `FilterPipeline` before dispatch.

### R-054 — `IMessageHandlerProcessor` interface is unused
**File:** `src/ServiceConnect.Interfaces/IMessageHandlerProcessor.cs`
**Description:** Zero implementers, zero references. Superseded by `IMessageProcessor` + processor pipeline.
**Fix:** Delete the file.

### R-055 — `IMessageBusReadStream.CompleteEventHandler` and `HandlerCount` are dead members
**Files:** `src/ServiceConnect.Interfaces/IMessageBusReadStream.cs:11,13`; `src/ServiceConnect/Services/MessageBusReadStream.cs:14-15`
**Description:** Never assigned, read, or invoked in production. `CompleteEventHandler` initialized to `null!`.
**Fix:** Remove from interface + implementation.

### R-056 — `IServiceConnectConnection.ConnectAsync` unnecessary on public interface
**File:** [src/ServiceConnect.Client.RabbitMQ/IServiceConnectConnection.cs:7](src/ServiceConnect.Client.RabbitMQ/IServiceConnectConnection.cs#L7)
**Description:** Only used internally by `Connection.CreateChannelAsync`. External callers go via `CreateChannelAsync`.
**Fix:** Remove from the interface; keep as private/internal on `Connection`.

### R-057 — Configuration interfaces expose mutable setters on singletons
**Files:** `src/ServiceConnect.Interfaces/Configuration/IBusConfiguration.cs`, `IQueueConfiguration.cs`, `IPersistenceConfiguration.cs`, `ITransportConfiguration.cs`
**Description:** All properties are `{ get; set; }`. Registered as singletons. Any component can mutate configuration at runtime.
**Fix:** Split into read-only interfaces (consumed by runtime) + mutable builder-time types (used by `ServiceConnectBuilder`). Breaking change — consider for next major.

### R-058 — `QueueConfiguration.AddQueueMapping` mutates `List` without synchronization + fragile cast
**File:** [src/ServiceConnect/Configuration/QueueConfiguration.cs:17-32](src/ServiceConnect/Configuration/QueueConfiguration.cs#L17-L32)
**Description:** `ConcurrentDictionary` serializes dict access but not the `List` values inside. Code also casts `QueueMappings` (typed `IDictionary`) back to `ConcurrentDictionary` for `AddOrUpdate` — if a consumer assigns a plain `Dictionary` via the public setter, the cast throws.
**Fix:** Back with a private `ConcurrentDictionary<string, ImmutableList<string>>` (compare-and-swap). Remove the public setter; expose `IReadOnlyDictionary`.

### R-059 — `DefaultProcessManagerPropertyMapper.Mappings` is a public settable `List<T>`
**File:** [src/ServiceConnect/Services/Processors/DefaultProcessManagerPropertyMapper.cs:8](src/ServiceConnect/Services/Processors/DefaultProcessManagerPropertyMapper.cs#L8)
**Description:** External code can replace the list or mutate it (`Clear`, `RemoveAt`) after configuration.
**Fix:** Expose `IReadOnlyList<ProcessManagerToMessageMap>`, back with a private `List`.

### R-060 — `Envelope.Headers` mutable with public setter
**File:** [src/ServiceConnect.Interfaces/Envelope.cs:5](src/ServiceConnect.Interfaces/Envelope.cs#L5)
**Description:** Setter allows replacement of the dict mid-pipeline, losing earlier stage headers.
**Fix:** Make `init`-only or set in constructor only; filters mutate via the existing dict.

### R-061 — Missing `sealed` on concrete implementation classes
**Files:** `CacheProvider`, `CacheItem`, `SlidingDetails`, `MemoryData<T>`, `MongoDbData<T>`, `ConsumeEventResult`, `ProcessManagerToMessageMap`, `TimeoutsBatch`, `ConsumeEventArgs` (keep `OutgoingEventArgs` open — it's subclassed)
**Description:** Concrete DTOs and implementation classes with no extension points.
**Fix:** Add `sealed` to each.

### R-062 — `ServiceConnectException` should be `abstract`
**File:** [src/ServiceConnect.Interfaces/Exceptions/ServiceConnectException.cs:3](src/ServiceConnect.Interfaces/Exceptions/ServiceConnectException.cs#L3)
**Description:** Callers can `throw new ServiceConnectException(...)` bypassing the typed subclass hierarchy (PersistenceException, TransportException).
**Fix:** Mark `abstract`. Downstream callers must pick a typed subclass.

### R-063 — Magic strings/numbers across RabbitMQ + Telemetry layers
**Files:** [Consumer.cs:157, 68, 162, 199, 227](src/ServiceConnect.Client.RabbitMQ/Consumer.cs); [Producer.cs:130](src/ServiceConnect.Client.RabbitMQ/Producer.cs#L130); [RabbitMqConsumerHost.cs:70](src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs#L70); [ServiceConnectActivitySource.cs:54, 102](src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs)
**Description:** `".Retries"`, `".Retries.DeadLetter"`, `"fanout"`, `"direct"`, `"MessageId"`, `"DestinationAddress"` scattered as literals. RabbitMQ client ships `ExchangeType.Fanout`/`Direct`. The telemetry strings have `HeaderKeys` counterparts (requires adding an `Interfaces` reference to `Telemetry`).
**Fix:** Introduce `RetryQueueSuffix`/`RetryDeadLetterSuffix` constants; use `ExchangeType.Fanout`/`Direct`; add Interfaces reference to Telemetry and use `HeaderKeys`.

### R-064 — `Aggregator<T>.BatchSize()` / `Timeout()` virtual methods + no dual-zero validation
**File:** [src/ServiceConnect.Interfaces/Aggregator.cs:15-27](src/ServiceConnect.Interfaces/Aggregator.cs#L15-L27)
**Description:** Virtual parameterless methods returning config constants are unusual in idiomatic C#. Both defaulting to zero is a silent configuration error — the aggregator would buffer messages forever.
**Fix:** Add validation in `AggregatorRegistry.BuildDescriptor` rejecting `BatchSize==0 && Timeout==TimeSpan.Zero`. Consider switching to abstract properties in next major.

### R-065 — Missing `QueueName` validation in `Bus.StartConsumingAsync`
**Files:** [src/ServiceConnect/Bus.cs:228](src/ServiceConnect/Bus.cs#L228); [src/ServiceConnect/Configuration/QueueConfiguration.cs:8](src/ServiceConnect/Configuration/QueueConfiguration.cs#L8)
**Description:** `QueueName` defaults to `""`. Empty string is AMQP-legal (broker auto-generates a name) but almost certainly not the intent — fails with a confusing broker error.
**Fix:** Throw `InvalidOperationException` early in `StartConsumingAsync` if `QueueName` is empty/whitespace, with a message pointing at the configuration API.

### R-066 — Missing XML docs on handler interfaces + `ServiceConnectActivitySource` public methods
**Files:** `IMessageHandler<T>`, `IProcessHandler<TData,TMessage>`, `IStreamHandler<T>`, `IConsumeContext`, `Envelope`, `Message`, `ServiceConnectActivitySource.Publish/Consume/Send/TryGetExistingContext`
**Description:** `IBus` and `IProducer` have full XML docs — but the handler interfaces (which consumers actually implement) have zero. Telemetry public entry points also undocumented.
**Fix:** Add `/// <summary>` to at least the four handler interfaces and four telemetry public methods.

### R-067 — French XML comments in InMemory persistence
**Files:** [src/ServiceConnect.Persistence.InMemory/CacheItem.cs:11](src/ServiceConnect.Persistence.InMemory/CacheItem.cs#L11); [SlidingDetails.cs:6](src/ServiceConnect.Persistence.InMemory/SlidingDetails.cs#L6); `SlidingDetails.CanExpire` has a copy-pasted doc from `Contains`
**Fix:** Translate to English; rewrite the `CanExpire` doc to describe what `CanExpire` actually does.

### R-068 — CLEAN duplication opportunities (low-priority refactor)
**Files:** Retry overloads ([Retry.cs:5-64](src/ServiceConnect.Client.RabbitMQ/Retry.cs)), `ServiceConnectActivitySource` Publish/Consume/Send tag setup, `HandlerScanner` four-branch interface matching + DI-registration mirror in `ServiceCollectionExtensions:82-121`
**Description:** Stable, working, near-duplicate patterns that could be collapsed. Zero bug surface today.
**Fix:** Extract helpers at the team's leisure; don't block on these.

### R-069 — `DestinationMachine`/`SourceMachine` leak internal hostname to audit-queue consumers
**Files:** [RabbitMqConsumerHost.cs:149](src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs#L149); `Producer.GetHeaders`
**Category:** Security / info disclosure (defence-in-depth)
**Description:** `Environment.MachineName` flows into every audited message, visible to any consumer of the audit exchange. Information disclosure in shared-broker deployments.
**Fix:** Add opt-in `IncludeMachineNameInHeaders` config (default false) or redact from headers forwarded to audit/error.

### R-070 — `EnrichWithMessage`/`EnrichWithMessageBytes` callbacks invite operators to log payload
**File:** [src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs:63, 124, 174](src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L63)
**Category:** Security / docs
**Description:** The callbacks receive the raw message / message bytes. An operator who naively adds the body as a span tag flows message content (possibly PII/secrets) to their OTel collector. No library-level guard.
**Fix:** Add `/// <remarks>WARNING: do not add raw payload bytes as span tags — they may contain PII or secrets.</remarks>` on both callback properties.

### R-071 — `MessageBusWriteStream.CloseAsync` protocol uses an empty packet as terminator
**File:** [src/ServiceConnect/Services/MessageBusWriteStream.cs:45-58](src/ServiceConnect/Services/MessageBusWriteStream.cs#L45-L58)
**Description:** Close sends an empty-body packet whose `PacketNumber == LastPacketNumber`. Reader iterates `0..LastPacketNumber` inclusive and happens to write 0 bytes for the close packet. Works today but future changes to the close packet would corrupt reassembly.
**Fix:** Comment the invariant at the writer, or have close set `LastPacketNumber = _packetNumber - 1` with close as a distinct control message.

---

## INFO (8)

### R-072 — `InMemoryProcessManagerFinder.FindDataAsync` compiles an `Expression.Lambda` per call
**File:** [src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs:110](src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs#L110)
**Description:** Test/dev implementation only; not a performance concern. Rolled into R-043 if that refactor proceeds.

### R-073 — `catch (Exception)` swallow in `InMemoryProcessManagerFinder.FindDataAsync:47-51`
**Description:** Silently returns null for ANY mapper delegate failure (including bugs in the mapping expression). MongoDB implementation logs + rethrows as `PersistenceException`. Add at minimum a logger dependency and debug-level log.

### R-074 — `MongoDbProcessManagerFinder.Ensure*Index` calls sync `CreateOne`
**File:** [src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs:315-333](src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs#L315-L333)
**Description:** `CreateOneAsync` exists. Runs once per collection (guarded) but blocks the thread during startup.
**Fix:** Switch to `CreateOneAsync` and propagate async.

### R-075 — `CacheProvider.Default` static singleton unused in production
**File:** [src/ServiceConnect.Persistence.InMemory/CacheProvider.cs:8](src/ServiceConnect.Persistence.InMemory/CacheProvider.cs#L8)
**Description:** Accessed only by a unit test's null-check. Remove the property and update the test.

### R-076 — `CacheProvider.Contains(object key)` is non-generic while siblings are generic
**File:** [src/ServiceConnect.Persistence.InMemory/CacheProvider.cs:113](src/ServiceConnect.Persistence.InMemory/CacheProvider.cs#L113)
**Description:** Inconsistent API. `Add/Get/Remove/Keys` all take `TKey`.
**Fix:** Make `Contains<TKey>(TKey key)`.

### R-077 — `TimeoutInsertedDelegate` co-located on wrong interface file
**File:** Declared in [src/ServiceConnect.Interfaces/IProcessManagerFinder.cs:3](src/ServiceConnect.Interfaces/IProcessManagerFinder.cs#L3)
**Description:** Should sit with `ITimeoutStore` (the interface that uses it) or a standalone `Delegates.cs`. Moot if R-044 proceeds.

### R-078 — Infrastructure projects reference `ServiceConnect` core for builder extensions
**Files:** `ServiceConnect.Client.RabbitMQ.csproj`, `ServiceConnect.Persistence.MongoDb.csproj`, `ServiceConnect.Persistence.InMemory.csproj`
**Description:** Pragmatic; extensions live on `ServiceConnectBuilder`. Would be cleaner if `ServiceConnectBuilder` lived in a `ServiceConnect.DependencyInjection` assembly, but not actionable without a larger layering reorganization. Flag for future.

### R-079 — `AggregatorProcessor.OnTimerFired` fire-and-forget with `OnlyOnFaulted` continuation
**File:** [src/ServiceConnect/Services/Processors/AggregatorProcessor.cs:73](src/ServiceConnect/Services/Processors/AggregatorProcessor.cs#L73)
**Description:** Fine for timer callbacks. If the logger itself throws, the exception is lost. Flagged for awareness only; no action required.

---

## Themes and suggested ordering

Looking at the 42 findings as a whole, the natural clusters are:

1. **Security (R-038, R-048, R-049, R-069, R-070).** R-038 is the only High. R-048 and R-049 are TLS-defaults hygiene — low-risk to fix. R-069 and R-070 are docs/opt-in changes.

2. **InMemoryProcessManagerFinder cleanup (R-043 + dependent Infos R-072, R-073).** One coherent refactor: eliminate the self-reflection + `dynamic` + per-call expression compile. Would match the C-4 registry-refactor pattern.

3. **RabbitMQ adapter cohesion (R-040, R-041, R-047, R-051, R-052).** Another coherent bundle: Consumer SRP split + shared ConnectionFactoryBuilder + Producer using IServiceConnectConnection + consistent ConfigureQueue error handling. This is the biggest refactor in the report and would likely ship as its own C-6 group.

4. **Concurrency (R-039, R-050, R-058).** Three separate small fixes: aggregator flush lock, SemaphoreSlim dispose, list-in-dict synchronization.

5. **Dead code removal (R-044, R-053, R-054, R-055, R-056, R-075).** Low-risk, small-diff cleanup pass.

6. **API surface tightening (R-045, R-046, R-057, R-059, R-060, R-061, R-062, R-065).** Mostly breaking changes — batch for a major version bump.

7. **Telemetry duplication + magic strings (R-042, R-063, R-066).** Coherent small bundle — delete duplicate event-args classes, add Interfaces reference, use HeaderKeys constants, document public methods.

8. **Docs, comments, validation misc (R-064, R-067, R-068, R-071, R-076, R-077, R-078, R-079).** Scatter-shot; not worth coordinating.

No findings block shipping the current branch. **R-038 (routing-slip injection)** is the only one I'd call out for near-term action given the deployment model — if the service runs behind a trusted broker with all publishers operated by your team, risk is low; in any multi-tenant or externally-reachable broker deployment, it's a real exposure vector.

Report also written to `docs/2026-04-13-code-review-master-report.md`.
