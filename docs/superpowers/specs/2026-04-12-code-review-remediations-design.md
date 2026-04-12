# Code Review Remediations — Design Spec

**Date:** 2026-04-12
**Scope:** 7 improvements identified during code review, ranging from security hardening to async correctness.

## Execution Order

Items are ordered to minimize merge conflicts and build on prior changes:

1. RabbitMQ settings constants (zero-risk, cleans up files touched later)
2. IMessageDispatcher interface (decouples Bus before dispatcher changes)
3. Message type registry (touches dispatcher, now behind interface)
4. IProcessManagerFinder split (independent, interface-level change)
5. Publisher confirms cleanup (contained to Producer.cs, before async refactor)
6. Timer leak fix (one-line DI registration changes)
7. Sync-over-async elimination (largest change, benefits from all prior cleanup)

---

## 1. RabbitMQ Settings Constants

**Problem:** 15 magic string keys (`"Durable"`, `"Exclusive"`, `"Port"`, etc.) are repeated across `Producer.cs`, `Connection.cs`, `Client.cs`, and `Consumer.cs`. Misspelling a key is a silent runtime misconfiguration.

**Change:** Create `src/ServiceConnect.Client.RabbitMQ/RabbitMQSettingKeys.cs`:

```csharp
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

Replace all bare string literals in `Producer.cs` (lines 42-47), `Connection.cs` (lines 12-13), `Client.cs` (lines 44-48), and `Consumer.cs` (lines 49-54) with these constants.

**Files changed:** 5 (1 new, 4 modified)

---

## 2. IMessageDispatcher Interface

**Problem:** `Bus` depends on concrete `MessageDispatcher`, preventing mocking and alternative implementations.

**Change:**

Create `src/ServiceConnect.Interfaces/IMessageDispatcher.cs`:
```csharp
public interface IMessageDispatcher
{
    Task<ConsumeEventResult> Dispatch(byte[] messageBytes, string messageType, IDictionary<string, object> headers);
}
```

- `MessageDispatcher` implements `IMessageDispatcher`
- `Bus` constructor takes `IMessageDispatcher` instead of `MessageDispatcher`
- DI registration: `services.TryAddSingleton<IMessageDispatcher, MessageDispatcher>()`
- Update `BusTests` to mock `IMessageDispatcher` instead of constructing concrete dispatcher

**Files changed:** 4 (`IMessageDispatcher.cs` new, `MessageDispatcher.cs`, `Bus.cs`, `ServiceCollectionExtensions.cs` modified) + test updates

---

## 3. Message Type Registry

**Problem:** `Type.GetType()` is called on untrusted wire data (message headers, DB fields) at 3 call sites. An attacker who can publish messages can trigger loading of arbitrary CLR types.

**Change:**

Create `src/ServiceConnect.Interfaces/IMessageTypeRegistry.cs`:
```csharp
public interface IMessageTypeRegistry
{
    bool TryResolve(string assemblyQualifiedName, out Type type);
    void Register(Type type);
}
```

Create `src/ServiceConnect/Services/MessageTypeRegistry.cs`:
```csharp
public sealed class MessageTypeRegistry : IMessageTypeRegistry
{
    private readonly ConcurrentDictionary<string, Type> _types = new();

    public bool TryResolve(string assemblyQualifiedName, out Type type)
        => _types.TryGetValue(assemblyQualifiedName, out type!);

