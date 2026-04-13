# CancellationToken End-to-End + Async Persistence + R-034 Race Fix -- Design Spec

**Date:** 2026-04-13
**Scope:** R-016 / B-01 (CancellationToken on IBus async methods), R-034 (race condition in `Bus.StartConsumingAsync`), plus full async migration of core persistence interfaces
**Breaking changes:** Yes -- all IBus async signatures change, transport/processor/middleware/context/persistence interfaces change. Major version bump warranted.

## Problem

ServiceConnect has four related gaps that should be fixed together:

1. **R-016 / B-01 -- No CancellationToken on public APIs.** `IBus` and all internal async interfaces (`IConsumer`, `IProducer`, `IConsumeContext`, `IMessageProcessor`, middleware delegates) have no way to be cancelled. Callers cannot abort an in-flight publish, send, request, or consume-loop start. `BusHostedService.StopAsync(CancellationToken)` accepts a token but drops it on the floor.

2. **R-034 -- Race in `Bus.StartConsumingAsync` / `StopConsumingAsync`.** The state lock is released before the long-running `_consumer.StartConsumingAsync()` await, so concurrent Start/Stop can set `_consuming` inconsistently. The intent of releasing the lock was deadlock avoidance, but the price is an incorrect lifecycle state machine.

3. **Sync persistence under async dispatch.** `IProcessManagerFinder`, `IAggregatorPersistor`, `ITimeoutStore` are fully synchronous. MongoDB implementations block the async dispatch thread with sync-over-async calls. A cancellation token threaded through the framework cannot reach persistence without making these interfaces async.

4. **RequestReplyManager has internal timeout but no external cancel.** `SendRequestAsync` already creates a `CancellationTokenSource(options.Timeout)` internally but ignores caller cancellation.

Fixing these together is the right unit of work: they all sit on the same breaking-change boundary, and a CT that stops at processors (without reaching persistence) is a leaky abstraction.

### Explicitly out of scope

The following issues touch overlapping code but are deferred to a later Group C spec:

- **R-017 / R-018** (silent exception swallowing in dedup filter persistors) -- requires `IFilter.Process` to become async, which cascades into `IMessageDeduplicationPersistor`. The filter persistor stays synchronous here.
- **R-009** (service locator anti-pattern) -- independent DI migration.
- **R-020 / R-021** (ProcessManagerProcessor / Client SRP) -- independent structural refactor.
- **R-032** (settings singleton) -- depends on R-009.

## Design

### 1. CancellationToken parameter convention

Every async method on a public or internal framework interface gains `CancellationToken cancellationToken = default` as its **last** parameter. This matches .NET BCL style (`HttpClient`, `Stream`, `DbCommand`, MongoDB.Driver) and keeps existing call sites source-compatible.

Interface changes are still breaking for anyone who *implements* these interfaces (custom transports, custom persistors) -- this is accepted as a major version bump.

### 2. Interface surface

#### 2.1 IBus

```csharp
Task PublishAsync<T>(T message, PublishOptions? options = null, CancellationToken cancellationToken = default);
Task SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default);
Task<TReply> SendRequestAsync<T, TReply>(T message, RequestOptions? options = null, CancellationToken cancellationToken = default);
Task<IList<TReply>> SendRequestMultiAsync<T, TReply>(T message, RequestOptions? options = null, CancellationToken cancellationToken = default);
Task PublishRequestAsync<TRequest, TReply>(TRequest message, Action<TReply> onReply, RequestOptions? options = null, CancellationToken cancellationToken = default);
Task RouteAsync<T>(T message, IList<string> destinations, CancellationToken cancellationToken = default);
Task StartConsumingAsync(CancellationToken cancellationToken = default);
Task StopConsumingAsync(CancellationToken cancellationToken = default);
```

#### 2.2 IConsumeContext

Adds a `CancellationToken` property. `ReplyAsync` gains CT. Existing implementers (`ConsumeContext`) must set the property when constructed by processors.

```csharp
public interface IConsumeContext
{
    IBus? Bus { get; set; }
    IDictionary<string, object>? Headers { get; set; }
    CancellationToken CancellationToken { get; set; }   // NEW

    Task ReplyAsync<TReply>(TReply reply, CancellationToken cancellationToken = default);
    // ...
}
```

**Handler impact:** zero. User handlers reach CT via `Context.CancellationToken`. `IMessageHandler<T>.HandleAsync(TMessage message)` signature is unchanged.

