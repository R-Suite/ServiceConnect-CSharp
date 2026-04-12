# Code Review Remediations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix 7 code review findings: magic strings, missing interface, insecure deserialization, fat interface, dead publisher-confirm code, timer leak, and sync-over-async blocking.

**Architecture:** Each task is independent and produces a working build. Tasks are ordered so earlier ones clean up files touched by later ones. The async refactor (Task 7) is last because it reshapes the RabbitMQ transport layer.

**Tech Stack:** .NET 10, C#, RabbitMQ.Client 7.2.1, xUnit, Moq

**Spec:** `docs/superpowers/specs/2026-04-12-code-review-remediations-design.md`

---

### Task 1: RabbitMQ Settings Constants

**Files:**
- Create: `src/ServiceConnect.Client.RabbitMQ/RabbitMQSettingKeys.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Connection.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Client.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer.cs`

- [ ] **Step 1: Create `RabbitMQSettingKeys.cs`**

```csharp
namespace ServiceConnect.Client.RabbitMQ;

public static class RabbitMQSettingKeys
{
    public const string Port = "Port";
    public const string Durable = "Durable";
    public const string Exclusive = "Exclusive";
    public const string AutoDelete = "AutoDelete";
    public const string Arguments = "Arguments";
    public const string RetryQueueArguments = "RetryQueueArguments";
    public const string UtilityQueueArguments = "UtilityQueueArguments";
    public const string PrefetchCount = "PrefetchCount";
    public const string DisablePrefetch = "DisablePrefetch";
    public const string MessageSize = "MessageSize";
    public const string PublisherAcknowledgements = "PublisherAcknowledgements";
    public const string RetryCount = "RetryCount";
    public const string RetrySeconds = "RetrySeconds";
    public const string HeartbeatEnabled = "HeartbeatEnabled";
    public const string HeartbeatTime = "HeartbeatTime";
}
```

- [ ] **Step 2: Replace magic strings in `Producer.cs`**

Replace all `"MessageSize"`, `"PublisherAcknowledgements"`, `"RetryCount"`, `"RetrySeconds"`, `"Port"` string literals with `RabbitMQSettingKeys.*` constants. These appear in the constructor (lines 42-47) and `CreateConnection` (line 76).

- [ ] **Step 3: Replace magic strings in `Connection.cs`**

Replace `"HeartbeatEnabled"` and `"HeartbeatTime"` (lines 12-13) and `"Port"` (line 42) with `RabbitMQSettingKeys.*` constants.

- [ ] **Step 4: Replace magic strings in `Client.cs`**

Replace `"AutoDelete"`, `"PrefetchCount"`, `"DisablePrefetch"`, `"Arguments"` (lines 44-48) with `RabbitMQSettingKeys.*` constants.

- [ ] **Step 5: Replace magic strings in `Consumer.cs`**

Replace `"Durable"`, `"Exclusive"`, `"AutoDelete"`, `"Arguments"`, `"RetryQueueArguments"`, `"UtilityQueueArguments"` (lines 49-54) with `RabbitMQSettingKeys.*` constants.

- [ ] **Step 6: Build and verify**

Run: `dotnet build src/ServiceConnect.sln`
Expected: 0 errors, 0 warnings

- [ ] **Step 7: Run all tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -v q`
Expected: 204 passed

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/RabbitMQSettingKeys.cs src/ServiceConnect.Client.RabbitMQ/Producer.cs src/ServiceConnect.Client.RabbitMQ/Connection.cs src/ServiceConnect.Client.RabbitMQ/Client.cs src/ServiceConnect.Client.RabbitMQ/Consumer.cs
git commit -m "refactor: replace magic strings with RabbitMQSettingKeys constants"
```

---

### Task 2: IMessageDispatcher Interface

**Files:**
- Create: `src/ServiceConnect.Interfaces/IMessageDispatcher.cs`
- Modify: `src/ServiceConnect/Services/MessageDispatcher.cs`
- Modify: `src/ServiceConnect/Bus.cs`
- Modify: `src/ServiceConnect/ServiceCollectionExtensions.cs`
- Modify: `src/ServiceConnect.UnitTests/BusTests.cs`

