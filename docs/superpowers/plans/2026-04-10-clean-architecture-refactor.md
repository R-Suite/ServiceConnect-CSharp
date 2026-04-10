# ServiceConnect Clean Architecture Refactor — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor ServiceConnect-CSharp from 17 projects to 8, applying CLEAN code principles: decompose god objects, eliminate duplication, modernize interfaces, and standardize on Microsoft DI.

**Architecture:** The Bus becomes a thin orchestrator delegating to focused services (IMessageSerializer, IFilterPipeline, IRequestReplyManager). IConfiguration is split into focused sub-interfaces. MongoDB persistence projects are merged with SSL as a config option. Microsoft.Extensions.DependencyInjection is the sole container.

**Tech Stack:** .NET 8.0 + 10.0 (multi-target), Newtonsoft.Json (default serializer), RabbitMQ.Client, MongoDB.Driver, Microsoft.Extensions.Logging, Microsoft.Extensions.DependencyInjection, xUnit + Moq

---

## File Structure

### ServiceConnect.Interfaces (redesigned)

```
src/ServiceConnect.Interfaces/
├── ServiceConnect.Interfaces.csproj
├── IBus.cs                          -- Simplified: 3 async methods + options
├── IMessageHandler.cs               -- Combined sync+async into async-only
├── IConsumeContext.cs                -- Enriched with MessageId, CorrelationId
├── IConsumer.cs                      -- Async-first
├── IProducer.cs                      -- Async-first
├── IFilter.cs                        -- Unchanged interface
├── IMessageSerializer.cs             -- NEW: extracted from Bus inline code
├── IFilterPipeline.cs                -- NEW: extracted from Bus inline code
├── IRequestReplyManager.cs           -- NEW: extracted from Bus inline code
├── IStreamManager.cs                 -- NEW: extracted from Bus inline code
├── IAggregatorPersistor.cs           -- Kept
├── IAggregatorProcessor.cs           -- Kept
├── IProcessManagerFinder.cs          -- Kept
├── IProcessManagerProcessor.cs       -- Kept
├── IProcessManagerPropertyMapper.cs  -- Kept
├── IProcessManagerData.cs            -- Kept
├── IPersistenceData.cs               -- Renamed from IPersistanceData
├── IStartProcessManager.cs           -- Kept
├── IStartAsyncProcessManager.cs      -- Kept
├── IStreamHandler.cs                 -- Kept
├── IStreamProcessor.cs               -- Kept
├── IMessageBusReadStream.cs          -- Kept
├── IMessageBusWriteStream.cs         -- Kept
├── IProcessMessageMiddleware.cs      -- Kept
├── ISendMessageMiddleware.cs         -- Kept
├── IProcessMessagePipeline.cs        -- Kept
├── ISendMessagePipeline.cs           -- Kept
├── IRequestConfiguration.cs          -- Kept
├── IBusState.cs                      -- Kept
├── Configuration/
│   ├── IBusConfiguration.cs          -- NEW: top-level bus behavior
│   ├── ITransportConfiguration.cs    -- NEW: transport/connection settings
│   ├── IQueueConfiguration.cs        -- NEW: queue naming and routing
│   ├── IPersistenceConfiguration.cs  -- NEW: persistence store settings
│   └── IPipelineConfiguration.cs     -- NEW: filters and middleware
├── Options/
│   ├── PublishOptions.cs             -- NEW: headers, routing key
│   ├── SendOptions.cs                -- NEW: headers, endpoint(s)
│   └── RequestOptions.cs             -- NEW: headers, endpoint, timeout, count
├── Exceptions/
│   ├── ServiceConnectException.cs    -- NEW: base exception
│   ├── TransportException.cs         -- NEW
│   ├── SerializationException.cs     -- NEW
│   ├── PersistenceException.cs       -- NEW
│   └── RequestTimeoutException.cs    -- NEW
├── Message.cs                        -- Kept
├── Envelope.cs                       -- Kept
├── Aggregator.cs                     -- Kept
├── ProcessManager.cs                 -- Moved from Core (it's a base class)
├── HandlerReference.cs               -- Kept
├── HeaderKeys.cs                     -- Kept
├── ConsumerEventHandler.cs           -- Kept
├── ConsumeEventArgs.cs               -- Kept
├── ConsumeEventResult.cs             -- Kept
├── OutgoingEventArgs.cs              -- Kept
├── PublishEventArgs.cs               -- Kept
├── SendEventArgs.cs                  -- Kept
├── TimeoutData.cs                    -- Kept
├── TimeoutMessage.cs                 -- Kept
└── TimeoutsBatch.cs                  -- Kept
```

### ServiceConnect (merged Core + Container.ServiceCollection)

```
src/ServiceConnect/
├── ServiceConnect.csproj
├── Bus.cs                            -- Thin orchestrator (~150 lines)
├── ServiceConnectBuilder.cs          -- NEW: fluent builder for DI registration
├── ServiceCollectionExtensions.cs    -- NEW: AddServiceConnect() extension
├── Services/
│   ├── NewtonsoftJsonMessageSerializer.cs  -- NEW: default IMessageSerializer
│   ├── FilterPipeline.cs                   -- NEW: IFilterPipeline impl
│   ├── RequestReplyManager.cs              -- NEW: IRequestReplyManager impl
│   └── StreamManager.cs                    -- NEW: IStreamManager impl
├── Pipeline/
│   ├── ProcessMessagePipeline.cs     -- From Core, updated
│   ├── SendMessagePipeline.cs        -- From Core, updated
│   └── RequestConfiguration.cs       -- From Core
├── Handlers/
│   ├── MessageHandlerProcessor.cs    -- From Core
│   ├── ProcessManagerProcessor.cs    -- From Core
│   ├── ProcessManagerPropertyMapper.cs -- From Core
│   ├── AggregatorProcessor.cs        -- From Core
│   ├── StreamProcessor.cs            -- From Core
│   └── ExpiredTimeoutsPoller.cs      -- From Core
├── State/
│   ├── BusState.cs                   -- From Core
│   └── ConsumeContext.cs             -- From Core
├── Configuration/
│   ├── BusConfiguration.cs           -- NEW: IBusConfiguration impl
│   ├── TransportConfiguration.cs     -- NEW: ITransportConfiguration impl
│   ├── QueueConfiguration.cs         -- NEW: IQueueConfiguration impl
│   ├── PersistenceConfiguration.cs   -- NEW: IPersistenceConfiguration impl
│   └── PipelineConfiguration.cs      -- NEW: IPipelineConfiguration impl
├── Streams/
│   ├── MessageBusReadStream.cs       -- From Core
│   └── MessageBusWriteStream.cs      -- From Core
├── Internal/
│   ├── HeartbeatMessage.cs           -- From Core
│   ├── HeartbeatTimerState.cs        -- From Core
│   ├── StreamResponseMessage.cs      -- From Core
│   └── RoutingKey.cs                 -- From Core
├── ServiceConnectActivitySource.cs   -- Kept
└── MessagingAttributes.cs            -- Kept
```

### ServiceConnect.Persistence.MongoDb (merged)

```
src/ServiceConnect.Persistence.MongoDb/
├── ServiceConnect.Persistence.MongoDb.csproj
├── MongoDbPersistenceOptions.cs      -- NEW: connection + SSL config
├── MongoDbSslOptions.cs              -- NEW: SSL-specific settings
├── MongoClientFactory.cs             -- NEW: builds MongoClient with/without SSL
├── MongoDbData.cs                    -- Unified data model (Id + Locked)
├── MongoDbAggregatorPersistor.cs     -- Merged implementation
├── MongoDbProcessManagerFinder.cs    -- Merged implementation (with locking)
└── MongoDbPersistenceExtensions.cs   -- NEW: builder.UseMongoDbPersistence()
```

### Kept As-Is (minor updates)

- `src/ServiceConnect.Client.RabbitMQ/` — Update to use new interfaces
- `src/ServiceConnect.Persistence.InMemory/` — Rename from Persistance, update interfaces
- `src/ServiceConnect.Telemetry/` — Update references

### Removed Entirely

- `src/ServiceConnect.Core/` — Merged into ServiceConnect
- `src/ServiceConnect.Container.Default/`
- `src/ServiceConnect.Container.StructureMap/`
- `src/ServiceConnect.Container.Ninject/`
- `src/ServiceConnect.Container.ServiceCollection/` — Merged into ServiceConnect
- `src/ServiceConnect.Persistance.MongoDb/` — Replaced by Persistence.MongoDb
- `src/ServiceConnect.Persistance.MongoDbSsl/`
- `src/ServiceConnect.Persistance.SqlServer/`
- `src/ServiceConnect.IntegrationTestsSsl/` — Merged into IntegrationTests

---

## Phase 1: Foundation — New Interfaces & Exceptions

### Task 1: Create the new ServiceConnect.Interfaces project structure

**Files:**
- Modify: `src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj`
- Create: `src/ServiceConnect.Interfaces/Configuration/IBusConfiguration.cs`
- Create: `src/ServiceConnect.Interfaces/Configuration/ITransportConfiguration.cs`
- Create: `src/ServiceConnect.Interfaces/Configuration/IQueueConfiguration.cs`
- Create: `src/ServiceConnect.Interfaces/Configuration/IPersistenceConfiguration.cs`
- Create: `src/ServiceConnect.Interfaces/Configuration/IPipelineConfiguration.cs`
- Create: `src/ServiceConnect.Interfaces/Options/PublishOptions.cs`
- Create: `src/ServiceConnect.Interfaces/Options/SendOptions.cs`
- Create: `src/ServiceConnect.Interfaces/Options/RequestOptions.cs`
- Create: `src/ServiceConnect.Interfaces/Exceptions/ServiceConnectException.cs`
- Create: `src/ServiceConnect.Interfaces/Exceptions/TransportException.cs`
- Create: `src/ServiceConnect.Interfaces/Exceptions/SerializationException.cs`
- Create: `src/ServiceConnect.Interfaces/Exceptions/PersistenceException.cs`
- Create: `src/ServiceConnect.Interfaces/Exceptions/RequestTimeoutException.cs`
- Create: `src/ServiceConnect.Interfaces/IMessageSerializer.cs`
- Create: `src/ServiceConnect.Interfaces/IFilterPipeline.cs`
- Create: `src/ServiceConnect.Interfaces/IRequestReplyManager.cs`
- Create: `src/ServiceConnect.Interfaces/IStreamManager.cs`
- Test: `src/ServiceConnect.UnitTests/Configuration/ConfigurationInterfaceTests.cs`