#### 2.3 IConsumer, IProducer

```csharp
// IConsumer
Task StartConsumingAsync(string queueName, IList<string> messageTypes, ConsumerEventHandler eventHandler, CancellationToken cancellationToken = default);

// IProducer
Task PublishAsync(Type type, byte[] message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default);
Task SendAsync(Type type, byte[] message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default);
Task SendAsync(string endPoint, Type type, byte[] message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default);
Task SendBytesAsync(string endPoint, byte[] packet, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default);
Task DisconnectAsync(CancellationToken cancellationToken = default);
```

#### 2.4 IMessageProcessor and implementations

```csharp
public interface IMessageProcessor
{
    Task<ProcessResult> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default);
}
```

Implementations (`HandlerProcessor`, `ProcessManagerProcessor`, `AggregatorProcessor`, `StreamProcessor`) thread CT into:
- `IConsumeContext.CancellationToken` before invoking the user handler
- Persistence calls (new async persistor methods)

`ProcessResult` is **not extended** with a cancellation outcome. `OperationCanceledException` propagates.

#### 2.5 Middleware delegate signatures

```csharp
// IMessageProcessingMiddleware
public delegate Task<ConsumeEventResult> MessageProcessingDelegate(
    byte[] messageBytes, Type messageType, object message,
    IDictionary<string, object> headers, Envelope envelope,
    CancellationToken cancellationToken);   // NEW

// ISendMessageMiddleware
public delegate Task SendMessageDelegate(
    Type typeObject, byte[] messageBytes,
    Dictionary<string, string> headers, string? endPoint = null,
    CancellationToken cancellationToken = default);   // NEW
```

User-written middleware must be updated to accept and forward the token.

#### 2.6 IRequestReplyManager

```csharp
Task<TReply> SendRequestAsync<TRequest, TReply>(..., CancellationToken cancellationToken = default);
Task<IList<TReply>> SendRequestMultiAsync<TRequest, TReply>(..., CancellationToken cancellationToken = default);
```

Internally, both methods build a linked CTS:

```csharp
using var timeoutCts = new CancellationTokenSource(options.Timeout);
using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
linkedCts.Token.Register(() =>
{
    if (cancellationToken.IsCancellationRequested)
        tcs.TrySetCanceled(cancellationToken);
    else
        tcs.TrySetException(new RequestTimeoutException(messageId, options.Timeout));
});
```

Existing `RequestTimeoutException` behavior is preserved for timeout; `OperationCanceledException` is thrown for external cancellation.

### 3. Bus lifecycle serialization (R-034 fix)

Add a `SemaphoreSlim _lifecycleSemaphore = new(1, 1)` to `Bus`. The existing `_stateLock` stays for fast synchronous reads of `_consuming`.

```csharp
public async Task StartConsumingAsync(CancellationToken cancellationToken = default)
{
    await _lifecycleSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
        IConsumer localConsumer;
        List<string> messageTypeNames;
        lock (_stateLock)
        {
            if (_consumer == null)
                throw new InvalidOperationException("Consumer not initialized.");
            messageTypeNames = [.. _handlerReferences.Select(h => _messageTypeRegistry.GetTypeName(h.MessageType))];
            localConsumer = _consumer;
        }

        _logger.LogInformation("Starting consumer...");
        await localConsumer.StartConsumingAsync(_configuration.TransportSettings.QueueName, messageTypeNames, ConsumeMessageEvent, cancellationToken).ConfigureAwait(false);

        lock (_stateLock) { _consuming = true; }
    }
    finally
    {
        _lifecycleSemaphore.Release();
    }
}

public async Task StopConsumingAsync(CancellationToken cancellationToken = default)
{
    await _lifecycleSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
        IConsumer? localConsumer;
        lock (_stateLock) { localConsumer = _consumer; }

        if (localConsumer != null)
            await localConsumer.DisposeAsync().ConfigureAwait(false);

        lock (_stateLock) { _consuming = false; }
    }
    finally
    {
        _lifecycleSemaphore.Release();
    }
}
```

Dispose path releases the semaphore: `_lifecycleSemaphore.Dispose()` in `DisposeAsync`. Concurrent Start/Stop is serialized; `WaitAsync(ct)` respects the caller's token so callers can abort their wait without affecting the in-flight operation holding the semaphore.

### 4. Persistence async migration

Three interfaces convert to fully async. Method names gain `Async` suffix; all return `Task`/`Task<T>` and accept `CancellationToken`.