- [ ] **Step 1: Create `IMessageDispatcher.cs`**

```csharp
namespace ServiceConnect.Interfaces;

public interface IMessageDispatcher
{
    Task<ConsumeEventResult> Dispatch(byte[] messageBytes, string messageType, IDictionary<string, object> headers);
}
```

- [ ] **Step 2: Make `MessageDispatcher` implement `IMessageDispatcher`**

In `src/ServiceConnect/Services/MessageDispatcher.cs`, add `IMessageDispatcher` to the type declaration. Add `using ServiceConnect.Interfaces;` if not already present.

Change:
```csharp
public sealed class MessageDispatcher(
    ...
) 
```
To:
```csharp
public sealed class MessageDispatcher(
    ...
) : IMessageDispatcher
```

- [ ] **Step 3: Update `Bus.cs` to depend on `IMessageDispatcher`**

In `Bus.cs`, the primary constructor parameter `MessageDispatcher dispatcher` becomes `IMessageDispatcher dispatcher`. The field `_dispatcher` type changes from `MessageDispatcher` to `IMessageDispatcher`. The `_dispatcher.Dispatch` method reference in `StartConsumingAsync` is unchanged since the interface has the same method signature.

- [ ] **Step 4: Update DI registration in `ServiceCollectionExtensions.cs`**

Change line 48:
```csharp
services.TryAddSingleton<MessageDispatcher>();
```
To:
```csharp
services.TryAddSingleton<IMessageDispatcher, MessageDispatcher>();
```

- [ ] **Step 5: Update `BusTests.cs`**

Replace the concrete `MessageDispatcher` construction with a `Mock<IMessageDispatcher>`. The `_dispatcher` field becomes `Mock<IMessageDispatcher>` and the Bus constructor receives `_mockDispatcher.Object`.

Remove the `using ServiceConnect.Services;` and `using ServiceConnect.Services.Processors;` imports if they were only needed for `MessageDispatcher`/processor types. Remove the `ServiceCollection`/`ServiceProvider` setup that was only used to construct the dispatcher.

The Bus constructor call changes from:
```csharp
_bus = new Bus(
    _mockSerializer.Object,
    _mockFilterPipeline.Object,
    _mockSendPipeline.Object,
    _mockRequestReplyManager.Object,
    _mockLogger.Object,
    _mockQueueConfig.Object,
    _dispatcher,
    _handlerReferences);
```
To:
```csharp
_bus = new Bus(
    _mockSerializer.Object,
    _mockFilterPipeline.Object,
    _mockSendPipeline.Object,
    _mockRequestReplyManager.Object,
    _mockLogger.Object,
    _mockQueueConfig.Object,
    _mockDispatcher.Object,
    _handlerReferences);
```

Also update the `StartConsumingAsync_ShouldSetIsConnectedToTrue_WhenConsumerRegistered` test that constructs a second Bus with the same pattern.

Also update the `Constructor_ShouldThrow_WhenDependencyIsNull` test — the `_dispatcher` argument position now takes `_mockDispatcher.Object` instead of the concrete instance, and the null-check test for that parameter uses `(IMessageDispatcher)null!`.

- [ ] **Step 6: Build and verify**

Run: `dotnet build src/ServiceConnect.sln`
Expected: 0 errors, 0 warnings

- [ ] **Step 7: Run all tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -v q`
Expected: 204 passed

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect.Interfaces/IMessageDispatcher.cs src/ServiceConnect/Services/MessageDispatcher.cs src/ServiceConnect/Bus.cs src/ServiceConnect/ServiceCollectionExtensions.cs src/ServiceConnect.UnitTests/BusTests.cs
git commit -m "refactor: extract IMessageDispatcher interface, decouple Bus from concrete dispatcher"
```

---

### Task 3: Message Type Registry

