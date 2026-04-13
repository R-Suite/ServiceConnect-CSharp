# Remaining Issues — Verified but Deferred

Issues verified against source code on 2026-04-12. R-016/B-01 (CancellationToken) and R-034 (race condition) completed in Group C-1 on 2026-04-13. R-017/R-018 (async filter pipeline + fail-closed dedup) and R-032 (settings DI) completed in Group C-2 on 2026-04-13. R-020/R-021 (Client + ProcessManagerProcessor SRP refactor) completed in Group C-3 on 2026-04-13. R-009 (service locator + reflection in remaining three processors) completed in Group C-4 on 2026-04-13. R-028 (unit test gap-fill) completed in Group C-5 on 2026-04-13. R-037 (TryGetExistingContext carrier-type bug) fixed on 2026-04-13. All known issues now resolved.

## From Code Review Plan (Medium Priority)

| ID | Category | Description | Breaking? | Notes |
|----|----------|-------------|-----------|-------|
| B-01 | .NET Best Practices | Missing CancellationToken on all IBus async methods | **Yes** | **Done** (Group C-1) — optional CT parameter on all IBus async methods, threaded through pipeline/transport/persistence |
| B-02 | CLEAN | Mutable `IDictionary<string, object>` exposure in TransportConfiguration (line 37) | No | **Done** (Group A) — IReadOnlyDictionary + SetClientSetting method |
| T-01 | Security | `CheckCertificateRevocation = false` in MessageDeduplicationPersistorMongoDbSsl | No | **Done** (Group A) — flipped to true |
| T-02 | Tech Debt | Inconsistent exception types in InMemoryProcessManagerFinder — mix of InvalidOperationException and PersistenceException | No | **Done** (Group A) — standardized on PersistenceException |

## From Deferred Issues (Confirmed Real)

| ID | Category | Description | Scope |
|----|----------|-------------|-------|
| R-009 | Architecture | Service locator anti-pattern in all Processors (HandlerProcessor, ProcessManagerProcessor, StreamProcessor, AggregatorProcessor) | **Done** (Groups C-3 + C-4) — reflection + discovery moved to four internal registries with compiled expression-tree delegates; dispatch-time `IServiceProvider.GetService` for handler types unavoidable (dispatch is by runtime type) |
| R-016 | .NET Best Practices | Missing CancellationToken on public async APIs | **Done** (Group C-1) — completed with B-01 |
| R-017/R-018 | Error Handling | Silent exception swallowing in dedup filter persistors (OutgoingFilter, MongoDb persistors) | **Done** (Group C-2) — IFilter/pipeline async; outgoing dedup now fail-closed; MongoDb persistor inner swallows removed |
| R-020/R-021 | SRP | ProcessManagerProcessor and Client have too many responsibilities | **Done** (Group C-3) — Client split into `RabbitMqConsumerHost` + `MessageRetryHandler` + `MessageAuditPublisher`; `ProcessManagerProcessor` thinned via `ProcessManagerHandlerRegistry` with compiled-expression delegates |
| R-022 | Architecture | Dedup filter combinatorial explosion | **Done** (Group B) — collapsed 8 filter variants to 2 + PersistorFactory, removed Redis support |
| R-027 | Tech Debt | MongoDbSsl manual connection string parsing | **Done** (Group B) — merged MongoDbSsl into MongoDb persistor with driver-native MongoUrl parsing |
| R-028 | Testing | Zero unit test coverage | **Done** (Group C-5) — every logic-bearing non-integration-heavy class now has unit tests. Remaining untested files are either (a) broker/DB integration classes (Consumer/Producer/Connection/Client, MongoDbAggregatorPersistor/MongoDbProcessManagerFinder) covered comprehensively by E2E, or (b) logic-less POCOs (config classes, event-args, options). |
| R-032 | Architecture | DeduplicationFilterSettings singleton pattern | **Done** (Group C-2) — POCO + IOptions<T> + AddMessageDeduplicationFilter extension method; PersistorFactory removed; DeduplicationCleanupHostedService replaces static Timer |
| R-034 | Async/Threading | Race condition in Bus.StartConsumingAsync — lock released before long-running await | **Done** (Group C-1) — SemaphoreSlim lifecycle serialization in Bus, with new unit tests |

## Discovered During This Series

| ID | Category | Description | Scope |
|----|----------|-------------|-------|
| R-037 | Bug | **Done** (2026-04-13) — `ExtractTraceIdAndState` now pattern-matches both `Dictionary<string, object>` (Consume / AMQP header path) and `Dictionary<string, string>` (TryGetExistingContext path). Public API unchanged. Unit test flipped from bug-characterization to correctness. |

## Not Real (Removed)

| ID | Original Claim | Why Removed |
|----|---------------|-------------|
| R-010 | Layering violation — RabbitMQ depends on Core via IMessageTypeRegistry | IMessageTypeRegistry is in Interfaces (correct layer); RabbitMQ does not reference it |
| R-011/R-012/R-013 | IBus fat interface violates ISP | IBus has ~10 cohesive members — not a fat interface |