    public void Register(Type type)
    {
        if (type.AssemblyQualifiedName is not null)
            _types[type.AssemblyQualifiedName] = type;
        if (type.FullName is not null)
            _types[type.FullName] = type;
    }
}
```

Types are keyed on both `AssemblyQualifiedName` and `FullName` since wire headers may contain either form.

**Population at startup** (`ServiceCollectionExtensions.AddServiceConnect`):
After `HandlerScanner.ScanForHandlers` runs, iterate all `HandlerReference.MessageType` values and call `registry.Register()` for each. Register as singleton.

**Integration points** (all replace `Type.GetType()` with `registry.TryResolve()`):
- `MessageDispatcher.Dispatch` — inject `IMessageTypeRegistry`. Replace `Type.GetType(fullTypeName)` + `IsAssignableFrom` guard with `registry.TryResolve()`. Rejection returns `ConsumeEventResult { Success = false }` with a log warning.
- `StreamProcessor.ProcessAsync` — inject `IMessageTypeRegistry`. Replace `Type.GetType()` + `IsAssignableFrom` guard with `registry.TryResolve()`. Rejection returns `ProcessResult.Handled` with a log warning.
- `MongoDbAggregatorPersistor.GetData` — inject `IMessageTypeRegistry`. Replace `Type.GetType(doc.DataTypeName)` with `registry.TryResolve()`. Unknown types are skipped with a log warning (existing behavior).

**Strict mode:** Unknown types are rejected entirely. No fallback to `Type.GetType()`.

**Files changed:** 6 (2 new, 4 modified) + test updates

---

## 4. IProcessManagerFinder Split

**Problem:** `IProcessManagerFinder` is a fat interface mixing CRUD (4 methods) with timeout management (3 methods + 1 event). Consumers that only need one concern are forced to depend on both.

**Change:**

Split into two interfaces in `ServiceConnect.Interfaces`:

**`IProcessManagerFinder`** (keeps existing name, CRUD only):
```csharp
public interface IProcessManagerFinder
{
    IPersistenceData<T>? FindData<T>(IProcessManagerPropertyMapper mapper, Message message) where T : class, IProcessManagerData;
    void InsertData(IProcessManagerData data);
    void UpdateData<T>(IPersistenceData<T> data) where T : class, IProcessManagerData;
    void DeleteData<T>(IPersistenceData<T> data) where T : class, IProcessManagerData;
}
```

**`ITimeoutStore`** (new):
```csharp
public interface ITimeoutStore
{
    event TimeoutInsertedDelegate? TimeoutInserted;
    void InsertTimeout(TimeoutData timeoutData);
    TimeoutsBatch GetTimeoutsBatch();
    void RemoveDispatchedTimeout(Guid id);
}
```

**Implementations:** Both `MongoDbProcessManagerFinder` and `InMemoryProcessManagerFinder` implement both interfaces. No class splitting.

**DI registration** (in each persistence extension method):
```csharp
services.AddSingleton<MongoDbProcessManagerFinder>();
services.AddSingleton<IProcessManagerFinder>(sp => sp.GetRequiredService<MongoDbProcessManagerFinder>());
services.AddSingleton<ITimeoutStore>(sp => sp.GetRequiredService<MongoDbProcessManagerFinder>());
```

**Consumer changes:**
- `ProcessManagerProcessor`: no change (only uses CRUD methods via `IProcessManagerFinder`)
- `ProcessManagerTimeoutService`: resolve `ITimeoutStore` instead of `IProcessManagerFinder`
- User code that calls `InsertTimeout` must resolve `ITimeoutStore` instead of `IProcessManagerFinder` (**breaking change**)

**Files changed:** 5 (1 new `ITimeoutStore.cs`, `IProcessManagerFinder.cs` modified, both implementations modified, `ProcessManagerTimeoutService.cs` modified) + persistence extension methods + test updates

---

## 5. Publisher Confirms Cleanup

**Problem:** `Producer._messagesSent` dictionary is never populated. The `CleanOutstandingConfirms`, `WaitForOutstandingConfirms`, and `BasicAcksAsync`/`BasicNacksAsync` handlers are dead code. Publisher confirms appear to work but the tracking layer is a no-op.

**Root cause:** RabbitMQ v7 with `publisherConfirmationTrackingEnabled: true` handles confirm tracking internally. `BasicPublishAsync` only returns when the broker confirms. The manual tracking layer was never migrated from v6.

**Change:** Remove from `Producer.cs`:
- `_messagesSent` field (ConcurrentDictionary)
- `CleanOutstandingConfirms` method
- `WaitForOutstandingConfirms` method
- `BasicAcksAsync` event handler subscription
- `BasicNacksAsync` event handler subscription
- `WaitForOutstandingConfirms()` call in `DisposeAsync()`

Publisher confirms continue to work correctly via the v7 client's built-in tracking.

**Files changed:** 1 (`Producer.cs`)

---

## 6. Timer Leak Fix

**Problem:** `AggregatorProcessor` and `StreamProcessor` implement `IDisposable` but are registered with `TryAddSingleton<T>()`. The built-in DI container only tracks and disposes singletons registered through `AddSingleton<T>()`.

**Change** in `ServiceCollectionExtensions.cs`:
```csharp
// Before:
services.TryAddSingleton<StreamProcessor>();
services.TryAddSingleton<AggregatorProcessor>();