**Files:**
- Create: `src/ServiceConnect.Interfaces/IMessageTypeRegistry.cs`
- Create: `src/ServiceConnect/Services/MessageTypeRegistry.cs`
- Create: `src/ServiceConnect.UnitTests/MessageTypeRegistryTests.cs`
- Modify: `src/ServiceConnect/Services/MessageDispatcher.cs`
- Modify: `src/ServiceConnect/Services/Processors/StreamProcessor.cs`
- Modify: `src/ServiceConnect.Persistence.MongoDb/MongoDbAggregatorPersistor.cs`
- Modify: `src/ServiceConnect/ServiceCollectionExtensions.cs`
- Modify: `src/ServiceConnect.UnitTests/MessageDispatcherTests.cs`

- [ ] **Step 1: Create `IMessageTypeRegistry.cs`**

```csharp
namespace ServiceConnect.Interfaces;

public interface IMessageTypeRegistry
{
    bool TryResolve(string typeName, out Type type);
    void Register(Type type);
}
```

- [ ] **Step 2: Write failing tests for `MessageTypeRegistry`**

Create `src/ServiceConnect.UnitTests/MessageTypeRegistryTests.cs`:
```csharp
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MessageTypeRegistryTests
{
    [Fact]
    public void TryResolve_RegisteredByAssemblyQualifiedName_ReturnsTrue()
    {
        var registry = new MessageTypeRegistry();
        registry.Register(typeof(FakeMessage1));

        var result = registry.TryResolve(typeof(FakeMessage1).AssemblyQualifiedName!, out var type);

        Assert.True(result);
        Assert.Equal(typeof(FakeMessage1), type);
    }

    [Fact]
    public void TryResolve_RegisteredByFullName_ReturnsTrue()
    {
        var registry = new MessageTypeRegistry();
        registry.Register(typeof(FakeMessage1));

        var result = registry.TryResolve(typeof(FakeMessage1).FullName!, out var type);

        Assert.True(result);
        Assert.Equal(typeof(FakeMessage1), type);
    }

    [Fact]
    public void TryResolve_UnregisteredType_ReturnsFalse()
    {
        var registry = new MessageTypeRegistry();

        var result = registry.TryResolve("Some.Unknown.Type, SomeAssembly", out _);

        Assert.False(result);
    }

    [Fact]
    public void Register_DuplicateType_DoesNotThrow()
    {
        var registry = new MessageTypeRegistry();
        registry.Register(typeof(FakeMessage1));
        registry.Register(typeof(FakeMessage1));

        var result = registry.TryResolve(typeof(FakeMessage1).FullName!, out _);
        Assert.True(result);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter MessageTypeRegistryTests -v q`
Expected: FAIL (class does not exist yet)

- [ ] **Step 4: Create `MessageTypeRegistry.cs`**

```csharp
using System.Collections.Concurrent;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

public sealed class MessageTypeRegistry : IMessageTypeRegistry
{
    private readonly ConcurrentDictionary<string, Type> _types = new();

    public bool TryResolve(string typeName, out Type type)
        => _types.TryGetValue(typeName, out type!);

    public void Register(Type type)
    {
        if (type.AssemblyQualifiedName is not null)
            _types[type.AssemblyQualifiedName] = type;
        if (type.FullName is not null)
            _types[type.FullName] = type;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter MessageTypeRegistryTests -v q`
Expected: 4 passed

- [ ] **Step 6: Wire registry into DI and populate from handler scan**

In `ServiceCollectionExtensions.cs`, after the `handlerReferences` list is built (around line 55), add:

```csharp
var registry = new MessageTypeRegistry();
foreach (var handlerRef in handlerReferences)
{
    registry.Register(handlerRef.MessageType);
}
services.TryAddSingleton<IMessageTypeRegistry>(registry);
```

- [ ] **Step 7: Update `MessageDispatcher.cs` to use registry**

Add `IMessageTypeRegistry typeRegistry` to the primary constructor parameters. Replace the `Type.GetType()` + `IsAssignableFrom` block (lines 38-43) with:

```csharp
if (!typeRegistry.TryResolve(fullTypeName, out var type))
{
    _logger.LogWarning("Unregistered message type '{TypeName}'. Rejecting", fullTypeName);
    return new ConsumeEventResult { Success = false };
}
```