- [ ] **Step 1: Update the .csproj to multi-target net8.0;net10.0**

Edit `src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj` to set:

```xml
<PropertyGroup>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
</PropertyGroup>
```

Remove any old target framework entries and the Common.Logging package reference if present.

- [ ] **Step 2: Create the exception hierarchy**

Create `src/ServiceConnect.Interfaces/Exceptions/ServiceConnectException.cs`:

```csharp
namespace ServiceConnect.Interfaces.Exceptions;

public class ServiceConnectException : Exception
{
    public ServiceConnectException() { }
    public ServiceConnectException(string message) : base(message) { }
    public ServiceConnectException(string message, Exception innerException) : base(message, innerException) { }
}
```

Create `src/ServiceConnect.Interfaces/Exceptions/TransportException.cs`:

```csharp
namespace ServiceConnect.Interfaces.Exceptions;

public class TransportException : ServiceConnectException
{
    public string? Endpoint { get; }

    public TransportException(string message, string? endpoint = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Endpoint = endpoint;
    }
}
```

Create `src/ServiceConnect.Interfaces/Exceptions/SerializationException.cs`:

```csharp
namespace ServiceConnect.Interfaces.Exceptions;

public class SerializationException : ServiceConnectException
{
    public Type? MessageType { get; }

    public SerializationException(string message, Type? messageType = null, Exception? innerException = null)
        : base(message, innerException)
    {
        MessageType = messageType;
    }
}
```

Create `src/ServiceConnect.Interfaces/Exceptions/PersistenceException.cs`:

```csharp
namespace ServiceConnect.Interfaces.Exceptions;

public class PersistenceException : ServiceConnectException
{
    public PersistenceException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}
```

Create `src/ServiceConnect.Interfaces/Exceptions/RequestTimeoutException.cs`:

```csharp
namespace ServiceConnect.Interfaces.Exceptions;

public class RequestTimeoutException : ServiceConnectException
{
    public Guid CorrelationId { get; }
    public TimeSpan Elapsed { get; }

    public RequestTimeoutException(Guid correlationId, TimeSpan elapsed)
        : base($"Request {correlationId} timed out after {elapsed.TotalMilliseconds}ms")
    {
        CorrelationId = correlationId;
        Elapsed = elapsed;
    }
}
```

- [ ] **Step 3: Create the split configuration interfaces**

Create `src/ServiceConnect.Interfaces/Configuration/ITransportConfiguration.cs`:

```csharp
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace ServiceConnect.Interfaces.Configuration;

public interface ITransportConfiguration
{
    string Host { get; set; }
    string? Username { get; set; }
    string? Password { get; set; }
    string? VirtualHost { get; set; }
    int RetryDelay { get; set; }
    int MaxRetries { get; set; }
    ushort PrefetchCount { get; set; }
    bool SslEnabled { get; set; }
    SslPolicyErrors AcceptablePolicyErrors { get; set; }
    string? ServerName { get; set; }
    string? CertPath { get; set; }
    string? CertPassphrase { get; set; }
    X509CertificateCollection? Certs { get; set; }
    SslProtocols SslProtocol { get; set; }
    LocalCertificateSelectionCallback? CertificateSelectionCallback { get; set; }
    RemoteCertificateValidationCallback? CertificateValidationCallback { get; set; }
    IDictionary<string, object> ClientSettings { get; set; }
}
```

Create `src/ServiceConnect.Interfaces/Configuration/IQueueConfiguration.cs`:

```csharp
namespace ServiceConnect.Interfaces.Configuration;

public interface IQueueConfiguration
{
    string QueueName { get; set; }
    string ErrorQueueName { get; set; }
    string AuditQueueName { get; set; }
    string HeartbeatQueueName { get; set; }
    bool AuditingEnabled { get; set; }
    bool DisableErrors { get; set; }
    bool PurgeQueueOnStartup { get; set; }
    IDictionary<string, IList<string>> QueueMappings { get; set; }
    void AddQueueMapping(Type messageType, string queue);
    void AddQueueMapping(Type messageType, IList<string> queues);
}
```

Create `src/ServiceConnect.Interfaces/Configuration/IPersistenceConfiguration.cs`:

```csharp
namespace ServiceConnect.Interfaces.Configuration;

public interface IPersistenceConfiguration
{
    string ConnectionString { get; set; }
    string DatabaseName { get; set; }
    string AggregatorCollectionName { get; set; }
}
```

Create `src/ServiceConnect.Interfaces/Configuration/IPipelineConfiguration.cs`:

```csharp
namespace ServiceConnect.Interfaces.Configuration;

public interface IPipelineConfiguration
{
    IList<Type> BeforeConsumingFilters { get; }
    IList<Type> AfterConsumingFilters { get; }
    IList<Type> OutgoingFilters { get; }
    IList<Type> MessageProcessingMiddleware { get; }
    IList<Type> SendMessageMiddleware { get; }
}
```

Create `src/ServiceConnect.Interfaces/Configuration/IBusConfiguration.cs`:

```csharp
namespace ServiceConnect.Interfaces.Configuration;

public interface IBusConfiguration
{
    bool ScanForMessageHandlers { get; set; }
    bool AutoStartConsuming { get; set; }
    bool EnableProcessManagerTimeouts { get; set; }
    int Clients { get; set; }
    Action<Exception>? ExceptionHandler { get; set; }
    ITransportConfiguration Transport { get; }
    IQueueConfiguration Queues { get; }
    IPersistenceConfiguration Persistence { get; }
    IPipelineConfiguration Pipeline { get; }
}
```

- [ ] **Step 4: Create the option classes**

Create `src/ServiceConnect.Interfaces/Options/PublishOptions.cs`:

```csharp
namespace ServiceConnect.Interfaces;

public class PublishOptions
{
    public Dictionary<string, string>? Headers { get; set; }
    public string? RoutingKey { get; set; }
}
```

Create `src/ServiceConnect.Interfaces/Options/SendOptions.cs`:

```csharp
namespace ServiceConnect.Interfaces;

public class SendOptions
{
    public Dictionary<string, string>? Headers { get; set; }
    public string? EndPoint { get; set; }
    public IList<string>? EndPoints { get; set; }
}
```

Create `src/ServiceConnect.Interfaces/Options/RequestOptions.cs`:

```csharp
namespace ServiceConnect.Interfaces;

public class RequestOptions
{
    public Dictionary<string, string>? Headers { get; set; }
    public string? EndPoint { get; set; }
    public IList<string>? EndPoints { get; set; }
    public int Timeout { get; set; } = 10000;
    public int? ExpectedReplyCount { get; set; }
}
```

- [ ] **Step 5: Create the new extracted service interfaces**

Create `src/ServiceConnect.Interfaces/IMessageSerializer.cs`:

```csharp
namespace ServiceConnect.Interfaces;

public interface IMessageSerializer
{
    byte[] Serialize<T>(T message) where T : Message;
    T Deserialize<T>(byte[] data) where T : Message;
    object Deserialize(byte[] data, Type type);
}
```

Create `src/ServiceConnect.Interfaces/IFilterPipeline.cs`:

```csharp
namespace ServiceConnect.Interfaces;

public interface IFilterPipeline
{
    bool ExecuteOutgoingFilters(Envelope envelope);
    bool ExecuteBeforeConsumingFilters(Envelope envelope);
    bool ExecuteAfterConsumingFilters(Envelope envelope);
}
```

Create `src/ServiceConnect.Interfaces/IRequestReplyManager.cs`:

```csharp
namespace ServiceConnect.Interfaces;

public interface IRequestReplyManager
{
    Task<TReply> SendRequestAsync<TRequest, TReply>(
        byte[] messageBytes,
        Dictionary<string, string> headers,
        Action<Type, byte[], Dictionary<string, string>, string?> sendAction,
        RequestOptions options)
        where TRequest : Message
        where TReply : Message;

    Task<IList<TReply>> SendRequestMultiAsync<TRequest, TReply>(
        byte[] messageBytes,
        Dictionary<string, string> headers,
        Action<Type, byte[], Dictionary<string, string>, string?> sendAction,
        RequestOptions options)
        where TRequest : Message
        where TReply : Message;

    void ProcessReply(string messageId, string messageJson, Type type);
}
```

Create `src/ServiceConnect.Interfaces/IStreamManager.cs`:

```csharp
namespace ServiceConnect.Interfaces;

public interface IStreamManager
{
    IMessageBusWriteStream CreateStream<T>(string endpoint, T message) where T : Message;
    Task ProcessStream(byte[] message, Type type, IDictionary<string, object> headers);
}
```

- [ ] **Step 6: Redesign IBus interface**

Replace the content of `src/ServiceConnect.Interfaces/IBus.cs` with:

```csharp
namespace ServiceConnect.Interfaces;

public interface IBus : IDisposable
{
    Task PublishAsync<T>(T message, PublishOptions? options = null) where T : Message;
    Task SendAsync<T>(T message, SendOptions? options = null) where T : Message;
    Task<TReply> SendRequestAsync<T, TReply>(T message, RequestOptions? options = null)
        where T : Message where TReply : Message;
    Task<IList<TReply>> SendRequestMultiAsync<T, TReply>(T message, RequestOptions? options = null)
        where T : Message where TReply : Message;

    Task PublishRequestAsync<TRequest, TReply>(TRequest message, Action<TReply> onReply, RequestOptions? options = null)
        where TRequest : Message where TReply : Message;

    void Route<T>(T message, IList<string> destinations) where T : Message;

    IMessageBusWriteStream CreateStream<T>(string endpoint, T message) where T : Message;

    void StartConsuming();
    void StopConsuming();

    bool IsConnected { get; }
}
```

