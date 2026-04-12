# Remaining Issues — Verified but Deferred

Issues verified against source code on 2026-04-12. Tackle after the critical async/threading fixes are complete.

## From Code Review Plan (Medium Priority)

| ID | Category | Description | Breaking? | Notes |
|----|----------|-------------|-----------|-------|
| B-01 | .NET Best Practices | Missing CancellationToken on all IBus async methods | **Yes** | Requires major version bump; touches IBus, Bus, Client, all processors |
| B-02 | CLEAN | Mutable `IDictionary<string, object>` exposure in TransportConfiguration (line 37) | No | Change to IReadOnlyDictionary + setter method |
| T-01 | Security | `CheckCertificateRevocation = false` in MessageDeduplicationPersistorMongoDbSsl | No | One-line fix but needs integration testing with real certs |
| T-02 | Tech Debt | Inconsistent exception types in InMemoryProcessManagerFinder — mix of InvalidOperationException and PersistenceException | No | Standardize on PersistenceException |

## From Deferred Issues (Confirmed Real)

| ID | Category | Description | Scope |
|----|----------|-------------|-------|
| R-009 | Architecture | Service locator anti-pattern in all Processors (HandlerProcessor, ProcessManagerProcessor, StreamProcessor, AggregatorProcessor) | Large — inherent to message dispatch design |
| R-016 | .NET Best Practices | Missing CancellationToken on public async APIs | Same as B-01 above |
| R-017/R-018 | Error Handling | Silent exception swallowing in dedup filter persistors (OutgoingFilter, MongoDb persistors) | Medium — needs IFilter interface change for async |
| R-020/R-021 | SRP | ProcessManagerProcessor and Client have too many responsibilities | Large — internal structure refactor |
| R-022 | Architecture | Dedup filter combinatorial explosion | Large — filter project restructuring |
| R-027 | Tech Debt | MongoDbSsl manual connection string parsing | Large — full rewrite of MongoDB SSL dedup filter |
| R-028 | Testing | Zero unit test coverage | Large — ongoing effort |
| R-032 | Architecture | DeduplicationFilterSettings singleton pattern | Medium — requires DI migration of filter project |
| R-034 | Async/Threading | Race condition in Bus.StartConsumingAsync — lock released before long-running await | Medium — mitigated by local consumer copy; full fix needs CancellationToken (R-016) |

## Not Real (Removed)

| ID | Original Claim | Why Removed |
|----|---------------|-------------|
| R-010 | Layering violation — RabbitMQ depends on Core via IMessageTypeRegistry | IMessageTypeRegistry is in Interfaces (correct layer); RabbitMQ does not reference it |
| R-011/R-012/R-013 | IBus fat interface violates ISP | IBus has ~10 cohesive members — not a fat interface |