Remove the `typeof(Message).IsAssignableFrom(type)` check — the registry only contains `Message` subtypes by construction.

- [ ] **Step 8: Update `StreamProcessor.cs` to use registry**

Add `IMessageTypeRegistry typeRegistry` to the constructor. Replace the `Type.GetType(fullTypeName!)` call and the `IsAssignableFrom` guard (lines 69-90 in current file) with:

```csharp
if (!typeRegistry.TryResolve(fullTypeName!, out var resolvedType))
{
    _logger.LogWarning("Unregistered type '{TypeName}' for completed stream. Rejecting", fullTypeName);
    return ProcessResult.Handled;
}
```

- [ ] **Step 9: Update `MongoDbAggregatorPersistor.cs` to use registry**

Add `IMessageTypeRegistry typeRegistry` to the constructor. Replace `Type.GetType(doc.DataTypeName)` (line 71) with:

```csharp
if (!typeRegistry.TryResolve(doc.DataTypeName, out var type))
{
    _logger.LogWarning("Cannot resolve type '{TypeName}' for aggregator data", doc.DataTypeName);
    continue;
}
```

- [ ] **Step 10: Update `MessageDispatcherTests` for registry**

In the test setup, create a `MessageTypeRegistry`, register `FakeMessage1` (and any other test message types like `TestMiddlewareMessage`), and pass it to the `MessageDispatcher` constructor. Add a test that verifies unregistered types are rejected:

```csharp
[Fact]
public async Task Dispatch_UnregisteredType_ReturnsFailure()
{
    // Use an empty registry (no types registered)
    var emptyRegistry = new MessageTypeRegistry();
    var dispatcher = new MessageDispatcher(
        _mockSerializer.Object, _mockFilterPipeline.Object, processors,
        NullLogger<MessageDispatcher>.Instance, _mockConfig.Object,
        mockPipelineConfig.Object, testProvider, emptyRegistry);

    var headers = MakeHeaders();
    var result = await dispatcher.Dispatch(new byte[] { 1 }, "test", headers);

    Assert.False(result.Success);
}
```

- [ ] **Step 11: Build and run all tests**

Run: `dotnet build src/ServiceConnect.sln && dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -v q`
Expected: 0 errors, 0 warnings, all tests pass (count will increase with new tests)

- [ ] **Step 12: Commit**

```bash
git add src/ServiceConnect.Interfaces/IMessageTypeRegistry.cs src/ServiceConnect/Services/MessageTypeRegistry.cs src/ServiceConnect.UnitTests/MessageTypeRegistryTests.cs src/ServiceConnect/Services/MessageDispatcher.cs src/ServiceConnect/Services/Processors/StreamProcessor.cs src/ServiceConnect.Persistence.MongoDb/MongoDbAggregatorPersistor.cs src/ServiceConnect/ServiceCollectionExtensions.cs src/ServiceConnect.UnitTests/MessageDispatcherTests.cs
git commit -m "feat: add MessageTypeRegistry to replace Type.GetType on untrusted input"
```

---

### Task 4: IProcessManagerFinder Split

**Files:**
- Create: `src/ServiceConnect.Interfaces/ITimeoutStore.cs`
- Modify: `src/ServiceConnect.Interfaces/IProcessManagerFinder.cs`
- Modify: `src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs`
- Modify: `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceExtensions.cs`
- Modify: `src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs`
- Modify: `src/ServiceConnect.Persistence.MongoDb/MongoDbPersistenceExtensions.cs`
- Modify: `src/ServiceConnect/Services/ProcessManagerTimeoutService.cs`

- [ ] **Step 1: Create `ITimeoutStore.cs`**

```csharp
namespace ServiceConnect.Interfaces;

public interface ITimeoutStore
{
    event TimeoutInsertedDelegate? TimeoutInserted;
    void InsertTimeout(TimeoutData timeoutData);
    TimeoutsBatch GetTimeoutsBatch();
    void RemoveDispatchedTimeout(Guid id);
}
```

- [ ] **Step 2: Remove timeout methods from `IProcessManagerFinder.cs`**

Update to contain only CRUD methods:

```csharp
namespace ServiceConnect.Interfaces;

public interface IProcessManagerFinder
{
    IPersistenceData<T>? FindData<T>(IProcessManagerPropertyMapper mapper, Message message) where T : class, IProcessManagerData;
    void InsertData(IProcessManagerData data);
    void UpdateData<T>(IPersistenceData<T> data) where T : class, IProcessManagerData;
    void DeleteData<T>(IPersistenceData<T> data) where T : class, IProcessManagerData;
}
```

- [ ] **Step 3: Update `InMemoryProcessManagerFinder` to implement both interfaces**

Change class declaration to:
```csharp
public sealed class InMemoryProcessManagerFinder : IProcessManagerFinder, ITimeoutStore
```

No method changes needed — it already implements all methods from both interfaces.

- [ ] **Step 4: Update `MongoDbProcessManagerFinder` to implement both interfaces**

Change class declaration to:
```csharp
public sealed class MongoDbProcessManagerFinder : IProcessManagerFinder, ITimeoutStore
```

No method changes needed.

- [ ] **Step 5: Update `InMemoryPersistenceExtensions.cs` DI registration**

Replace:
```csharp
services.TryAddSingleton<IProcessManagerFinder>(_ =>
    new InMemoryProcessManagerFinder("", ""));
```
With:
```csharp
services.TryAddSingleton<InMemoryProcessManagerFinder>(_ =>
    new InMemoryProcessManagerFinder("", ""));
services.TryAddSingleton<IProcessManagerFinder>(sp =>
    sp.GetRequiredService<InMemoryProcessManagerFinder>());
services.TryAddSingleton<ITimeoutStore>(sp =>
    sp.GetRequiredService<InMemoryProcessManagerFinder>());
```

Add `using Microsoft.Extensions.DependencyInjection;` if not already present (needed for `GetRequiredService`).

- [ ] **Step 6: Update `MongoDbPersistenceExtensions.cs` DI registration**

Replace:
```csharp
services.TryAddSingleton<IProcessManagerFinder, MongoDbProcessManagerFinder>();
```
With:
```csharp
services.TryAddSingleton<MongoDbProcessManagerFinder>();
services.TryAddSingleton<IProcessManagerFinder>(sp =>
    sp.GetRequiredService<MongoDbProcessManagerFinder>());
services.TryAddSingleton<ITimeoutStore>(sp =>
    sp.GetRequiredService<MongoDbProcessManagerFinder>());
```

- [ ] **Step 7: Update `ProcessManagerTimeoutService.cs`**

Change the `_finder` field from `IProcessManagerFinder?` to `ITimeoutStore?`. Update the resolution in `StartAsync` and `PollOnceAsync`:

```csharp
_finder = serviceProvider.GetService<ITimeoutStore>();
```

Update the field declaration and all references. The methods called (`GetTimeoutsBatch`, `RemoveDispatchedTimeout`) are now on `ITimeoutStore`.

- [ ] **Step 8: Build and verify**

Run: `dotnet build src/ServiceConnect.sln`
Expected: 0 errors, 0 warnings

- [ ] **Step 9: Run all tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -v q`
Expected: all pass

- [ ] **Step 10: Commit**

```bash
git add src/ServiceConnect.Interfaces/ITimeoutStore.cs src/ServiceConnect.Interfaces/IProcessManagerFinder.cs src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceExtensions.cs src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs src/ServiceConnect.Persistence.MongoDb/MongoDbPersistenceExtensions.cs src/ServiceConnect/Services/ProcessManagerTimeoutService.cs
git commit -m "refactor: split IProcessManagerFinder into CRUD + ITimeoutStore"
```

---

### Task 5: Publisher Confirms Cleanup

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer.cs`

- [ ] **Step 1: Remove dead publisher-confirm tracking code from `Producer.cs`**

Remove these items:
- The `_messagesSent` field: `private readonly ConcurrentDictionary<ulong, string> _messagesSent = new();`
- The `using System.Collections.Concurrent;` import (if `_messagesSent` was the only user — check first)
- The `CleanOutstandingConfirms` method (lines ~282-294)
- The `WaitForOutstandingConfirms` method (lines ~310-318)
- The `BasicAcksAsync` event handler subscription in `CreateConnection` (lines ~114-118)
- The `BasicNacksAsync` event handler subscription in `CreateConnection` (lines ~119-124)
- The `WaitForOutstandingConfirms()` call in `DisposeAsync` (line ~215)