- [ ] **Step 7: Update IMessageHandler to async-only**

Replace `src/ServiceConnect.Interfaces/IMessageHandler.cs` with:

```csharp
namespace ServiceConnect.Interfaces;

public interface IMessageHandler<in TMessage> where TMessage : Message
{
    IConsumeContext? Context { get; set; }
    Task HandleAsync(TMessage message);
}
```

Keep `IAsyncMessageHandler` temporarily for backward compat — it will be removed once all handlers migrate.

- [ ] **Step 8: Update IConsumeContext**

Replace `src/ServiceConnect.Interfaces/IConsumeContext.cs` with:

```csharp
namespace ServiceConnect.Interfaces;

public interface IConsumeContext
{
    IBus Bus { get; set; }
    IDictionary<string, object> Headers { get; set; }
    string? MessageId { get; }
    Guid CorrelationId { get; }
    void Reply<TReply>(TReply message, Dictionary<string, string>? headers = null) where TReply : Message;
}
```

- [ ] **Step 9: Update IConsumer and IProducer to async-first**

Replace `src/ServiceConnect.Interfaces/IConsumer.cs` with:

```csharp
namespace ServiceConnect.Interfaces;

public interface IConsumer : IAsyncDisposable, IDisposable
{
    bool IsConnected { get; }
    Task StartConsumingAsync(string queueName, IList<string> messageTypes, ConsumerEventHandler eventHandler);
}
```

Replace `src/ServiceConnect.Interfaces/IProducer.cs` with:

```csharp
namespace ServiceConnect.Interfaces;

public interface IProducer : IAsyncDisposable, IDisposable
{
    Task PublishAsync(Type type, byte[] message, Dictionary<string, string>? headers = null);
    Task SendAsync(Type type, byte[] message, Dictionary<string, string>? headers = null);
    Task SendAsync(string endPoint, Type type, byte[] message, Dictionary<string, string>? headers = null);
    Task SendBytesAsync(string endPoint, byte[] packet, Dictionary<string, string>? headers = null);
    long MaximumMessageSize { get; }
    void Disconnect();
}
```

- [ ] **Step 10: Rename IPersistanceData to IPersistenceData**

Rename `src/ServiceConnect.Interfaces/IPersistanceData.cs` to `src/ServiceConnect.Interfaces/IPersistenceData.cs`. Update the interface name and all references:

```csharp
namespace ServiceConnect.Interfaces;

public interface IPersistenceData<T> where T : class, IProcessManagerData
{
    T Data { get; set; }
}
```

- [ ] **Step 11: Remove IBusContainer and ILogger**

Delete `src/ServiceConnect.Interfaces/IBusContainer.cs` — container abstraction is gone.

Delete `src/ServiceConnect.Interfaces/ILogger.cs` — replaced by `Microsoft.Extensions.Logging.ILogger`.

Delete `src/ServiceConnect.Interfaces/Container/` directory entirely.

- [ ] **Step 12: Remove old IConfiguration and ITransportSettings**

Delete `src/ServiceConnect.Interfaces/IConfiguration.cs` — replaced by split configuration interfaces.

Delete `src/ServiceConnect.Interfaces/ITransportSettings.cs` — absorbed into ITransportConfiguration.

- [ ] **Step 13: Verify the Interfaces project compiles**

Run: `dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj`

Expected: Build succeeds with no errors. Fix any compilation issues.

- [ ] **Step 14: Commit**

```bash
git add src/ServiceConnect.Interfaces/
git commit -m "feat: redesign ServiceConnect.Interfaces with split config, async-first, and exceptions"
```

---

## Phase 2: Core Services — Extracted from Bus

### Task 2: Create the ServiceConnect project with extracted services

**Files:**
- Modify: `src/ServiceConnect/ServiceConnect.csproj`
- Create: `src/ServiceConnect/Services/NewtonsoftJsonMessageSerializer.cs`
- Create: `src/ServiceConnect/Services/FilterPipeline.cs`
- Create: `src/ServiceConnect/Services/RequestReplyManager.cs`
- Create: `src/ServiceConnect/Services/StreamManager.cs`
- Create: `src/ServiceConnect/Configuration/BusConfiguration.cs`
- Create: `src/ServiceConnect/Configuration/TransportConfiguration.cs`
- Create: `src/ServiceConnect/Configuration/QueueConfiguration.cs`
- Create: `src/ServiceConnect/Configuration/PersistenceConfiguration.cs`
- Create: `src/ServiceConnect/Configuration/PipelineConfiguration.cs`
- Test: `src/ServiceConnect.UnitTests/Pipeline/MessageSerializerTests.cs`
- Test: `src/ServiceConnect.UnitTests/Pipeline/FilterPipelineTests.cs`
- Test: `src/ServiceConnect.UnitTests/Pipeline/RequestReplyManagerTests.cs`
- Test: `src/ServiceConnect.UnitTests/Configuration/ServiceConnectBuilderTests.cs`

- [ ] **Step 1: Update ServiceConnect.csproj**

Update `src/ServiceConnect/ServiceConnect.csproj` to multi-target and add new dependencies. Remove references to Container.Default, Persistance.SqlServer:

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
    </PropertyGroup>
    <ItemGroup>
        <ProjectReference Include="..\ServiceConnect.Interfaces\ServiceConnect.Interfaces.csproj" />
    </ItemGroup>
    <ItemGroup>
        <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
        <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="9.0.0" />
        <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="9.0.0" />
        <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.0.0" />
        <PackageReference Include="Microsoft.Extensions.DependencyModel" Version="9.0.0" />
    </ItemGroup>
</Project>
```

- [ ] **Step 2: Write failing test for MessageSerializer**

Create `src/ServiceConnect.UnitTests/Pipeline/MessageSerializerTests.cs`:

```csharp
using ServiceConnect.Services;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.Pipeline;

public class MessageSerializerTests
{
    private readonly NewtonsoftJsonMessageSerializer _serializer = new();

    [Fact]
    public void Serialize_ReturnsUtf8JsonBytes()
    {
        var message = new TestMessage(Guid.NewGuid()) { Value = "hello" };

        byte[] result = _serializer.Serialize(message);

        Assert.NotNull(result);
        Assert.NotEmpty(result);
        string json = System.Text.Encoding.UTF8.GetString(result);
        Assert.Contains("hello", json);
    }

    [Fact]
    public void Deserialize_Generic_RoundTrips()
    {
        var original = new TestMessage(Guid.NewGuid()) { Value = "test" };
        byte[] bytes = _serializer.Serialize(original);

        var deserialized = _serializer.Deserialize<TestMessage>(bytes);

        Assert.Equal(original.Value, deserialized.Value);
        Assert.Equal(original.CorrelationId, deserialized.CorrelationId);
    }

    [Fact]
    public void Deserialize_ByType_RoundTrips()
    {
        var original = new TestMessage(Guid.NewGuid()) { Value = "typed" };
        byte[] bytes = _serializer.Serialize(original);

        var deserialized = (TestMessage)_serializer.Deserialize(bytes, typeof(TestMessage));

        Assert.Equal(original.Value, deserialized.Value);
    }

    [Fact]
    public void Serialize_NullMessage_ThrowsSerializationException()
    {
        Assert.Throws<Interfaces.Exceptions.SerializationException>(() =>
            _serializer.Serialize<TestMessage>(null!));
    }

    public class TestMessage : Message
    {
        public TestMessage(Guid correlationId) : base(correlationId) { }
        public string Value { get; set; } = "";
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test src/ServiceConnect.UnitTests/ --filter "FullyQualifiedName~MessageSerializerTests" --no-restore`

Expected: FAIL — `NewtonsoftJsonMessageSerializer` does not exist yet.

- [ ] **Step 4: Implement NewtonsoftJsonMessageSerializer**

Create `src/ServiceConnect/Services/NewtonsoftJsonMessageSerializer.cs`:

```csharp
using System.Text;
using Newtonsoft.Json;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;

namespace ServiceConnect.Services;

public class NewtonsoftJsonMessageSerializer : IMessageSerializer
{
    private readonly JsonSerializerSettings _settings;

    public NewtonsoftJsonMessageSerializer(JsonSerializerSettings? settings = null)
    {
        _settings = settings ?? new JsonSerializerSettings();
    }

    public byte[] Serialize<T>(T message) where T : Message
    {
        if (message is null)
            throw new Interfaces.Exceptions.SerializationException(
                "Cannot serialize null message", typeof(T));

        try
        {
            string json = JsonConvert.SerializeObject(message, _settings);
            return Encoding.UTF8.GetBytes(json);
        }
        catch (JsonException ex)
        {
            throw new Interfaces.Exceptions.SerializationException(
                $"Failed to serialize message of type {typeof(T).Name}", typeof(T), ex);
        }
    }

    public T Deserialize<T>(byte[] data) where T : Message
    {
        return (T)Deserialize(data, typeof(T));
    }

    public object Deserialize(byte[] data, Type type)
    {
        try
        {
            string json = Encoding.UTF8.GetString(data);
            return JsonConvert.DeserializeObject(json, type, _settings)
                ?? throw new Interfaces.Exceptions.SerializationException(
                    $"Deserialization returned null for type {type.Name}", type);
        }
        catch (JsonException ex)
        {
            throw new Interfaces.Exceptions.SerializationException(
                $"Failed to deserialize message of type {type.Name}", type, ex);
        }
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test src/ServiceConnect.UnitTests/ --filter "FullyQualifiedName~MessageSerializerTests" --no-restore`

Expected: All 4 tests PASS.

- [ ] **Step 6: Write failing test for FilterPipeline**

Create `src/ServiceConnect.UnitTests/Pipeline/FilterPipelineTests.cs`:

```csharp
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Pipeline;

public class FilterPipelineTests
{
    [Fact]
    public void ExecuteOutgoingFilters_NoFilters_ReturnsFalse()
    {
        var pipelineConfig = new Mock<IPipelineConfiguration>();
        pipelineConfig.Setup(x => x.OutgoingFilters).Returns(new List<Type>());
        var serviceProvider = new Mock<IServiceProvider>();

        var pipeline = new FilterPipeline(pipelineConfig.Object, serviceProvider.Object);

        var envelope = new Envelope { Body = new byte[] { 1 }, Headers = new Dictionary<string, object>() };
        bool stopped = pipeline.ExecuteOutgoingFilters(envelope);

        Assert.False(stopped);
    }

