# ServiceConnect Clean Architecture Refactor — Design Spec

## Overview

A major refactor of ServiceConnect-CSharp applying CLEAN code principles: reducing duplication, breaking apart god objects, modernizing interfaces, consolidating projects, and upgrading to modern .NET patterns. This is a breaking change (v2).

**Target:** net8.0 + net10.0 (multi-target)

---

## 1. Solution Structure & Project Consolidation

**17 projects → 8 projects**

| Proposed Project | Description | Source |
|---|---|---|
| `ServiceConnect.Interfaces` | Redesigned core interfaces | Redesigned from current |
| `ServiceConnect` | Bus orchestrator + DI extensions | Merged: Core + ServiceConnect + Container.ServiceCollection |
| `ServiceConnect.Client.RabbitMQ` | RabbitMQ transport | Refactored from current |
| `ServiceConnect.Persistence.MongoDb` | MongoDB persistence (SSL as config option) | Merged: MongoDb + MongoDbSsl |
| `ServiceConnect.Persistence.InMemory` | Lightweight in-memory persistence | Kept from current |
| `ServiceConnect.Telemetry` | OpenTelemetry instrumentation | Kept from current |
| `ServiceConnect.UnitTests` | Unit tests | Updated from current |
| `ServiceConnect.IntegrationTests` | Integration tests (SSL via test traits) | Merged: IntegrationTests + IntegrationTestsSsl |

### Removed Projects

| Project | Reason |
|---|---|
| `ServiceConnect.Container.Default` | Microsoft DI is the only container |
| `ServiceConnect.Container.StructureMap` | Obsolete DI framework |
| `ServiceConnect.Container.Ninject` | Obsolete DI framework |
| `ServiceConnect.Persistance.MongoDbSsl` | Merged into MongoDb |
| `ServiceConnect.Persistance.SqlServer` | Dropped |
| `ServiceConnect.IntegrationTestsSsl` | Merged into IntegrationTests |

### Naming Fix

All `Persistance` references corrected to `Persistence`.

---

## 2. Interface Redesign

### IConfiguration → Split Into Focused Interfaces

The current `IConfiguration` (45+ members) violates the Interface Segregation Principle. It is split into:

**`ITransportConfiguration`** — Transport/connection concerns only:
- Host, Username, Password, VirtualHost
- SSL settings (enabled, cert path, passphrase, validation callbacks)
- Heartbeat, PrefetchCount, RetryDelay, MaxRetries

**`IQueueConfiguration`** — Queue naming and routing:
- QueueName, ErrorQueueName, AuditQueueName
- QueueMappings (message type to endpoint)
- ExchangeName, RoutingKeys

**`IPersistenceConfiguration`** — Persistence store settings:
- ConnectionString, DatabaseName
- AggregatorCollectionName, ProcessManagerCollectionName
- TimeoutCollectionName

**`IPipelineConfiguration`** — Filters and middleware:
- OutgoingFilters, BeforeConsumingFilters, AfterConsumingFilters
- MessageProcessingMiddleware, SendMessageMiddleware

**`IBusConfiguration`** — Top-level bus behavior:
- ScanForMessageHandlers, AutoStartConsuming
- References the above sub-configurations
- No factory methods (DI handles instantiation)

### IBus → Simplified Surface

Current: 11+ overloads of Send/Publish/SendRequest.
Proposed: 3 methods with option objects.

```csharp
public interface IBus : IDisposable
{
    Task PublishAsync<T>(T message, PublishOptions? options = null) where T : Message;
    Task SendAsync<T>(T message, SendOptions? options = null) where T : Message;
    Task<TReply> SendRequestAsync<T, TReply>(T message, RequestOptions? options = null)
        where T : Message where TReply : Message;

    void StartConsuming();
    void StopConsuming();

    bool IsConnected { get; }
}
```

**Option classes:**

- `PublishOptions` — Headers, RoutingKey
- `SendOptions` — Headers, EndPoint, EndPoints (list)
- `RequestOptions` — Headers, EndPoint, Timeout, ExpectedReplyCount

### IConsumer / IProducer → Async-First

```csharp
public interface IConsumer : IAsyncDisposable
{
    Task StartConsumingAsync(ConsumerOptions options, ConsumerEventHandler handler);
    bool IsConnected { get; }
}

public interface IProducer : IAsyncDisposable
{
    Task PublishAsync(string routingKey, byte[] message, IDictionary<string, string>? headers = null);
    Task SendAsync(string endPoint, byte[] message, IDictionary<string, string>? headers = null);
    int MaximumMessageSize { get; }
}
```