// After:
services.AddSingleton<StreamProcessor>();
services.AddSingleton<AggregatorProcessor>();
```

The container will now call `Dispose()` on both during shutdown, which disposes their internal timers.

**Files changed:** 1 (`ServiceCollectionExtensions.cs`)

---

## 7. Sync-over-Async Elimination

**Problem:** 8 `.GetAwaiter().GetResult()` calls and 2 `Thread.Sleep()` calls block thread-pool threads and risk deadlocks. All are in the RabbitMQ transport layer.

### 7a. Retry.cs

Replace sync methods with async:
- Delete `Do(Action, Action<Exception>, TimeSpan, int)`
- Delete `Do<T>(Func<T>, Action<Exception>, TimeSpan, int)`
- Add `DoAsync(Func<Task>, Func<Exception, Task>, TimeSpan, int)` using `await Task.Delay()`
- Add `DoAsync<T>(Func<Task<T>>, Func<Exception, Task>, TimeSpan, int)` using `await Task.Delay()`

### 7b. Connection.cs

- Delete sync `Connect()` from class
- Rename/replace with `ConnectAsync()`: uses `await _connectionLock.WaitAsync()` and `await CreateConnectionCoreAsync()`
- `CreateConnectionCore()` → `CreateConnectionCoreAsync()`: `await connectionFactory.CreateConnectionAsync()`
- `CreateChannelAsync()` calls `await ConnectAsync()` instead of sync `Connect()`
- Update `IServiceConnectConnection`: replace `void Connect()` with `Task ConnectAsync()`

### 7c. Producer.cs

- `EnsureConnected()` → `EnsureConnectedAsync()`:
  - Replace `lock(_connectionLock)` with `SemaphoreSlim` (`await _semaphore.WaitAsync()`)
  - Calls `await Retry.DoAsync(CreateConnectionAsync, ...)`
- `CreateConnection()` → `CreateConnectionAsync()`:
  - `await _connectionFactory.CreateConnectionAsync()`
  - `await _connection.CreateChannelAsync()`
- `DisposeConnection()` → `DisposeConnectionAsync()`:
  - `await _connection.CloseAsync()`
  - `await _model.CloseAsync()`
- `Dispose()`: fire-and-forget `DisposeAsync()` via `Task.Run` (matching Connection.Dispose pattern) since sync callers can't await
- All public methods (`PublishAsync`, `SendAsync`, `SendBytesAsync`) call `await EnsureConnectedAsync()`
- Replace `_connectionLock` (currently `object`/`Lock`) with a new `SemaphoreSlim(1,1)` named `_connectionSemaphore` for async-safe locking. The existing `_publishLock` SemaphoreSlim for publish serialization is unchanged.

### 7d. Client.cs

- Implement `IAsyncDisposable`
- `DisposeAsync()`: await retry queue deletion, then null the model
- `Dispose()`: fire-and-forget `DisposeAsync()` for sync callers (spin-wait for in-flight messages retained)
- `Consumer.Dispose()` continues to call `Client.Dispose()` (sync path)

### Summary of deleted sync-over-async calls

| File | Call | Replacement |
|------|------|-------------|
| `Connection.cs:37` | `CreateConnectionAsync().GetAwaiter().GetResult()` | `await CreateConnectionAsync()` |
| `Producer.cs:106` | `CreateConnectionAsync().GetAwaiter().GetResult()` | `await CreateConnectionAsync()` |
| `Producer.cs:113` | `CreateChannelAsync().GetAwaiter().GetResult()` | `await CreateChannelAsync()` |
| `Producer.cs:128` | `CreateChannelAsync().GetAwaiter().GetResult()` | `await CreateChannelAsync()` |
| `Producer.cs:204` | `DisposeAsync().AsTask().GetAwaiter().GetResult()` | `Task.Run(() => DisposeAsync())` fire-and-forget |
| `Producer.cs:369` | `CloseAsync().GetAwaiter().GetResult()` | `await CloseAsync()` |
| `Producer.cs:383` | `CloseAsync().GetAwaiter().GetResult()` | `await CloseAsync()` |
| `Client.cs:282` | `QueueDeleteAsync().GetAwaiter().GetResult()` | `await QueueDeleteAsync()` in `DisposeAsync` |
| `Retry.cs:27` | `Thread.Sleep(retryInterval)` | `await Task.Delay(retryInterval)` |
| `Retry.cs:55` | `Thread.Sleep(retryInterval)` | `await Task.Delay(retryInterval)` |

**Files changed:** 4 (`Retry.cs`, `Connection.cs`, `Producer.cs`, `Client.cs`) + `IServiceConnectConnection` interface

---

## Testing Strategy

Each item has its own test scope:

1. **Settings constants:** Build-only verification (compile-time safety). No new tests needed.
2. **IMessageDispatcher:** Update `BusTests` to mock interface. Existing dispatcher tests unchanged.
3. **Type registry:** New unit tests for `MessageTypeRegistry`. Update `MessageDispatcherTests` to verify rejection of unregistered types. Update `StreamProcessorTests` similarly.
4. **Finder split:** Update existing finder tests to verify both interfaces. Update `ProcessManagerTimeoutService` tests to use `ITimeoutStore`.
5. **Publisher confirms:** Remove dead confirm-tracking test assertions. Existing E2E tests validate publish still works.
6. **Timer leak:** Verify via DI container test that processors are disposed on shutdown.
7. **Async refactor:** Existing E2E tests are the primary validation (71 tests cover all transport paths). Update unit tests where method signatures changed.

## Breaking Changes

- `IProcessManagerFinder` no longer contains timeout methods. Code that resolves `IProcessManagerFinder` to call `InsertTimeout`/`GetTimeoutsBatch`/`RemoveDispatchedTimeout` must resolve `ITimeoutStore` instead.
- `IServiceConnectConnection.Connect()` replaced with `ConnectAsync()`. Affects anyone implementing a custom connection (internal interface, low risk).
- `Retry.Do`/`Retry.Do<T>` sync methods removed. Replaced by `DoAsync`/`DoAsync<T>`.