    [Fact]
    public void ExecuteOutgoingFilters_FilterReturnsFalse_StopsProcessing()
    {
        var filter = new Mock<IFilter>();
        filter.Setup(f => f.Process(It.IsAny<Envelope>())).Returns(false);

        var pipelineConfig = new Mock<IPipelineConfiguration>();
        pipelineConfig.Setup(x => x.OutgoingFilters).Returns(new List<Type> { typeof(IFilter) });

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(IFilter))).Returns(filter.Object);

        var pipeline = new FilterPipeline(pipelineConfig.Object, serviceProvider.Object);

        var envelope = new Envelope { Body = new byte[] { 1 }, Headers = new Dictionary<string, object>() };
        bool stopped = pipeline.ExecuteOutgoingFilters(envelope);

        Assert.True(stopped);
    }

    [Fact]
    public void ExecuteOutgoingFilters_FilterReturnsTrue_ContinuesProcessing()
    {
        var filter = new Mock<IFilter>();
        filter.Setup(f => f.Process(It.IsAny<Envelope>())).Returns(true);

        var pipelineConfig = new Mock<IPipelineConfiguration>();
        pipelineConfig.Setup(x => x.OutgoingFilters).Returns(new List<Type> { typeof(IFilter) });

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(IFilter))).Returns(filter.Object);

        var pipeline = new FilterPipeline(pipelineConfig.Object, serviceProvider.Object);

        var envelope = new Envelope { Body = new byte[] { 1 }, Headers = new Dictionary<string, object>() };
        bool stopped = pipeline.ExecuteOutgoingFilters(envelope);

        Assert.False(stopped);
    }
}
```

- [ ] **Step 7: Run test to verify it fails**

Run: `dotnet test src/ServiceConnect.UnitTests/ --filter "FullyQualifiedName~FilterPipelineTests" --no-restore`

Expected: FAIL — `FilterPipeline` does not exist yet.

- [ ] **Step 8: Implement FilterPipeline**

Create `src/ServiceConnect/Services/FilterPipeline.cs`:

```csharp
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Services;

public class FilterPipeline : IFilterPipeline
{
    private readonly IPipelineConfiguration _config;
    private readonly IServiceProvider _serviceProvider;

    public FilterPipeline(IPipelineConfiguration config, IServiceProvider serviceProvider)
    {
        _config = config;
        _serviceProvider = serviceProvider;
    }

    public bool ExecuteOutgoingFilters(Envelope envelope)
    {
        return ExecuteFilters(_config.OutgoingFilters, envelope);
    }

    public bool ExecuteBeforeConsumingFilters(Envelope envelope)
    {
        return ExecuteFilters(_config.BeforeConsumingFilters, envelope);
    }

    public bool ExecuteAfterConsumingFilters(Envelope envelope)
    {
        return ExecuteFilters(_config.AfterConsumingFilters, envelope);
    }

    private bool ExecuteFilters(IList<Type> filterTypes, Envelope envelope)
    {
        if (filterTypes == null || filterTypes.Count == 0)
            return false;

        foreach (Type filterType in filterTypes)
        {
            var filter = (IFilter)(_serviceProvider.GetService(filterType)
                ?? throw new InvalidOperationException($"Filter of type {filterType.Name} not registered in DI container."));

            bool continueProcessing = filter.Process(envelope);
            if (!continueProcessing)
                return true; // stopped
        }

        return false; // not stopped
    }
}
```

- [ ] **Step 9: Run test to verify it passes**

Run: `dotnet test src/ServiceConnect.UnitTests/ --filter "FullyQualifiedName~FilterPipelineTests" --no-restore`

Expected: All 3 tests PASS.

- [ ] **Step 10: Implement configuration classes**

Create `src/ServiceConnect/Configuration/TransportConfiguration.cs`:

```csharp
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Configuration;

public class TransportConfiguration : ITransportConfiguration
{
    public string Host { get; set; } = "localhost";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? VirtualHost { get; set; }
    public int RetryDelay { get; set; } = 3000;
    public int MaxRetries { get; set; } = 3;
    public ushort PrefetchCount { get; set; } = 1;
    public bool SslEnabled { get; set; }
    public SslPolicyErrors AcceptablePolicyErrors { get; set; } = SslPolicyErrors.None;
    public string? ServerName { get; set; }
    public string? CertPath { get; set; }
    public string? CertPassphrase { get; set; }
    public X509CertificateCollection? Certs { get; set; }
    public SslProtocols SslProtocol { get; set; } = SslProtocols.Tls12;
    public LocalCertificateSelectionCallback? CertificateSelectionCallback { get; set; }
    public RemoteCertificateValidationCallback? CertificateValidationCallback { get; set; }
    public IDictionary<string, object> ClientSettings { get; set; } = new Dictionary<string, object>();
}
```

Create `src/ServiceConnect/Configuration/QueueConfiguration.cs`:

```csharp
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Configuration;

public class QueueConfiguration : IQueueConfiguration
{
    public string QueueName { get; set; } = "";
    public string ErrorQueueName { get; set; } = "errors";
    public string AuditQueueName { get; set; } = "audit";
    public string HeartbeatQueueName { get; set; } = "heartbeat";
    public bool AuditingEnabled { get; set; }
    public bool DisableErrors { get; set; }
    public bool PurgeQueueOnStartup { get; set; }
    public IDictionary<string, IList<string>> QueueMappings { get; set; } = new Dictionary<string, IList<string>>();

    public void AddQueueMapping(Type messageType, string queue)
    {
        string key = messageType.FullName!;
        if (!QueueMappings.ContainsKey(key))
            QueueMappings[key] = new List<string>();
        QueueMappings[key].Add(queue);
    }

    public void AddQueueMapping(Type messageType, IList<string> queues)
    {
        string key = messageType.FullName!;
        if (!QueueMappings.ContainsKey(key))
            QueueMappings[key] = new List<string>();
        foreach (string queue in queues)
            QueueMappings[key].Add(queue);
    }
}
```

Create `src/ServiceConnect/Configuration/PersistenceConfiguration.cs`:

```csharp
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Configuration;

public class PersistenceConfiguration : IPersistenceConfiguration
{
    public string ConnectionString { get; set; } = "mongodb://localhost/";
    public string DatabaseName { get; set; } = "RMessageBusPersistantStore";
    public string AggregatorCollectionName { get; set; } = "Aggregator";
}
```

Create `src/ServiceConnect/Configuration/PipelineConfiguration.cs`:

```csharp
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Configuration;

public class PipelineConfiguration : IPipelineConfiguration
{
    public IList<Type> BeforeConsumingFilters { get; } = new List<Type>();
    public IList<Type> AfterConsumingFilters { get; } = new List<Type>();
    public IList<Type> OutgoingFilters { get; } = new List<Type>();
    public IList<Type> MessageProcessingMiddleware { get; } = new List<Type>();
    public IList<Type> SendMessageMiddleware { get; } = new List<Type>();
}
```

Create `src/ServiceConnect/Configuration/BusConfiguration.cs`:

```csharp
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Configuration;

public class BusConfiguration : IBusConfiguration
{
    public bool ScanForMessageHandlers { get; set; } = true;
    public bool AutoStartConsuming { get; set; } = true;
    public bool EnableProcessManagerTimeouts { get; set; }
    public int Clients { get; set; } = 1;
    public Action<Exception>? ExceptionHandler { get; set; }
    public ITransportConfiguration Transport { get; } = new TransportConfiguration();
    public IQueueConfiguration Queues { get; } = new QueueConfiguration();
    public IPersistenceConfiguration Persistence { get; } = new PersistenceConfiguration();
    public IPipelineConfiguration Pipeline { get; } = new PipelineConfiguration();
}
```

- [ ] **Step 11: Implement RequestReplyManager**

Create `src/ServiceConnect/Services/RequestReplyManager.cs`:

```csharp
using System.Collections.Concurrent;
using Newtonsoft.Json;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;

namespace ServiceConnect.Services;

public class RequestReplyManager : IRequestReplyManager
{
    private readonly ConcurrentDictionary<string, RequestState> _pendingRequests = new();

    public async Task<TReply> SendRequestAsync<TRequest, TReply>(
        byte[] messageBytes,
        Dictionary<string, string> headers,
        Action<Type, byte[], Dictionary<string, string>, string?> sendAction,
        RequestOptions options)
        where TRequest : Message
        where TReply : Message
    {
        var messageId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<object>();
        _pendingRequests[messageId.ToString()] = new RequestState(tcs, 1);

        headers["RequestMessageId"] = messageId.ToString();

        if (!string.IsNullOrEmpty(options.EndPoint))
            sendAction(typeof(TRequest), messageBytes, headers, options.EndPoint);
        else
            sendAction(typeof(TRequest), messageBytes, headers, null);

        using var cts = new CancellationTokenSource(options.Timeout);
        cts.Token.Register(() => tcs.TrySetCanceled());

        try
        {
            var result = await tcs.Task;
            return (TReply)result;
        }
        catch (OperationCanceledException)
        {
            _pendingRequests.TryRemove(messageId.ToString(), out _);
            throw new RequestTimeoutException(messageId, TimeSpan.FromMilliseconds(options.Timeout));
        }
    }