### IBusContainer → Removed

No container abstraction. Handler discovery and registration happens via `IServiceCollection` extension methods at startup.

---

## 3. Bus Architecture — From God Object to Orchestrator

The current Bus.cs (896 lines) is refactored into a thin orchestrator (~150 lines) that delegates to focused services.

### Extracted Services

**`IMessageSerializer`** — Serialization/deserialization:
- Currently: `JsonConvert.SerializeObject` + `Encoding.UTF8.GetBytes` repeated 7+ times inline
- Default implementation uses Newtonsoft.Json
- Users can swap via DI (e.g., System.Text.Json)

**`IFilterPipeline`** — Executes filter chains:
- Currently: 15-line filter processing block copy-pasted 7+ times
- `ExecuteOutgoingAsync(envelope)` / `ExecuteIncomingAsync(envelope)`
- Single implementation, called once per send/publish/consume path

**`IRequestReplyManager`** — Request/reply correlation:
- Currently: Mixed into Bus.cs with lock management and dictionary tracking
- Owns `TaskCompletionSource` dictionary, timeout handling, correlation ID generation
- Uses `ConcurrentDictionary` internally (no lock contention)

**`IStreamManager`** — Large message streaming:
- Currently: Inline in Bus.cs
- Dedicated service for stream-based send/receive

### New Bus.cs Structure

```
Bus (target ~150 lines)
+-- constructor(IProducer, IConsumer, IMessageSerializer,
|              IFilterPipeline, IRequestReplyManager,
|              IPipelineConfiguration, ILogger<Bus>)
+-- PublishAsync()      -> serialize -> filter -> producer.PublishAsync()
+-- SendAsync()         -> serialize -> filter -> producer.SendAsync()
+-- SendRequestAsync()  -> serialize -> filter -> requestReplyManager.SendAndWaitAsync()
+-- StartConsuming()    -> consumer.StartConsumingAsync()
+-- StopConsuming()     -> consumer dispose
```

Each public method follows the same 3-step pattern (serialize, filter, delegate) with no duplication. The Bus owns no state beyond its dependencies.

### Logging Replacement

- **Drop:** Common.Logging (abandoned since 2016)
- **Replace with:** `Microsoft.Extensions.Logging.ILogger<T>`
- Standard in modern .NET, no adapter needed with Microsoft DI

---

## 4. MongoDB Persistence Merge

Two near-identical projects (~400 lines of duplication) become one project with SSL as a configuration option.

### Connection Configuration

```csharp
public class MongoDbPersistenceOptions
{
    public string ConnectionString { get; set; }
    public string DatabaseName { get; set; }

    // SSL options -- null means no SSL
    public MongoDbSslOptions? Ssl { get; set; }
}

public class MongoDbSslOptions
{
    public string CertPath { get; set; }
    public string CertPassphrase { get; set; }
    public SslProtocols SslProtocol { get; set; } = SslProtocols.Tls12;
    public bool AllowInsecureTls { get; set; } = false;
}
```

### Implementation

- **`MongoClientFactory`** — Single factory that builds a `MongoClient` with or without SSL based on whether `SslOptions` is provided. Eliminates the duplicated connection string parsing (~110 lines duplicated).
- **`MongoDbAggregatorPersistor`** — One implementation (current non-SSL and SSL versions are identical once connected).
- **`MongoDbProcessManagerFinder`** — One implementation. The SSL version's explicit locking mechanism for `GetTimeoutsBatch` is carried forward as the correct implementation.

### Data Model

Unified `MongoDbData<T>` with both `Id` (GUID) and `Locked` properties. The current inconsistency (SSL version has `Locked` but commented-out `Id`) is resolved.

### Fix Empty Catch Blocks

Replace `catch { return null; }` with proper error logging wrapped in `PersistenceException`.

---

## 5. DI Registration & Startup

### Builder Pattern

```csharp
services.AddServiceConnect(builder =>
{
    builder.UseRabbitMQ(transport =>
    {
        transport.Host = "localhost";
        transport.Username = "guest";
        transport.Password = "guest";
        transport.PrefetchCount = 10;
    });

    builder.UseMongoDbPersistence(persistence =>
    {
        persistence.ConnectionString = "mongodb://localhost";
        persistence.DatabaseName = "ServiceConnect";
        // Optional SSL:
        // persistence.Ssl = new MongoDbSslOptions { CertPath = "..." };
    });

    builder.UseNewtonsoftJsonSerializer(); // default, explicit for clarity

    builder.AddOutgoingFilter<MyCustomFilter>();
    builder.AddBeforeConsumingFilter<AuditFilter>();

    builder.ConfigureQueues(queues =>
    {
        queues.QueueName = "my-service";
        queues.ErrorQueueName = "my-service.errors";
    });
});
```