#### 4.1 IProcessManagerFinder

```csharp
public interface IProcessManagerFinder
{
    Task<IPersistanceData<T>?> FindDataAsync<T>(IProcessManagerPropertyMapper mapper, Message message, CancellationToken cancellationToken = default) where T : class, IProcessManagerData;
    Task InsertDataAsync(IProcessManagerData data, CancellationToken cancellationToken = default);
    Task UpdateDataAsync<T>(IPersistanceData<T> data, CancellationToken cancellationToken = default) where T : class, IProcessManagerData;
    Task DeleteDataAsync<T>(IPersistanceData<T> data, CancellationToken cancellationToken = default) where T : class, IProcessManagerData;
}
```

Implementations:
- `InMemoryProcessManagerFinder`: wrap existing sync bodies in `Task.FromResult`; call `cancellationToken.ThrowIfCancellationRequested()` at method entry.
- `MongoDbProcessManagerFinder`: switch to driver-native async methods (`FindAsync`, `InsertOneAsync`, `UpdateOneAsync`, `DeleteOneAsync`); pass CT directly to driver.

#### 4.2 IAggregatorPersistor

```csharp
public interface IAggregatorPersistor
{
    Task InsertDataAsync(object data, string name, CancellationToken cancellationToken = default);
    Task<IList<object>> GetDataAsync(string name, CancellationToken cancellationToken = default);
    Task RemoveDataAsync(string name, CancellationToken cancellationToken = default);
    Task<int> CountAsync(string name, CancellationToken cancellationToken = default);
}
```

Same pattern as above. InMemory wraps sync with `Task.FromResult`; MongoDb uses driver async.

#### 4.3 ITimeoutStore

```csharp
public interface ITimeoutStore
{
    event TimeoutInsertedDelegate? TimeoutInserted;
    Task InsertTimeoutAsync(TimeoutData timeoutData, CancellationToken cancellationToken = default);
    Task<TimeoutsBatch> GetTimeoutsBatchAsync(CancellationToken cancellationToken = default);
    Task RemoveDispatchedTimeoutAsync(Guid id, CancellationToken cancellationToken = default);
}
```

The `TimeoutInserted` event stays synchronous -- event handlers can dispatch async work themselves. Return type preserves existing `TimeoutsBatch`; `GetTimeoutsBatch` takes no parameters today, matching the current interface.

**Implementation note:** `ITimeoutStore` is implemented on the same class as `IProcessManagerFinder` -- both `InMemoryProcessManagerFinder` and `MongoDbProcessManagerFinder` implement both interfaces. Update the single class per backend, not two.

`ProcessManagerTimeoutService` (BackgroundService) already receives a CT from `StartAsync` -- thread it into the new async store calls.

#### 4.4 Processor updates

Processors that call persistors (`ProcessManagerProcessor`, `AggregatorProcessor`) update their `ProcessAsync` body to `await` the new async methods and forward the CT. `HandlerProcessor` doesn't touch persistence. `StreamProcessor` doesn't touch persistence.

### 5. Error handling

- **`OperationCanceledException`** -- propagates untouched through processors, `MessageDispatcher`, middleware chain, and the consumer's message callback. The consumer top-level catch (RabbitMQ `Consumer.ConsumeMessageEvent`) already handles exceptions by nacking the message; it must distinguish OCE during shutdown (expected) from OCE mid-message (treat as a normal exception -- nack and requeue).
- **`RequestTimeoutException`** -- preserved by distinguishing which token fired the linked CTS in `RequestReplyManager`.
- **All other exceptions** -- unchanged. Existing retry logic, error queue routing, filter chain behavior is untouched.
- **Persistence OCE** -- MongoDB driver throws `OperationCanceledException` when its CT fires; this is the desired behavior. Let it propagate.

### 6. RabbitMQ client implementation

`Consumer.cs`:
- `StartConsumingAsync(..., CancellationToken ct)`: store `ct` on the instance. RabbitMQ's `BasicConsumeAsync` already accepts a CT -- pass through. The internal dispatch loop checks the token periodically.

`Producer.cs`:
- All methods accept CT and forward to RabbitMQ client async calls where the driver supports it. Where the driver does not (channel operations are still largely sync-over-async in older .NET RabbitMQ.Client versions), the CT is honored at the entry point via `ThrowIfCancellationRequested`.

## Data flow