    public async Task<IList<TReply>> SendRequestMultiAsync<TRequest, TReply>(
        byte[] messageBytes,
        Dictionary<string, string> headers,
        Action<Type, byte[], Dictionary<string, string>, string?> sendAction,
        RequestOptions options)
        where TRequest : Message
        where TReply : Message
    {
        var messageId = Guid.NewGuid();
        var responses = new List<TReply>();
        int expectedCount = options.ExpectedReplyCount ?? options.EndPoints?.Count ?? -1;
        var tcs = new TaskCompletionSource<object>();

        _pendingRequests[messageId.ToString()] = new RequestState(tcs, expectedCount, reply =>
        {
            responses.Add((TReply)reply);
            if (expectedCount > 0 && responses.Count >= expectedCount)
                tcs.TrySetResult(null!);
        });

        headers["RequestMessageId"] = messageId.ToString();

        if (options.EndPoints != null)
        {
            foreach (string endPoint in options.EndPoints)
                sendAction(typeof(TRequest), messageBytes, headers, endPoint);
        }
        else
        {
            sendAction(typeof(TRequest), messageBytes, headers, null);
        }

        using var cts = new CancellationTokenSource(options.Timeout);
        cts.Token.Register(() => tcs.TrySetResult(null!)); // timeout returns what we have

        await tcs.Task;
        _pendingRequests.TryRemove(messageId.ToString(), out _);

        return responses;
    }

    public void ProcessReply(string messageId, string messageJson, Type type)
    {
        if (!_pendingRequests.TryGetValue(messageId, out var state))
            return;

        object reply = JsonConvert.DeserializeObject(messageJson, type)!;

        if (state.OnReply != null)
        {
            state.OnReply(reply);
        }
        else
        {
            state.Tcs.TrySetResult(reply);
            _pendingRequests.TryRemove(messageId, out _);
        }
    }

    private record RequestState(
        TaskCompletionSource<object> Tcs,
        int ExpectedCount,
        Action<object>? OnReply = null);
}
```

- [ ] **Step 12: Verify all new services compile**

Run: `dotnet build src/ServiceConnect/ServiceConnect.csproj`

Expected: Build succeeds. Fix any compilation errors.

- [ ] **Step 13: Run all tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ --filter "FullyQualifiedName~Pipeline" --no-restore`

Expected: All tests PASS.

- [ ] **Step 14: Commit**

```bash
git add src/ServiceConnect/ src/ServiceConnect.UnitTests/Pipeline/
git commit -m "feat: add extracted services (serializer, filter pipeline, request-reply manager, configuration)"
```

---

## Phase 3: New Bus — Thin Orchestrator

### Task 3: Rewrite Bus as a thin orchestrator

**Files:**
- Modify: `src/ServiceConnect/Bus.cs`
- Test: `src/ServiceConnect.UnitTests/Bus/BusPublishTests.cs`
- Test: `src/ServiceConnect.UnitTests/Bus/BusSendTests.cs`
- Test: `src/ServiceConnect.UnitTests/Bus/BusRequestReplyTests.cs`

- [ ] **Step 1: Write failing test for Bus.PublishAsync**

Create `src/ServiceConnect.UnitTests/Bus/BusPublishTests.cs`:

```csharp
using Moq;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.Bus;

public class BusPublishTests
{
    private readonly Mock<IProducer> _producer = new();
    private readonly Mock<IConsumer> _consumer = new();
    private readonly Mock<IMessageSerializer> _serializer = new();
    private readonly Mock<IFilterPipeline> _filterPipeline = new();
    private readonly Mock<IRequestReplyManager> _requestReplyManager = new();
    private readonly Mock<ISendMessagePipeline> _sendPipeline = new();
    private readonly Mock<IBusConfiguration> _config = new();
    private readonly Mock<ILogger<ServiceConnect.Bus>> _logger = new();

    public BusPublishTests()
    {
        var pipelineConfig = new Mock<IPipelineConfiguration>();
        pipelineConfig.Setup(x => x.OutgoingFilters).Returns(new List<Type>());
        _config.Setup(c => c.Pipeline).Returns(pipelineConfig.Object);
    }

    private ServiceConnect.Bus CreateBus()
    {
        return new ServiceConnect.Bus(
            _serializer.Object,
            _filterPipeline.Object,
            _sendPipeline.Object,
            _requestReplyManager.Object,
            _config.Object,
            _logger.Object);
    }

    [Fact]
    public async Task PublishAsync_SerializesAndSendsViaPipeline()
    {
        var message = new TestMessage(Guid.NewGuid());
        byte[] expectedBytes = new byte[] { 1, 2, 3 };
        _serializer.Setup(s => s.Serialize(message)).Returns(expectedBytes);
        _filterPipeline.Setup(f => f.ExecuteOutgoingFilters(It.IsAny<Envelope>())).Returns(false);

        var bus = CreateBus();
        await bus.PublishAsync(message);

        _serializer.Verify(s => s.Serialize(message), Times.Once);
        _sendPipeline.Verify(p => p.ExecutePublishMessagePipeline(
            typeof(TestMessage), expectedBytes, It.IsAny<Dictionary<string, string>>(), null), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_WithRoutingKey_IncludesInHeaders()
    {
        var message = new TestMessage(Guid.NewGuid());
        _serializer.Setup(s => s.Serialize(message)).Returns(new byte[] { 1 });
        _filterPipeline.Setup(f => f.ExecuteOutgoingFilters(It.IsAny<Envelope>())).Returns(false);

        var bus = CreateBus();
        await bus.PublishAsync(message, new PublishOptions { RoutingKey = "my.key" });

        _sendPipeline.Verify(p => p.ExecutePublishMessagePipeline(
            typeof(TestMessage),
            It.IsAny<byte[]>(),
            It.Is<Dictionary<string, string>>(h => h["RoutingKey"] == "my.key"),
            null), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_WhenFilterStops_DoesNotSend()
    {
        var message = new TestMessage(Guid.NewGuid());
        _serializer.Setup(s => s.Serialize(message)).Returns(new byte[] { 1 });
        _filterPipeline.Setup(f => f.ExecuteOutgoingFilters(It.IsAny<Envelope>())).Returns(true);

        var bus = CreateBus();
        await bus.PublishAsync(message);

        _sendPipeline.Verify(p => p.ExecutePublishMessagePipeline(
            It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<string>()), Times.Never);
    }

    public class TestMessage : Message
    {
        public TestMessage(Guid correlationId) : base(correlationId) { }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/ServiceConnect.UnitTests/ --filter "FullyQualifiedName~BusPublishTests" --no-restore`

Expected: FAIL — Bus constructor doesn't match new signature yet.

- [ ] **Step 3: Rewrite Bus.cs as thin orchestrator**

Replace `src/ServiceConnect/Bus.cs` with the new implementation. The Bus takes all dependencies via constructor injection — no service locator, no Activator.CreateInstance:

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Exceptions;

namespace ServiceConnect;

public class Bus : IBus
{
    private readonly IMessageSerializer _serializer;
    private readonly IFilterPipeline _filterPipeline;
    private readonly ISendMessagePipeline _sendPipeline;
    private readonly IRequestReplyManager _requestReplyManager;
    private readonly IBusConfiguration _config;
    private readonly ILogger<Bus> _logger;
    private IConsumer? _consumer;
    private bool _startedConsuming;

    public bool IsConnected => _consumer?.IsConnected ?? false;

    public Bus(
        IMessageSerializer serializer,
        IFilterPipeline filterPipeline,
        ISendMessagePipeline sendPipeline,
        IRequestReplyManager requestReplyManager,
        IBusConfiguration config,
        ILogger<Bus> logger)
    {
        _serializer = serializer;
        _filterPipeline = filterPipeline;
        _sendPipeline = sendPipeline;
        _requestReplyManager = requestReplyManager;
        _config = config;
        _logger = logger;
    }

    public Task PublishAsync<T>(T message, PublishOptions? options = null) where T : Message
    {
        var headers = options?.Headers ?? new Dictionary<string, string>();
        byte[] messageBytes = _serializer.Serialize(message);

        var envelope = CreateOutgoingEnvelope(messageBytes, headers);
        if (_filterPipeline.ExecuteOutgoingFilters(envelope))
            return Task.CompletedTask;

        headers = ExtractStringHeaders(envelope);
        messageBytes = envelope.Body;

        if (!string.IsNullOrEmpty(options?.RoutingKey))
            headers["RoutingKey"] = options.RoutingKey;

        _sendPipeline.ExecutePublishMessagePipeline(typeof(T), messageBytes, headers);
        return Task.CompletedTask;
    }

    public Task SendAsync<T>(T message, SendOptions? options = null) where T : Message
    {
        var headers = options?.Headers ?? new Dictionary<string, string>();
        byte[] messageBytes = _serializer.Serialize(message);

        var envelope = CreateOutgoingEnvelope(messageBytes, headers);
        if (_filterPipeline.ExecuteOutgoingFilters(envelope))
            return Task.CompletedTask;

        headers = ExtractStringHeaders(envelope);
        messageBytes = envelope.Body;

        if (options?.EndPoints != null)
        {
            foreach (string endPoint in options.EndPoints)
                _sendPipeline.ExecuteSendMessagePipeline(typeof(T), messageBytes, headers, endPoint);
        }
        else
        {
            _sendPipeline.ExecuteSendMessagePipeline(typeof(T), messageBytes, headers, options?.EndPoint);
        }

        return Task.CompletedTask;
    }

    public async Task<TReply> SendRequestAsync<T, TReply>(T message, RequestOptions? options = null)
        where T : Message where TReply : Message
    {
        options ??= new RequestOptions();
        var headers = options.Headers ?? new Dictionary<string, string>();
        byte[] messageBytes = _serializer.Serialize(message);

        var envelope = CreateOutgoingEnvelope(messageBytes, headers);
        if (_filterPipeline.ExecuteOutgoingFilters(envelope))
            return default!;

        headers = ExtractStringHeaders(envelope);
        messageBytes = envelope.Body;

        return await _requestReplyManager.SendRequestAsync<T, TReply>(
            messageBytes, headers,
            (type, bytes, hdrs, ep) =>
            {
                if (ep != null)
                    _sendPipeline.ExecuteSendMessagePipeline(type, bytes, hdrs, ep);
                else
                    _sendPipeline.ExecuteSendMessagePipeline(type, bytes, hdrs);
            },
            options);
    }