### What `AddServiceConnect` Does

- Scans assemblies for `IMessageHandler<T>` implementations and registers them in DI
- Registers `IBus` as singleton
- Registers extracted services (`IMessageSerializer`, `IFilterPipeline`, `IRequestReplyManager`, etc.)
- Registers `IConsumer` and `IProducer` based on chosen transport
- No `Activator.CreateInstance`, no reflection for DI

### Handler Interface

```csharp
public interface IMessageHandler<in T> where T : Message
{
    Task HandleAsync(T message, IConsumeContext context);
}
```

Assembly scanning during `AddServiceConnect` discovers and registers implementations automatically.

---

## 6. Error Handling & Resilience

### Custom Exception Hierarchy

```csharp
public class ServiceConnectException : Exception { ... }
public class TransportException : ServiceConnectException { ... }
public class SerializationException : ServiceConnectException { ... }
public class PersistenceException : ServiceConnectException { ... }
public class RequestTimeoutException : ServiceConnectException { ... }
```

### Rules

- **No empty catch blocks.** Every catch either logs + rethrows, or wraps in a typed exception with context (message ID, handler type, endpoint).
- **Persistence errors** — Wrap MongoDB/driver exceptions in `PersistenceException` with connection details.
- **Transport errors** — Wrap RabbitMQ exceptions in `TransportException`. Retry logic extracted into a focused `RetryPolicy` class.
- **Serialization errors** — Wrap in `SerializationException` with the message type that failed.
- **Request timeouts** — `RequestTimeoutException` with correlation ID and elapsed time.

### Consumer Error Pipeline

```
Message received
-> Deserialize (catch -> SerializationException -> error queue)
-> Filters (catch -> log + rethrow)
-> Handler (catch -> retry policy -> error queue after max retries)
```

Each step has one clear failure path. Existing error queue mechanism is preserved but with structured exception information.

---

## 7. Testing Strategy

### Extracted Services Get Their Own Tests

Each service extracted from Bus.cs gets dedicated test coverage. These are currently untestable because they are inline.

### Bus Tests Become Simple

Bus is just an orchestrator — tests verify it calls the right services in the right order. Mocking is straightforward since each dependency has a focused interface.

### Test Structure

```
ServiceConnect.UnitTests/
+-- Bus/
|   +-- BusPublishTests.cs
|   +-- BusSendTests.cs
|   +-- BusRequestReplyTests.cs
+-- Pipeline/
|   +-- FilterPipelineTests.cs
|   +-- MessageSerializerTests.cs
|   +-- RequestReplyManagerTests.cs
+-- Configuration/
|   +-- ServiceConnectBuilderTests.cs
+-- Persistence/
    +-- MongoDb/
        +-- AggregatorPersistorTests.cs
        +-- ProcessManagerFinderTests.cs
```

### MongoDB Persistence Tests

Merged project gets unit tests with mocked `IMongoCollection<T>`. Connection string parsing and SSL configuration get dedicated tests (currently zero coverage).

### Integration Tests

- `IntegrationTestsSsl` merged into `IntegrationTests` with test categories/traits to distinguish SSL vs non-SSL runs
- Existing integration tests updated for new API surface

### Removed Tests

Tests for dropped projects (StructureMap, Ninject, Default container, SqlServer) are removed.

---

## 8. Supporting Types

### IConsumeContext

Provides message metadata to handlers during consumption:

```csharp
public interface IConsumeContext
{
    IDictionary<string, string> Headers { get; }
    string MessageId { get; }
    string CorrelationId { get; }
    IBus Bus { get; }
}
```

Replaces the current pattern of passing raw headers and bus references separately into handlers.

---

## 9. Out of Scope

- **`samples/` directory** — Samples will be updated after the core refactor is complete, as a follow-up task. They are not part of this spec.
- **`Tools/` project** — Kept as-is. Not affected by this refactor.
- **NuGet package publishing** — Package naming and versioning decisions are deferred.
- **Performance optimization** — This refactor focuses on architecture and maintainability. Performance work (e.g., object pooling, zero-copy serialization) is a separate effort.