Keep the `CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true)` — that's the v7 mechanism that actually works.

- [ ] **Step 2: Build and verify**

Run: `dotnet build src/ServiceConnect.sln`
Expected: 0 errors, 0 warnings

- [ ] **Step 3: Run all tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -v q`
Expected: all pass

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Producer.cs
git commit -m "fix: remove dead publisher-confirm tracking code (v7 handles internally)"
```

---

### Task 6: Timer Leak Fix

**Files:**
- Modify: `src/ServiceConnect/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Change DI registration for `StreamProcessor` and `AggregatorProcessor`**

In `ServiceCollectionExtensions.cs`, change lines 34 and 36:

```csharp
// Before:
services.TryAddSingleton<StreamProcessor>();
services.TryAddSingleton<AggregatorProcessor>();

// After:
services.AddSingleton<StreamProcessor>();
services.AddSingleton<AggregatorProcessor>();
```

This ensures the DI container tracks these instances and calls `Dispose()` on shutdown.

- [ ] **Step 2: Build and verify**

Run: `dotnet build src/ServiceConnect.sln`
Expected: 0 errors, 0 warnings

- [ ] **Step 3: Run all tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -v q`
Expected: all pass

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect/ServiceCollectionExtensions.cs
git commit -m "fix: use AddSingleton for IDisposable processors so DI disposes timers"
```

---

### Task 7: Sync-over-Async Elimination

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Retry.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Connection.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Client.cs`

This is the largest task. It has 4 sub-parts that must be done in order (Retry first, then Connection, then Producer, then Client).

- [ ] **Step 1: Rewrite `Retry.cs` to async**

Replace the entire file with:

```csharp
namespace ServiceConnect.Client.RabbitMQ;

public static class Retry
{
    public static async Task DoAsync(Func<Task> action, Func<Exception, Task> exceptionAction, TimeSpan retryInterval, int retryCount)
    {
        List<Exception> exceptions = [];

        for (int retry = 0; retry < retryCount; retry++)
        {
            try
            {
                await action().ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
                try
                {
                    await exceptionAction(ex).ConfigureAwait(false);
                }
                catch (Exception callbackEx)
                {
                    exceptions.Add(callbackEx);
                }
                await Task.Delay(retryInterval).ConfigureAwait(false);
            }
        }

        throw new AggregateException(exceptions);
    }

    public static async Task<T> DoAsync<T>(Func<Task<T>> action, Func<Exception, Task> exceptionAction, TimeSpan retryInterval, int retryCount)
    {
        List<Exception> exceptions = [];

        for (int retry = 0; retry < retryCount; retry++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
                try
                {
                    await exceptionAction(ex).ConfigureAwait(false);
                }
                catch (Exception callbackEx)
                {
                    exceptions.Add(callbackEx);
                }
                await Task.Delay(retryInterval).ConfigureAwait(false);
            }
        }

        throw new AggregateException(exceptions);
    }
}
```

- [ ] **Step 2: Rewrite `Connection.cs` to async**

Update `IServiceConnectConnection` interface: replace `void Connect()` with `Task ConnectAsync()`.

Rewrite connection methods:
- Delete sync `Connect()` method
- Add `ConnectAsync()` using `await _connectionLock.WaitAsync()` and `await CreateConnectionCoreAsync()`
- Rename `CreateConnectionCore()` to `CreateConnectionCoreAsync()` and await the `CreateConnectionAsync` call
- Update `CreateChannelAsync()` to call `await ConnectAsync()` instead of `Connect()`

The `IServiceConnectConnection` interface becomes:
```csharp
public interface IServiceConnectConnection
{
    Task ConnectAsync();
    Task<IChannel> CreateChannelAsync();
    void Dispose();
    bool IsConnected();
}
```