    public async Task<IList<TReply>> SendRequestMultiAsync<T, TReply>(T message, RequestOptions? options = null)
        where T : Message where TReply : Message
    {
        options ??= new RequestOptions();
        var headers = options.Headers ?? new Dictionary<string, string>();
        byte[] messageBytes = _serializer.Serialize(message);

        var envelope = CreateOutgoingEnvelope(messageBytes, headers);
        if (_filterPipeline.ExecuteOutgoingFilters(envelope))
            return new List<TReply>();

        headers = ExtractStringHeaders(envelope);
        messageBytes = envelope.Body;

        return await _requestReplyManager.SendRequestMultiAsync<T, TReply>(
            messageBytes, headers,
            (type, bytes, hdrs, ep) =>
            {
                if (ep != null)
                    _sendPipeline.ExecuteSendMessagePipeline(type, bytes, hdrs, ep);
                else
                    _sendPipeline.ExecuteSendMessagePipeline(type, bytes, hdrs);
            },
            options);
    }

    public Task PublishRequestAsync<TRequest, TReply>(TRequest message, Action<TReply> onReply, RequestOptions? options = null)
        where TRequest : Message where TReply : Message
    {
        // Delegate to multi-request with callback pattern
        return SendRequestMultiAsync<TRequest, TReply>(message, options);
    }

    public void Route<T>(T message, IList<string> destinations) where T : Message
    {
        if (destinations.Count == 0) return;

        string nextDestination = destinations[0];
        var remaining = destinations.Skip(1).ToList();

        var headers = new Dictionary<string, string>
        {
            { "RoutingSlip", Newtonsoft.Json.JsonConvert.SerializeObject(remaining) }
        };

        byte[] messageBytes = _serializer.Serialize(message);

        var envelope = CreateOutgoingEnvelope(messageBytes, headers);
        if (_filterPipeline.ExecuteOutgoingFilters(envelope))
            return;

        headers = ExtractStringHeaders(envelope);
        messageBytes = envelope.Body;

        _sendPipeline.ExecuteSendMessagePipeline(typeof(T), messageBytes, headers, nextDestination);
    }

    public IMessageBusWriteStream CreateStream<T>(string endpoint, T message) where T : Message
    {
        throw new NotImplementedException("Stream support will be wired via IStreamManager");
    }

    public void StartConsuming()
    {
        if (_startedConsuming) return;
        _startedConsuming = true;
        _logger.LogInformation("Bus started consuming");
    }

    public void StopConsuming()
    {
        _consumer?.Dispose();
        _startedConsuming = false;
    }

    public void Dispose()
    {
        try { StopConsuming(); }
        catch (Exception ex) { _logger.LogError(ex, "Error stopping consuming"); }

        try { _sendPipeline.Dispose(); }
        catch (Exception ex) { _logger.LogError(ex, "Error disposing send pipeline"); }
    }

    private static Envelope CreateOutgoingEnvelope(byte[] body, Dictionary<string, string> headers)
    {
        return new Envelope
        {
            Headers = headers.ToDictionary(x => x.Key, x => (object)x.Value),
            Body = body
        };
    }

