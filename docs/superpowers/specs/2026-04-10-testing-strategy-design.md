# ServiceConnect Testing Strategy Design

## Overview

Overhaul the ServiceConnect-CSharp test suite to achieve 80%+ combined coverage (unit + E2E), replace all sample projects with automated E2E tests, and remove the existing integration test project. Use TestContainers for infrastructure dependencies.

## Current State

- **37 total tests** across 3 projects (23 unit, 7 integration, 7 filter)
- Integration tests require a live MongoDB at `localhost` with no container management
- 26 sample directories (mostly dead net4.5 code) demonstrate patterns but aren't automated
- No TestContainers usage anywhere
- Very low test coverage

## Goals

1. Combined unit + E2E coverage exceeding 80%
2. New `ServiceConnect.EndToEndTests` project using TestContainers for RabbitMQ and MongoDB
3. E2E tests covering all messaging patterns currently demonstrated by samples
4. Remove all sample projects (replaced by E2E tests)
5. Remove `ServiceConnect.IntegrationTests` project (tests migrated)
6. Modernize filter test dependencies

## Project Structure Changes

### Additions
- `src/ServiceConnect.EndToEndTests/` — new E2E test project with TestContainers

### Deletions
- `src/ServiceConnect.IntegrationTests/` — removed from solution and disk
- `samples/` — entire directory removed (26 sample projects)

### Modifications
- `src/ServiceConnect.sln` — remove IntegrationTests, add EndToEndTests
- `src/ServiceConnect.UnitTests/` — expanded with new test files
- `filters/.../Tests/*.csproj` — package versions updated

### Migrations
- `BusSetupTests` (from IntegrationTests) -> `ServiceCollectionExtensionsTests` in UnitTests
- `MongoDbProcessManagerFinderTests` (from IntegrationTests) -> EndToEndTests with TestContainers

## E2E Project Design

### Dependencies
- `xunit 2.9.2`, `xunit.runner.visualstudio 2.8.2`, `Microsoft.NET.Test.Sdk 17.14.0`
- `Testcontainers.RabbitMq` (official RabbitMQ TestContainers module)
- `Testcontainers.MongoDb` (official MongoDB TestContainers module)
- Project references: ServiceConnect, ServiceConnect.Interfaces, ServiceConnect.Client.RabbitMQ, ServiceConnect.Persistence.MongoDb, ServiceConnect.Persistence.InMemory
- Target framework: `net10.0`

### Test Collections (Isolated Container Sets)

**Collection 1: `MessagingCollection`** (RabbitMQ only)
- `MessagingFixture` implements `IAsyncLifetime`
- Spins up one RabbitMQ container (`rabbitmq:3-management`)
- Exposes connection string and a `CreateBus(Action<ServiceConnectBuilder> configure)` helper
- Each test class gets unique queue name prefix to prevent cross-talk

**Collection 2: `PersistenceCollection`** (RabbitMQ + MongoDB)
- `PersistenceFixture` implements `IAsyncLifetime`
- Spins up both RabbitMQ and MongoDB containers
- Exposes both connection strings and bus creation helper with MongoDB persistence wired in
- Same unique queue name strategy

### Queue Naming Strategy
- Format: `{TestClassName}_{TestMethodName}_{Guid:N[..8]}`
- Ensures complete isolation between tests
- Short GUID suffix handles parallel test runs

### Timeout Handling
- Each E2E test uses a `CancellationTokenSource` with a 30-second timeout
- Tests use `TaskCompletionSource<T>` with the cancellation token to await message arrival
- Prevents hung tests from blocking CI

## E2E Test Classes

### MessagingCollection (RabbitMQ only)

| Test Class | Pattern | Key Tests |
|---|---|---|
| `PointToPointTests` | Basic send/receive | Send message to endpoint, handler receives it; multiple messages delivered in order |
| `PublishSubscribeTests` | Pub/sub fan-out | Publish message, multiple subscribers each receive a copy; unsubscribed consumers don't receive |
| `RequestReplyTests` | Request/reply | Send request, responder replies, requestor gets response; timeout when no responder |
| `PriorityQueueTests` | Priority consumption | Higher priority messages consumed before lower priority ones |
| `RoutingSlipTests` | Multi-step routing | Message routes through 3 steps in order; each step can modify the message |
| `ScatterGatherTests` | Broadcast + collect | Request sent to multiple responders, all responses collected |
| `CompetingConsumersTests` | Load balancing | Multiple consumers on same queue, each message delivered to only one consumer |
| `ContentRoutingTests` | Content-based routing | Messages routed to different handlers based on content/type |
| `PolymorphicMessageTests` | Polymorphic handlers | Base type handler receives derived message types |
| `FilterPipelineTests` | Filters/middleware | Outgoing filter modifies headers; incoming filter can block messages; filter ordering respected |
| `StreamingTests` | Large message streaming | Stream handler receives streamed message chunks |
| `MessageDeduplicationTests` | Dedup filter | Duplicate message (same ID, redelivered) is filtered out |
| `BusLifecycleTests` | Start/stop/dispose | Bus starts consuming, stops gracefully, disposes resources cleanly |