### Ingress (message dispatch)

```
Consumer loop
  └── lifecycle CT (Consumer-owned)
      └── MessageDispatcher.Dispatch(envelope, ct)
          └── MessageProcessingDelegate chain (ct threaded per middleware)
              └── IMessageProcessor.ProcessAsync(envelope, ct)
                  ├── HandlerProcessor: ctx.CancellationToken = ct; handler.HandleAsync(msg)
                  ├── ProcessManagerProcessor: finder.*Async(..., ct); ctx.CancellationToken = ct
                  ├── AggregatorProcessor: persistor.*Async(..., ct); ctx.CancellationToken = ct
                  └── StreamProcessor: ctx.CancellationToken = ct
```

### Egress (send/publish)

```
Bus.PublishAsync(msg, ct)
  └── SendMessagePipeline.ExecutePublishMessagePipelineAsync(..., ct)
      └── SendMessageDelegate chain (ct per middleware)
          └── IProducer.PublishAsync(..., ct) → RabbitMQ publish
```

### Lifecycle (R-034 fix)

```
bus.StartConsumingAsync(ct) ─┐
bus.StopConsumingAsync(ct)  ─┴─► _lifecycleSemaphore.WaitAsync(ct) serializes both
```

### Request/reply

```
Bus.SendRequestAsync(msg, options, ct)
  └── RequestReplyManager.SendRequestAsync
      ├── timeoutCts = new CancellationTokenSource(options.Timeout)
      ├── linkedCts = CreateLinkedTokenSource(ct, timeoutCts.Token)
      └── await reply TCS or linkedCts firing
          ├── External ct fired → throw OperationCanceledException(ct)
          ├── timeoutCts fired → throw RequestTimeoutException(messageId, timeout)
          └── Reply arrived → return reply
```

## Testing

### Unit (new)

- `BusTests.StartConsumingAsync_SerializesWithStop_NoRace` -- fire concurrent Start+Stop many times; assert consistent final `_consuming` state.
- `BusTests.StartConsumingAsync_PreCancelled_ThrowsOCE_WithoutInvokingConsumer` -- pass `new CancellationToken(canceled: true)`; mock consumer assertion.
- `BusTests.StopConsumingAsync_WhileStartInFlight_WaitsForStartToComplete` -- stagger Start/Stop; assert Stop blocks until Start's semaphore release.
- `RequestReplyManagerTests.ExternalCancellation_ThrowsOCE_NotTimeout` -- fire external CT before timeout; assert `OperationCanceledException`.
- `RequestReplyManagerTests.Timeout_BeforeExternalCancel_ThrowsRequestTimeoutException` -- preserve existing.
- `RequestReplyManagerTests.ReplyReceived_BeforeCancelOrTimeout_ReturnsReply` -- preserve existing.
- `InMemoryProcessManagerFinderTests.PreCancelledToken_Throws` -- for each async method on both `IProcessManagerFinder` and `ITimeoutStore` (same class).
- `InMemoryAggregatorPersistorTests.PreCancelledToken_Throws` -- for each async method.

### Unit (regression)

- Existing `BusTests`, `RequestReplyManagerTests`, `IncomingFilterTests`, `OutgoingFilterTests`, `PersistorFactoryTests` pass with default CT.
- `InMemoryProcessManagerFinder` tests pass against new async interface (sync-to-async wrapper).

### E2E (new)

- `CancellationE2ETests.CancelDuringStartConsuming_ShutsDownCleanly` -- start consuming with a CT, cancel before connection ready, assert no hung threads/tasks.
- `CancellationE2ETests.CancelInFlightSendRequest_ThrowsOCE_NoLeakedReplySlot` -- start a request that won't be replied to; cancel; assert OCE and assert `RequestReplyManager` internal pending-request dict is empty.

### E2E (regression)