    private static Dictionary<string, string> ExtractStringHeaders(Envelope envelope)
    {
        return envelope.Headers.ToDictionary(x => x.Key, x => x.Value?.ToString() ?? "");
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/ServiceConnect.UnitTests/ --filter "FullyQualifiedName~BusPublishTests" --no-restore`

Expected: All 3 tests PASS.

- [ ] **Step 5: Write and run Bus send tests**

Create `src/ServiceConnect.UnitTests/Bus/BusSendTests.cs` with tests for `SendAsync` — single endpoint, multiple endpoints, and filter-stops behavior. Follow the same mock pattern as BusPublishTests.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect/Bus.cs src/ServiceConnect.UnitTests/Bus/
git commit -m "feat: rewrite Bus as thin orchestrator with dependency injection"
```

---

## Phase 4: DI Registration — ServiceConnect Builder

### Task 4: Implement the builder and AddServiceConnect extension

**Files:**
- Create: `src/ServiceConnect/ServiceConnectBuilder.cs`
- Create: `src/ServiceConnect/ServiceCollectionExtensions.cs`
- Test: `src/ServiceConnect.UnitTests/Configuration/ServiceConnectBuilderTests.cs`

- [ ] **Step 1: Write failing test for builder**

Create `src/ServiceConnect.UnitTests/Configuration/ServiceConnectBuilderTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.Configuration;

public class ServiceConnectBuilderTests
{
    [Fact]
    public void AddServiceConnect_RegistersBusAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddServiceConnect(builder =>
        {
            builder.ConfigureQueues(q => q.QueueName = "test-queue");
        });

        var provider = services.BuildServiceProvider();
        var bus1 = provider.GetRequiredService<IBus>();
        var bus2 = provider.GetRequiredService<IBus>();

        Assert.Same(bus1, bus2);
    }

    [Fact]
    public void AddServiceConnect_RegistersConfiguration()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddServiceConnect(builder =>
        {
            builder.ConfigureTransport(t => t.Host = "rabbitmq.local");
            builder.ConfigureQueues(q => q.QueueName = "my-queue");
        });

        var provider = services.BuildServiceProvider();
        var config = provider.GetRequiredService<IBusConfiguration>();

        Assert.Equal("rabbitmq.local", config.Transport.Host);
        Assert.Equal("my-queue", config.Queues.QueueName);
    }

    [Fact]
    public void AddServiceConnect_RegistersSerializer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddServiceConnect(_ => { });

        var provider = services.BuildServiceProvider();
        var serializer = provider.GetRequiredService<IMessageSerializer>();

        Assert.NotNull(serializer);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/ServiceConnect.UnitTests/ --filter "FullyQualifiedName~ServiceConnectBuilderTests" --no-restore`

Expected: FAIL — `ServiceConnectBuilder` and `AddServiceConnect` do not exist.

- [ ] **Step 3: Implement ServiceConnectBuilder**

Create `src/ServiceConnect/ServiceConnectBuilder.cs`:

```csharp
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect;

public class ServiceConnectBuilder
{
    internal BusConfiguration BusConfig { get; } = new();

    public ServiceConnectBuilder ConfigureTransport(Action<ITransportConfiguration> configure)
    {
        configure(BusConfig.Transport);
        return this;
    }

    public ServiceConnectBuilder ConfigureQueues(Action<IQueueConfiguration> configure)
    {
        configure(BusConfig.Queues);
        return this;
    }

    public ServiceConnectBuilder ConfigurePersistence(Action<IPersistenceConfiguration> configure)
    {
        configure(BusConfig.Persistence);
        return this;
    }

    public ServiceConnectBuilder ConfigurePipeline(Action<IPipelineConfiguration> configure)
    {
        configure(BusConfig.Pipeline);
        return this;
    }

    public ServiceConnectBuilder ConfigureBus(Action<IBusConfiguration> configure)
    {
        configure(BusConfig);
        return this;
    }

    public ServiceConnectBuilder AddOutgoingFilter<T>() where T : class, Interfaces.IFilter
    {
        BusConfig.Pipeline.OutgoingFilters.Add(typeof(T));
        return this;
    }

    public ServiceConnectBuilder AddBeforeConsumingFilter<T>() where T : class, Interfaces.IFilter
    {
        BusConfig.Pipeline.BeforeConsumingFilters.Add(typeof(T));
        return this;
    }

    public ServiceConnectBuilder AddAfterConsumingFilter<T>() where T : class, Interfaces.IFilter
    {
        BusConfig.Pipeline.AfterConsumingFilters.Add(typeof(T));
        return this;
    }
}
```

- [ ] **Step 4: Implement AddServiceConnect extension method**

Create `src/ServiceConnect/ServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;

namespace ServiceConnect;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddServiceConnect(
        this IServiceCollection services,
        Action<ServiceConnectBuilder> configure)
    {
        var builder = new ServiceConnectBuilder();
        configure(builder);

        // Configuration
        services.TryAddSingleton<IBusConfiguration>(builder.BusConfig);
        services.TryAddSingleton<ITransportConfiguration>(builder.BusConfig.Transport);
        services.TryAddSingleton<IQueueConfiguration>(builder.BusConfig.Queues);
        services.TryAddSingleton<IPersistenceConfiguration>(builder.BusConfig.Persistence);
        services.TryAddSingleton<IPipelineConfiguration>(builder.BusConfig.Pipeline);

        // Core services
        services.TryAddSingleton<IMessageSerializer, NewtonsoftJsonMessageSerializer>();
        services.TryAddSingleton<IFilterPipeline, FilterPipeline>();
        services.TryAddSingleton<IRequestReplyManager, RequestReplyManager>();

        // Bus
        services.TryAddSingleton<IBus, Bus>();

        return services;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test src/ServiceConnect.UnitTests/ --filter "FullyQualifiedName~ServiceConnectBuilderTests" --no-restore`

Expected: All 3 tests PASS.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect/ServiceConnectBuilder.cs src/ServiceConnect/ServiceCollectionExtensions.cs src/ServiceConnect.UnitTests/Configuration/
git commit -m "feat: add ServiceConnectBuilder and AddServiceConnect DI extension"
```

---

## Phase 5: MongoDB Persistence Merge

### Task 5: Create merged ServiceConnect.Persistence.MongoDb project

**Files:**
- Create: `src/ServiceConnect.Persistence.MongoDb/ServiceConnect.Persistence.MongoDb.csproj`
- Create: `src/ServiceConnect.Persistence.MongoDb/MongoDbPersistenceOptions.cs`
- Create: `src/ServiceConnect.Persistence.MongoDb/MongoDbSslOptions.cs`
- Create: `src/ServiceConnect.Persistence.MongoDb/MongoClientFactory.cs`
- Create: `src/ServiceConnect.Persistence.MongoDb/MongoDbData.cs`
- Create: `src/ServiceConnect.Persistence.MongoDb/MongoDbAggregatorPersistor.cs`
- Create: `src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs`
- Create: `src/ServiceConnect.Persistence.MongoDb/MongoDbPersistenceExtensions.cs`
- Test: `src/ServiceConnect.UnitTests/Persistence/MongoDb/MongoClientFactoryTests.cs`
- Test: `src/ServiceConnect.UnitTests/Persistence/MongoDb/AggregatorPersistorTests.cs`

- [ ] **Step 1: Create .csproj**

Create `src/ServiceConnect.Persistence.MongoDb/ServiceConnect.Persistence.MongoDb.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
    </PropertyGroup>
    <ItemGroup>
        <ProjectReference Include="..\ServiceConnect.Interfaces\ServiceConnect.Interfaces.csproj" />
        <ProjectReference Include="..\ServiceConnect\ServiceConnect.csproj" />
    </ItemGroup>
    <ItemGroup>
        <PackageReference Include="MongoDB.Driver" Version="2.23.1" />
        <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="9.0.0" />
    </ItemGroup>
</Project>
```

- [ ] **Step 2: Create options and factory**

Create `src/ServiceConnect.Persistence.MongoDb/MongoDbPersistenceOptions.cs`:

```csharp
namespace ServiceConnect.Persistence.MongoDb;

public class MongoDbPersistenceOptions
{
    public string ConnectionString { get; set; } = "mongodb://localhost/";
    public string DatabaseName { get; set; } = "RMessageBusPersistantStore";
    public MongoDbSslOptions? Ssl { get; set; }
}
```

Create `src/ServiceConnect.Persistence.MongoDb/MongoDbSslOptions.cs`:

```csharp
using System.Security.Authentication;

namespace ServiceConnect.Persistence.MongoDb;

public class MongoDbSslOptions
{
    public string? CertPath { get; set; }
    public string? CertPassphrase { get; set; }
    public SslProtocols SslProtocol { get; set; } = SslProtocols.Tls12;
    public bool AllowInsecureTls { get; set; }
}
```

Create `src/ServiceConnect.Persistence.MongoDb/MongoClientFactory.cs`:

```csharp
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using MongoDB.Driver;
using Microsoft.Extensions.Logging;

namespace ServiceConnect.Persistence.MongoDb;

public class MongoClientFactory
{
    private readonly ILogger<MongoClientFactory> _logger;

    public MongoClientFactory(ILogger<MongoClientFactory> logger)
    {
        _logger = logger;
    }

    public IMongoClient CreateClient(MongoDbPersistenceOptions options)
    {
        if (options.Ssl != null)
            return CreateSslClient(options.ConnectionString, options.Ssl);

        return new MongoClient(options.ConnectionString);
    }

    private IMongoClient CreateSslClient(string connectionString, MongoDbSslOptions sslOptions)
    {
        var settings = MongoClientSettings.FromUrl(new MongoUrl(connectionString));

        settings.SslSettings = new SslSettings
        {
            EnabledSslProtocols = sslOptions.SslProtocol
        };

        if (!string.IsNullOrEmpty(sslOptions.CertPath))
        {
            var cert = string.IsNullOrEmpty(sslOptions.CertPassphrase)
                ? new X509Certificate2(sslOptions.CertPath)
                : new X509Certificate2(sslOptions.CertPath, sslOptions.CertPassphrase);

            settings.SslSettings.ClientCertificates = new[] { cert };
        }

        settings.UseTls = true;
        settings.AllowInsecureTls = sslOptions.AllowInsecureTls;

        _logger.LogDebug("Creating MongoDB client with SSL enabled");
        return new MongoClient(settings);
    }
}
```

- [ ] **Step 3: Create unified MongoDbData**

Create `src/ServiceConnect.Persistence.MongoDb/MongoDbData.cs`:

```csharp
using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistence.MongoDb;

public class MongoDbData<T> : IPersistenceData<T> where T : class, IProcessManagerData
{
    public Guid Id { get; set; }
    public T Data { get; set; } = default!;
    public bool Locked { get; set; }
}
```

- [ ] **Step 4: Create merged AggregatorPersistor**

Create `src/ServiceConnect.Persistence.MongoDb/MongoDbAggregatorPersistor.cs`:

Port from the current `MongoDBAggregatorPersistor.cs` but use `MongoClientFactory` and `MongoDbPersistenceOptions`. Wrap MongoDB exceptions in `PersistenceException`:

```csharp
using MongoDB.Driver;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;

namespace ServiceConnect.Persistence.MongoDb;

public class MongoDbAggregatorPersistor : IAggregatorPersistor
{
    private readonly IMongoDatabase _database;
    private readonly string _collectionName;
    private readonly ILogger<MongoDbAggregatorPersistor> _logger;

    public MongoDbAggregatorPersistor(
        MongoDbPersistenceOptions options,
        MongoClientFactory clientFactory,
        ILogger<MongoDbAggregatorPersistor> logger,
        string? collectionName = null)
    {
        _logger = logger;
        _collectionName = collectionName ?? "Aggregator";
        var client = clientFactory.CreateClient(options);
        _database = client.GetDatabase(options.DatabaseName);
    }

    public void InsertData(object data, string name)
    {
        try
        {
            var collection = _database.GetCollection<object>(name);
            collection.InsertOne(data);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to insert aggregator data into collection '{name}'", ex);
        }
    }

    public IList<object> GetData(string name)
    {
        try
        {
            var collection = _database.GetCollection<object>(name);
            return collection.Find(_ => true).ToList();
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to get aggregator data from collection '{name}'", ex);
        }
    }

    public void RemoveData(string name, Guid correlationId)
    {
        try
        {
            var collection = _database.GetCollection<object>(name);
            var filter = Builders<object>.Filter.Eq("CorrelationId", correlationId);
            collection.DeleteOne(filter);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to remove aggregator data from collection '{name}'", ex);
        }
    }

    public int Count(string name)
    {
        try
        {
            var collection = _database.GetCollection<object>(name);
            return (int)collection.CountDocuments(_ => true);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to count aggregator data in collection '{name}'", ex);
        }
    }
}
```

- [ ] **Step 5: Create merged ProcessManagerFinder**

Create `src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs` — merged implementation from both current versions. Carry forward the SSL version's locking mechanism for `GetTimeoutsBatch`. Wrap all MongoDB exceptions in `PersistenceException`. No empty catch blocks.

This is the largest file in this task. Port from `src/ServiceConnect.Persistance.MongoDbSsl/MongoDbSslProcessManagerFinder.cs` (which has the more complete implementation including locking), but use `MongoClientFactory` for connection setup.

- [ ] **Step 6: Create builder extension**

Create `src/ServiceConnect.Persistence.MongoDb/MongoDbPersistenceExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistence.MongoDb;

public static class MongoDbPersistenceExtensions
{
    public static ServiceConnectBuilder UseMongoDbPersistence(
        this ServiceConnectBuilder builder,
        Action<MongoDbPersistenceOptions> configure)
    {
        var options = new MongoDbPersistenceOptions();
        configure(options);

        // Options and factory will be registered via the returned builder
        // The actual registration happens in AddServiceConnect pipeline
        return builder;
    }
}
```

Note: The full DI wiring will need `IServiceCollection` access. Extend `ServiceConnectBuilder` to collect service registrations and apply them during `AddServiceConnect`.

- [ ] **Step 7: Verify it compiles**

Run: `dotnet build src/ServiceConnect.Persistence.MongoDb/ServiceConnect.Persistence.MongoDb.csproj`

Expected: Build succeeds.

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect.Persistence.MongoDb/
git commit -m "feat: create merged ServiceConnect.Persistence.MongoDb with SSL as config option"
```

---

## Phase 6: Update RabbitMQ Client & InMemory Persistence

### Task 6: Update ServiceConnect.Client.RabbitMQ for new interfaces

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer.cs`

- [ ] **Step 1: Update .csproj to multi-target and reference new Interfaces**

Update `src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
    </PropertyGroup>
    <ItemGroup>
        <ProjectReference Include="..\ServiceConnect.Interfaces\ServiceConnect.Interfaces.csproj" />
        <ProjectReference Include="..\ServiceConnect\ServiceConnect.csproj" />
    </ItemGroup>
    <ItemGroup>
        <PackageReference Include="RabbitMQ.Client" Version="6.8.1" />
        <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
        <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="9.0.0" />
    </ItemGroup>
</Project>
```

- [ ] **Step 2: Update Consumer to implement new IConsumer**

Update `src/ServiceConnect.Client.RabbitMQ/Consumer.cs` to:
- Implement `IAsyncDisposable` alongside `IDisposable`
- Change `StartConsuming` to `StartConsumingAsync` (remove `IConfiguration` parameter — consumer gets what it needs via constructor DI)
- Change `IsConnected()` method to `IsConnected` property
- Replace `ILogger` with `Microsoft.Extensions.Logging.ILogger<Consumer>`

- [ ] **Step 3: Update Producer to implement new IProducer**

Update `src/ServiceConnect.Client.RabbitMQ/Producer.cs` to:
- Implement `IAsyncDisposable` alongside `IDisposable`
- Rename methods to async variants (`PublishAsync`, `SendAsync`, `SendBytesAsync`)
- Replace `ILogger` with `Microsoft.Extensions.Logging.ILogger<Producer>`
- Accept `ITransportConfiguration` and `IQueueConfiguration` instead of `ITransportSettings`

- [ ] **Step 4: Create RabbitMQ builder extension**

Create `src/ServiceConnect.Client.RabbitMQ/RabbitMQExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

public static class RabbitMQExtensions
{
    public static ServiceConnectBuilder UseRabbitMQ(
        this ServiceConnectBuilder builder,
        Action<ITransportConfiguration>? configure = null)
    {
        configure?.Invoke(builder.BusConfig.Transport);
        // Consumer/Producer registration will happen in AddServiceConnect
        return builder;
    }
}
```

- [ ] **Step 5: Verify it compiles**

Run: `dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj`

Expected: Build succeeds.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/
git commit -m "feat: update RabbitMQ client for new async-first interfaces"
```

### Task 7: Rename and update InMemory persistence

**Files:**
- Create: `src/ServiceConnect.Persistence.InMemory/` (new directory — renamed from Persistance)
- Modify all `.cs` files in the project

- [ ] **Step 1: Create new project directory and copy files**

```bash
cp -r src/ServiceConnect.Persistance.InMemory src/ServiceConnect.Persistence.InMemory
```

- [ ] **Step 2: Update .csproj and namespace**

Update `src/ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj` to multi-target `net8.0;net10.0`. Update `RootNamespace` to `ServiceConnect.Persistence.InMemory`.

- [ ] **Step 3: Update all files to use new namespace and IPersistenceData**

Replace `ServiceConnect.Persistance.InMemory` with `ServiceConnect.Persistence.InMemory` in all files. Replace `IPersistanceData` with `IPersistenceData`.

- [ ] **Step 4: Verify it compiles**

Run: `dotnet build src/ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj`

Expected: Build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Persistence.InMemory/
git commit -m "feat: rename InMemory persistence (fix Persistance typo), update interfaces"
```

---

## Phase 7: Update Solution & Remove Dead Projects

### Task 8: Update solution file and remove dead projects

**Files:**
- Modify: `src/ServiceConnect.sln`
- Delete: `src/ServiceConnect.Core/`
- Delete: `src/ServiceConnect.Container.Default/`
- Delete: `src/ServiceConnect.Container.StructureMap/`
- Delete: `src/ServiceConnect.Container.Ninject/`
- Delete: `src/ServiceConnect.Container.ServiceCollection/`
- Delete: `src/ServiceConnect.Persistance.MongoDb/`
- Delete: `src/ServiceConnect.Persistance.MongoDbSsl/`
- Delete: `src/ServiceConnect.Persistance.SqlServer/`
- Delete: `src/ServiceConnect.Persistance.InMemory/` (replaced by Persistence.InMemory)
- Delete: `src/ServiceConnect.IntegrationTestsSsl/`

- [ ] **Step 1: Remove old projects from solution**

```bash
cd /home/tim/source/ServiceConnect-CSharp/src
dotnet sln remove ServiceConnect.Core/ServiceConnect.Core.csproj
dotnet sln remove ServiceConnect.Container.Default/ServiceConnect.Container.Default.csproj
dotnet sln remove ServiceConnect.Container.StructureMap/ServiceConnect.Container.StructureMap.csproj
dotnet sln remove ServiceConnect.Container.Ninject/ServiceConnect.Container.Ninject.csproj
dotnet sln remove ServiceConnect.Container.ServiceCollection/ServiceConnect.Container.ServiceCollection.csproj
dotnet sln remove ServiceConnect.Persistance.MongoDb/ServiceConnect.Persistance.MongoDb.csproj
dotnet sln remove ServiceConnect.Persistance.MongoDbSsl/ServiceConnect.Persistance.MongoDbSsl.csproj
dotnet sln remove ServiceConnect.Persistance.SqlServer/ServiceConnect.Persistance.SqlServer.csproj
dotnet sln remove ServiceConnect.Persistance.InMemory/ServiceConnect.Persistance.InMemory.csproj
dotnet sln remove ServiceConnect.IntegrationTestsSsl/ServiceConnect.IntegrationTestsSsl.csproj
```

- [ ] **Step 2: Add new projects to solution**

```bash
dotnet sln add ServiceConnect.Persistence.MongoDb/ServiceConnect.Persistence.MongoDb.csproj
dotnet sln add ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj
```

- [ ] **Step 3: Delete old project directories**

```bash
rm -rf ServiceConnect.Core/
rm -rf ServiceConnect.Container.Default/
rm -rf ServiceConnect.Container.StructureMap/
rm -rf ServiceConnect.Container.Ninject/
rm -rf ServiceConnect.Container.ServiceCollection/
rm -rf ServiceConnect.Persistance.MongoDb/
rm -rf ServiceConnect.Persistance.MongoDbSsl/
rm -rf ServiceConnect.Persistance.SqlServer/
rm -rf ServiceConnect.Persistance.InMemory/
rm -rf ServiceConnect.IntegrationTestsSsl/
```

- [ ] **Step 4: Remove old Configuration.cs (replaced by split configs)**

Delete `src/ServiceConnect/Configuration.cs` — fully replaced by the new configuration classes in `src/ServiceConnect/Configuration/`.

- [ ] **Step 5: Verify full solution builds**

Run: `dotnet build src/ServiceConnect.sln`

Expected: Build succeeds. Fix any remaining reference issues.

- [ ] **Step 6: Commit**

```bash
git add -A src/
git commit -m "chore: remove dead projects, update solution file (17 -> 8 projects)"
```

---

## Phase 8: Update Tests

### Task 9: Update unit tests for new architecture

**Files:**
- Modify: `src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj`
- Delete: `src/ServiceConnect.UnitTests/Container/DefaultBusContainerTests.cs`
- Delete: `src/ServiceConnect.UnitTests/Container/DefaultContainerTests.cs`
- Delete: `src/ServiceConnect.UnitTests/Container/StructureMapContainerTests.cs`
- Modify: remaining test files to use new interfaces

- [ ] **Step 1: Update UnitTests .csproj**

Update `src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj` to:
- Multi-target `net8.0;net10.0`
- Reference new projects (ServiceConnect, ServiceConnect.Interfaces)
- Remove references to deleted projects (Container.Default, Container.StructureMap, etc.)
- Add `Microsoft.Extensions.Logging` and `Microsoft.Extensions.DependencyInjection` packages

- [ ] **Step 2: Remove tests for dropped projects**

```bash
rm src/ServiceConnect.UnitTests/Container/DefaultBusContainerTests.cs
rm src/ServiceConnect.UnitTests/Container/DefaultContainerTests.cs
rm src/ServiceConnect.UnitTests/Container/StructureMapContainerTests.cs
```

- [ ] **Step 3: Update remaining test files**

Update all remaining test files to use:
- `IBusConfiguration` instead of `IConfiguration`
- `ILogger<T>` instead of `ILogger`
- New constructor signatures for Bus
- `IPersistenceData` instead of `IPersistanceData`
- Async test methods where appropriate

Key files to update:
- `BusTests.cs` — update mock setup for new Bus constructor
- `BusSetupTests.cs` — update for new configuration
- `ConfigurationTests.cs` — rewrite for split configuration
- `FilterTests.cs` — update for FilterPipeline
- `ServiceCollectionContainerTests.cs` — rewrite for new AddServiceConnect pattern

- [ ] **Step 4: Run all tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ --no-restore`

Expected: All tests PASS. Fix failures iteratively.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.UnitTests/
git commit -m "test: update unit tests for new architecture, remove dead container tests"
```

---

## Phase 9: Update Telemetry & Integration Tests

### Task 10: Update Telemetry project

**Files:**
- Modify: `src/ServiceConnect.Telemetry/ServiceConnect.Telemetry.csproj`
- Modify: `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`

- [ ] **Step 1: Update .csproj to multi-target**

- [ ] **Step 2: Update references to new interfaces**

Ensure `ServiceConnectActivitySource` works with the new `PublishEventArgs`, `SendEventArgs`, `ConsumeEventArgs` (these were kept in Interfaces).

- [ ] **Step 3: Verify it compiles**

Run: `dotnet build src/ServiceConnect.Telemetry/ServiceConnect.Telemetry.csproj`

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.Telemetry/
git commit -m "chore: update Telemetry project for new interfaces"
```

### Task 11: Update Integration Tests

**Files:**
- Modify: `src/ServiceConnect.IntegrationTests/ServiceConnect.IntegrationTests.csproj`
- Delete: `src/ServiceConnect.IntegrationTests/SqlServerProcessManagerFinderTest.cs`
- Modify: `src/ServiceConnect.IntegrationTests/MongoDbProcessManagerFinderTests.cs`

- [ ] **Step 1: Update .csproj**

Remove SqlServer and old MongoDb references. Add new ServiceConnect.Persistence.MongoDb reference. Multi-target.

- [ ] **Step 2: Remove SqlServer integration test**

```bash
rm src/ServiceConnect.IntegrationTests/SqlServerProcessManagerFinderTest.cs
```

- [ ] **Step 3: Update MongoDb integration tests**

Update `MongoDbProcessManagerFinderTests.cs` to use `MongoDbPersistenceOptions` and `MongoClientFactory` instead of direct connection string constructor.

- [ ] **Step 4: Verify it compiles**

Run: `dotnet build src/ServiceConnect.IntegrationTests/ServiceConnect.IntegrationTests.csproj`

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.IntegrationTests/
git commit -m "test: update integration tests for merged MongoDB, remove SqlServer tests"
```

---

## Phase 10: Final Verification

### Task 12: Full solution build and test

- [ ] **Step 1: Build entire solution**

Run: `dotnet build src/ServiceConnect.sln`

Expected: Build succeeds with zero errors.

- [ ] **Step 2: Run all unit tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ --no-restore -v normal`

Expected: All tests PASS.

- [ ] **Step 3: Verify project count**

Run: `dotnet sln src/ServiceConnect.sln list`

Expected: 8 projects listed:
- ServiceConnect.Interfaces
- ServiceConnect
- ServiceConnect.Client.RabbitMQ
- ServiceConnect.Persistence.MongoDb
- ServiceConnect.Persistence.InMemory
- ServiceConnect.Telemetry
- ServiceConnect.UnitTests
- ServiceConnect.IntegrationTests

- [ ] **Step 4: Commit final state**

```bash
git add -A
git commit -m "chore: complete clean architecture refactor - 17 projects consolidated to 8"
```