The `ConnectAsync` method:
```csharp
public async Task ConnectAsync()
{
    if (_connection != null) return;

    await _connectionLock.WaitAsync().ConfigureAwait(false);
    try
    {
        if (_connection == null)
            await CreateConnectionCoreAsync().ConfigureAwait(false);
    }
    finally
    {
        _connectionLock.Release();
    }
}
```

The `CreateConnectionCoreAsync` method:
```csharp
private async Task CreateConnectionCoreAsync()
{
    logger.LogDebug("Creating connection to queue {QueueName}", queueName);
    var connectionFactory = BuildConnectionFactory();
    _connection = await connectionFactory.CreateConnectionAsync(_hosts, queueName).ConfigureAwait(false);
}
```

Update `CreateChannelAsync`:
```csharp
public async Task<IChannel> CreateChannelAsync()
{
    if (_connection == null)
        await ConnectAsync().ConfigureAwait(false);

    return await _connection!.CreateChannelAsync().ConfigureAwait(false);
}
```

- [ ] **Step 3: Rewrite `Producer.cs` connection management to async**

Replace `lock(_connectionLock)` / `object _connectionLock` with `SemaphoreSlim`:

Remove:
```csharp
#if NET9_0_OR_GREATER
    private readonly Lock _connectionLock = new();
#else
    private readonly object _connectionLock = new();
#endif
```

Add:
```csharp
    private readonly SemaphoreSlim _connectionSemaphore = new(1, 1);
```

Convert `EnsureConnected()` to `EnsureConnectedAsync()`:
```csharp
private async Task EnsureConnectedAsync()
{
    ObjectDisposedException.ThrowIf(_disposed, this);
    if (_connected) return;

    await _connectionSemaphore.WaitAsync().ConfigureAwait(false);
    try
    {
        if (_connected) return;

        await Retry.DoAsync(CreateConnectionAsync, async ex =>
        {
            _logger.LogError(ex, "Error creating connection");
            await DisposeConnectionAsync().ConfigureAwait(false);
        }, TimeSpan.FromSeconds(_retryTimeInSeconds), _retryCount).ConfigureAwait(false);

        _connected = true;
    }
    finally
    {
        _connectionSemaphore.Release();
    }
}
```

Convert `CreateConnection()` to `CreateConnectionAsync()`:
```csharp
private async Task CreateConnectionAsync()
{
    var port = _transportConfiguration.ClientSettings.TryGetValue(RabbitMQSettingKeys.Port, out var portVal)
        ? Convert.ToInt32(portVal)
        : AmqpTcpEndpoint.UseDefaultPort;

    _connectionFactory = new ConnectionFactory
    {
        VirtualHost = "/",
        Port = port,
        AutomaticRecoveryEnabled = true,
        TopologyRecoveryEnabled = true
    };

    if (!string.IsNullOrEmpty(_transportConfiguration.Username))
        _connectionFactory.UserName = _transportConfiguration.Username;
    if (!string.IsNullOrEmpty(_transportConfiguration.Password))
        _connectionFactory.Password = _transportConfiguration.Password;
    if (_transportConfiguration.SslEnabled)
    {
        _connectionFactory.Ssl = SslConfigurationBuilder.BuildSslOptions(_transportConfiguration);
        _connectionFactory.Port = AmqpTcpEndpoint.DefaultAmqpSslPort;
    }
    if (!string.IsNullOrEmpty(_transportConfiguration.VirtualHost))
        _connectionFactory.VirtualHost = _transportConfiguration.VirtualHost;

    string producerName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name
        ?? System.Diagnostics.Process.GetCurrentProcess().ProcessName;

    _connection = await _connectionFactory.CreateConnectionAsync(_hosts, producerName).ConfigureAwait(false);

    if (_publisherAcks)
    {
        var channelOptions = new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true);
        _model = await _connection.CreateChannelAsync(channelOptions).ConfigureAwait(false);
    }
    else
    {
        _model = await _connection.CreateChannelAsync().ConfigureAwait(false);
    }
}
```