### PersistenceCollection (RabbitMQ + MongoDB)

| Test Class | Pattern | Key Tests |
|---|---|---|
| `ProcessManagerTests` | Saga/workflow | Multi-step workflow: start message creates state, subsequent messages advance state, completion message finalizes |
| `AggregatorTests` | Message aggregation | Partial messages collected, aggregated result emitted when complete |
| `MongoDbProcessManagerFinderTests` | Persistence CRUD | Insert/find/update/delete process manager data (migrated from integration tests) |
| `MongoDbAggregatorPersistorTests` | Persistence CRUD | Insert/retrieve/delete aggregator data |

## Unit Test Expansion

### New Test Files in `ServiceConnect.UnitTests`

| File | Target Class | Key Areas |
|---|---|---|
| `FilterPipelineTests.cs` | `FilterPipeline` | Executes filters in order; filter returning false stops pipeline; empty pipeline passes through |
| `SendMessagePipelineTests.cs` | `SendMessagePipeline` | Calls producer with correct args; applies outgoing filters; handles publish vs send vs route |
| `RequestReplyManagerTests.cs` | `RequestReplyManager` | Registers pending request; correlates reply by ID; timeout throws `RequestTimeoutException` |
| `NewtonsoftJsonMessageSerializerTests.cs` | `NewtonsoftJsonMessageSerializer` | Serializes/deserializes messages; handles type info; throws `SerializationException` on bad input |
| `ServiceConnectBuilderTests.cs` | `ServiceConnectBuilder` | Fluent configuration; adds filters; registers transport/persistence/pipeline |
| `ServiceCollectionExtensionsTests.cs` | `ServiceCollectionExtensions` | Registers `IBus` in DI; builder callback invoked (migrated from integration `BusSetupTests`) |
| `BusConfigurationTests.cs` | Configuration classes | Default values; property assignment; validation |
| `ProducerTests.cs` | RabbitMQ `Producer` | Serializes and publishes to correct exchange; sets headers; handles routing key (mocked RabbitMQ connection) |
| `ConsumerTests.cs` | RabbitMQ `Consumer` | Invokes handler on message receipt; acks/nacks correctly; handles errors (mocked channel) |
| `RetryTests.cs` | `Retry` | Retries on failure; respects max retries; backs off |
| `SslConfigurationBuilderTests.cs` | `SslConfigurationBuilder` | Builds SSL options from config |
| `TelemetryTests.cs` | `ServiceConnectActivitySource` | Creates activities with correct names and tags |
| `MongoDbProcessManagerFinderTests.cs` | `MongoDbProcessManagerFinder` | Unit-level tests with mocked `IMongoCollection` for edge cases |

### Existing Tests (Unchanged)
- `BusTests.cs` — 14 tests
- `InMemoryProcessManagerFinderTests.cs` — 7 tests
- `InMemoryAggregatorPersistorTest.cs` — 2 tests

### Filter Tests Modernization
Update `ServiceConnect.Filters.MessageDeduplication.Tests.csproj`:
- xunit: 2.2.0 -> 2.9.2
- Moq: 4.7.99 -> 4.20.72
- Microsoft.NET.Test.Sdk: 15.3.0 -> 17.14.0
- xunit.runner.visualstudio: add 2.8.2

## Estimated Test Count
- Existing unit tests: 23
- New unit tests: ~60-80
- E2E tests: ~30-40
- Filter tests (existing): 7
- **Total: ~120-150 tests**

## Technology Choices
- **Test framework:** xUnit 2.9.2 (consistent with existing)
- **Mocking:** Moq 4.20.72 (consistent with existing)
- **Containers:** Testcontainers for .NET (official library)
- **Container images:** `rabbitmq:3-management`, `mongo:7`
- **Assertions:** xUnit built-in `Assert` (consistent with existing)
- **Target framework:** `net10.0`
