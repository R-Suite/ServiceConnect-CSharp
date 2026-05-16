# PR Title

ServiceConnect v7: clean-architecture rewrite, async-first API, new docs & examples

# PR Body

## Overview

Complete rewrite of ServiceConnect targeting v7. Replaces the legacy static `Bus`
with a thin orchestrator driven by `Microsoft.Extensions.DependencyInjection`,
rebuilds the interface surface async-first, merges and consolidates packages,
and ships a hand-curated docs site plus runnable examples.

This is a breaking change and is **not backwards-compatible** with v6.

## Highlights

### API & architecture
- `AddServiceConnect(...)` DI extension + fluent `ServiceConnectBuilder`; `Bus`
  is now a thin orchestrator with constructor-injected services.
- `ServiceConnect.Interfaces` redesigned: split configuration, async-first
  signatures, typed exceptions.
- `CancellationToken` threaded end-to-end through `IBus`, `IProducer`,
  `IConsumer`, middleware pipelines, `IMessageProcessor`, `IConsumeContext`,
  `IProcessManagerFinder`, `ITimeoutStore`, and `IAggregatorPersistor`.
- `IAsyncDisposable` across the disposal chain; all sync-over-async removed
  from the RabbitMQ transport.
- New processor chain: `HandlerProcessor`, `ReplyProcessor`,
  `ProcessManagerProcessor`, `AggregatorProcessor`, `StreamProcessor`, with
  pluggable registries (`MessageHandlerRegistry`,
  `ProcessManagerHandlerRegistry`, `AggregatorRegistry`,
  `StreamHandlerRegistry`) replacing per-message reflection.
- Polymorphic message dispatch with type-hierarchy walking.
- Native streaming support: `bus.CreateStream<T>(endpoint)` →
  `IMessageBusWriteStream` / `IStreamHandler<T>`.
- `IMessageProcessingMiddleware` + modernized `ISendMessageMiddleware`; legacy
  `IProcessMessageMiddleware` removed.
- `BusHostedService` for `AutoStartConsuming`; `ProcessManagerTimeoutService`
  for timeout polling.

### Transport
- RabbitMQ client rewritten for async-first interfaces; lazy `Producer`
  connection, safe dispose, nack on retry failure, async Reply/Route.
- Consumer split into `Client` facade + `RabbitMqConsumerHost`; audit/retry
  concerns extracted (`MessageAuditPublisher`, `MessageRetryHandler`,
  `HeaderHelpers`).

### Persistence & filters
- Merged `ServiceConnect.Persistence.MongoDb` package with SSL as a config
  option; SqlServer / Redis persistence removed.
- `InMemory` persistence renamed (fixes `Persistance` typo).
- 8 deduplication filter variants collapsed into 2 generic classes; outgoing
  dedup is now fail-closed; async `IMessageDeduplicationPersistor`;
  `DeduplicationCleanupHostedService` for periodic expiry.
- `IProcessManagerFinder` split into CRUD + `ITimeoutStore`.

### Performance
- Zero-copy deserialization via `ReadOnlyMemoryStream`,
  `ReadOnlySequenceStream`, and `ReadOnlyMemory<byte>` /
  `ReadOnlySequence<byte>` overloads on `IMessageSerializer`.
- Stream-based JSON serialization with cached `JsonSerializer`.
- Cached middleware chain, PM property mappers, routing-slip delegates,
  exchange declares, type names, producer name.
- `$facet` aggregation for MongoDB timeout polling; sorted index for in-memory
  polling; shared Mongo client, async indexes, projections.

### Security
- Message size limits on publish and consume; header count/size limits;
  read-only headers for handlers.
- `SequenceId` validation, caps on active streams and packet numbers.
- Reply-destination and routing-slip queue validation.
- Gzip magic-byte check + decompression size limit.
- `Random.Shared` for retry jitter; exponential backoff with jitter; SSL cert
  revocation check in `MongoDbSsl` dedup persistor; exception sanitization;
  backoff overflow cap.

### Project structure
- Solution consolidated from **17 → 8 projects**; dead projects removed;
  legacy integration tests and sample projects dropped.
- MIT relicense.
- `.NET 10` target.

### Testing
- New `E2E` project using **TestContainers** (real RabbitMQ + MongoDB) covering
  pub/sub, point-to-point, request/reply, scatter-gather, routing slip,
  content-based routing, competing consumers, priority queue, streaming,
  aggregator, process manager, deduplication, cancellation, middleware,
  exception handler, lifecycle, filters, SSL.
- Unit-test gap-fill across serializer, builder, DI, configuration, retry,
  cache, persistence, telemetry, headers, property mappers, streams.

### Documentation & examples
- New Astro + Starlight docs site at `website/` replacing DocFX, with
  hand-curated reference (Bus, Messages, Handlers, Configuration,
  Process Managers, Filters & Middleware, Extension Points) and a Learn
  track (core concepts, messaging patterns, operations).
- Runnable examples at `examples/`: Aggregator, CompetingConsumers,
  ContentBasedRouting, and more, with `docker-compose` + `run.sh/ps1`.
- Root `README.md` rewritten for the new API.
- CI workflow to build & deploy the docs site.

## Test plan

- [ ] `dotnet build` clean in `src/` and `tests/`
- [ ] `dotnet test` passes for unit tests
- [ ] `dotnet test` passes for E2E tests (requires Docker for TestContainers)
- [ ] `cd website && npm install && npm run build` produces a clean site
- [ ] Each `examples/*/run.sh` starts dependencies and exercises the scenario
- [ ] Review MIT `LICENSE.md` and updated `README.md`
- [ ] Confirm target version bump in `ServiceConnect.nuspec` (currently
  `6.0.2` — should move to `7.0.0`)