Update all public methods to call `await EnsureConnectedAsync()`:
- `PublishAsync`: `EnsureConnected()` → `await EnsureConnectedAsync().ConfigureAwait(false)`
- `SendAsync` (both overloads): same
- `SendBytesAsync`: same

Convert `DisposeConnection()` to `DisposeConnectionAsync()`:
```csharp
private async Task DisposeConnectionAsync()
{
    await _connectionSemaphore.WaitAsync().ConfigureAwait(false);
    try
    {
        try
        {
            if (_connection != null && _connection.IsOpen)
            {
                await _connection.CloseAsync().ConfigureAwait(false);
                _connection.Dispose();
                _connection = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception trying to close connection");
        }

        try
        {
            if (_model != null && _model.IsOpen)
            {
                await _model.CloseAsync().ConfigureAwait(false);
                _model.Dispose();
                _model = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception trying to close model");
        }

        _connected = false;
    }
    finally
    {
        _connectionSemaphore.Release();
    }
}
```

Update `Dispose()` to fire-and-forget:
```csharp
public void Dispose()
{
    if (_disposed) return;
    _disposed = true;
    _ = Task.Run(async () =>
    {
        try { await DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Error during fire-and-forget dispose"); }
    });
}
```

Update `DisposeAsync()`:
```csharp
public async ValueTask DisposeAsync()
{
    await _connectionSemaphore.WaitAsync().ConfigureAwait(false);
    try
    {
        if (_disposed) return;
        _disposed = true;
    }
    finally
    {
        _connectionSemaphore.Release();
    }

    await DisposeModelAsync().ConfigureAwait(false);
    await DisposeConnectionInstanceAsync().ConfigureAwait(false);
}
```

- [ ] **Step 4: Add `IAsyncDisposable` to `Client.cs`**

Add `IAsyncDisposable` to the class declaration:
```csharp
public sealed class Client : IDisposable, IAsyncDisposable
```

Add `DisposeAsync`:
```csharp
public async ValueTask DisposeAsync()
{
    var deadline = Environment.TickCount64 + 5000;
    var wait = new SpinWait();
    while (Volatile.Read(ref _messagesBeingProcessed) > 0 && Environment.TickCount64 < deadline)
    {
        wait.SpinOnce();
    }

    if (_autoDelete && _model != null)
    {
        try
        {
            _logger.LogDebug("Deleting retry queue");
            await _model.QueueDeleteAsync(_queueName + ".Retries").ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error deleting retry queue");
        }
    }

    _model = null;
}
```

Update sync `Dispose()` to fire-and-forget:
```csharp
public void Dispose()
{
    var deadline = Environment.TickCount64 + 5000;
    var wait = new SpinWait();
    while (Volatile.Read(ref _messagesBeingProcessed) > 0 && Environment.TickCount64 < deadline)
    {
        wait.SpinOnce();
    }

    // Fire-and-forget async cleanup to avoid deadlock in consumer callback chain
    if (_autoDelete && _model != null)
    {
        var model = _model;
        var queueName = _queueName;
        _ = Task.Run(async () =>
        {
            try { await model.QueueDeleteAsync(queueName + ".Retries").ConfigureAwait(false); }
            catch { }
        });
    }

    _model = null;
}
```

- [ ] **Step 5: Build and verify**

Run: `dotnet build src/ServiceConnect.sln`
Expected: 0 errors, 0 warnings

- [ ] **Step 6: Run unit tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -v q`
Expected: all pass

- [ ] **Step 7: Run E2E tests**

Run: `sg docker "dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj -v q"`
Expected: 71 passed (these exercise the full transport layer)

- [ ] **Step 8: Verify no remaining sync-over-async**

Run: `grep -rn "GetAwaiter().GetResult()\|Thread\.Sleep" src/ServiceConnect.Client.RabbitMQ/ --include="*.cs"`
Expected: no matches

- [ ] **Step 9: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Retry.cs src/ServiceConnect.Client.RabbitMQ/Connection.cs src/ServiceConnect.Client.RabbitMQ/Producer.cs src/ServiceConnect.Client.RabbitMQ/Client.cs
git commit -m "fix: eliminate all sync-over-async in RabbitMQ transport layer"
```