- All 71 existing E2E tests pass with no caller-supplied CT (they don't pass one -- use defaults).
- MongoDB process manager and aggregator E2E tests pass after persistor async migration.

## Breaking changes

| Change | Migration |
|---|---|
| `IBus` async methods now accept CT (optional) | Existing call sites compile. Custom `IBus` implementers must add the parameter. |
| `IConsumer.StartConsumingAsync` signature changed | Custom transports must add CT. |
| `IProducer` all async methods changed | Custom transports must add CT. |
| `IMessageProcessor.ProcessAsync` changed | Custom processors (rare) must add CT. |
| `IMessageProcessingMiddleware` / `ISendMessageMiddleware` delegate types changed | User middleware must accept and forward CT. |
| `IConsumeContext` gains `CancellationToken` property | Implementers (usually internal) must add. User handlers unaffected. |
| `IProcessManagerFinder`, `IAggregatorPersistor`, `ITimeoutStore` fully converted to async | Custom persistors must rewrite. |

User message handlers (`IMessageHandler<T>`, `IStreamHandler<T>`, `Aggregator<T>`) are **unchanged**.

## File changes summary

| Action | File |
|---|---|
| Modify | `src/ServiceConnect.Interfaces/IBus.cs` |
| Modify | `src/ServiceConnect.Interfaces/IConsumer.cs` |
| Modify | `src/ServiceConnect.Interfaces/IProducer.cs` |
| Modify | `src/ServiceConnect.Interfaces/IConsumeContext.cs` |
| Modify | `src/ServiceConnect.Interfaces/IMessageProcessor.cs` |
| Modify | `src/ServiceConnect.Interfaces/IMessageProcessingMiddleware.cs` |
| Modify | `src/ServiceConnect.Interfaces/ISendMessageMiddleware.cs` |
| Modify | `src/ServiceConnect.Interfaces/IRequestReplyManager.cs` |
| Modify | `src/ServiceConnect.Interfaces/IProcessManagerFinder.cs` (rename to async) |
| Modify | `src/ServiceConnect.Interfaces/IAggregatorPersistor.cs` (rename to async) |
| Modify | `src/ServiceConnect.Interfaces/ITimeoutStore.cs` (rename to async) |
| Modify | `src/ServiceConnect/Bus.cs` (SemaphoreSlim + CT plumbing) |
| Modify | `src/ServiceConnect/ConsumeContext.cs` (CT property) |
| Modify | `src/ServiceConnect/Services/RequestReplyManager.cs` (linked CTS) |
| Modify | `src/ServiceConnect/Services/MessageDispatcher.cs` (CT through pipeline) |
| Modify | `src/ServiceConnect/Services/SendMessagePipeline.cs` (CT through pipeline) |
| Modify | `src/ServiceConnect/Processors/HandlerProcessor.cs` (CT into context) |
| Modify | `src/ServiceConnect/Processors/ProcessManagerProcessor.cs` (CT + async persistor) |
| Modify | `src/ServiceConnect/Processors/AggregatorProcessor.cs` (CT + async persistor) |
| Modify | `src/ServiceConnect/Processors/StreamProcessor.cs` (CT into context) |
| Modify | `src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs` (implements both `IProcessManagerFinder` and `ITimeoutStore`) |
| Modify | `src/ServiceConnect.Persistence.InMemory/InMemoryAggregatorPersistor.cs` |
| Modify | `src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs` (implements both `IProcessManagerFinder` and `ITimeoutStore`) |
| Modify | `src/ServiceConnect.Persistence.MongoDb/MongoDbAggregatorPersistor.cs` |
| Modify | `src/ServiceConnect/ProcessManagerTimeoutService.cs` (thread BackgroundService CT) |
| Modify | `src/ServiceConnect.Client.RabbitMQ/Consumer.cs` (CT on StartConsumingAsync) |
| Modify | `src/ServiceConnect.Client.RabbitMQ/Producer.cs` (CT on all methods) |
| Create | `src/ServiceConnect.UnitTests/CancellationTokenTests.cs` (consolidated new tests) |
| Create | `src/ServiceConnect.EndToEndTests/CancellationE2ETests.cs` |
| Modify | Existing unit tests under `src/ServiceConnect.UnitTests/` to use new async persistor methods where relevant |
| Modify | `docs/remaining-issues.md` (mark R-016/B-01, R-034 as Done at end of work) |

## Out of scope

- `IMessageDeduplicationPersistor` (filter persistor) stays synchronous -- making it async requires `IFilter.Process` to become async, which is R-017/R-018 territory deferred to a later Group C spec.
- `IFilter.Process` stays synchronous for the same reason.
- R-009 (service locator DI migration), R-020/R-021 (Processor/Client SRP), R-032 (settings singleton) -- independent concerns.
- Migrating user-code handler interfaces (`IMessageHandler<T>`, `IStreamHandler<T>`, `Aggregator<T>`) to accept CT directly -- handlers access CT via `IConsumeContext.CancellationToken`, consistent with MassTransit/NServiceBus/Rebus.
