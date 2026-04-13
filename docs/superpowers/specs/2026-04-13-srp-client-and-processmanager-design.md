# SRP Refactor — Client + ProcessManagerProcessor (R-020/R-021)

**Status:** Design approved 2026-04-13

**Goal:** Refactor two "too-many-responsibilities" files called out in R-020/R-021 without changing external behavior. Client is split into three collaborators; ProcessManagerProcessor becomes a thin orchestrator over a cached descriptor registry.

---

## Scope

**In scope:**

1. Split `src/ServiceConnect.Client.RabbitMQ/Client.cs` (313 lines) into three collaborators inside the same project:
   - `RabbitMqConsumerHost` — channel/consumer lifecycle, receive loop, ack/nack.
   - `MessageRetryHandler` — failure-path policy (retry counter, retry-queue publish, error-exchange publish on exhaustion).
   - `MessageAuditPublisher` — success-path policy (audit-exchange publish when enabled).
2. Refactor `src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs` to delegate all reflection to a new singleton `ProcessManagerHandlerRegistry` that eagerly builds a frozen `messageType → ProcessManagerDescriptor` map at first use. Compiled delegates (via `Expression.Lambda`) replace `MethodInfo.Invoke` / `GetProperty/SetValue` calls.
3. Add unit tests for each new class (beginning of R-028 coverage for these subsystems).

**Out of scope:**

- Transport-agnostic retry/error/audit abstractions. Only one transport exists (RabbitMQ); abstracting now is YAGNI.
- Other processors (`HandlerProcessor`, `StreamProcessor`, `AggregatorProcessor`). Same reflection pattern exists but is tracked separately under R-009.
- Renaming or relocating `Client` as a public type. Keeps binary-compat for downstream consumers.

**Success criteria:**

- All unit + E2E tests still green (E2E via `sg docker -c "dotnet test src/ServiceConnect.EndToEndTests -v quiet"`).
- `Client.cs` shrinks to a thin orchestrator forwarding to `RabbitMqConsumerHost`.
- `ProcessManagerProcessor.cs` has no direct `MethodInfo.Invoke` or `GetProperty/GetValue` calls.
- New unit tests cover each collaborator and the registry (see Testing section).

---

## Architecture — Client Split

**Files created in `src/ServiceConnect.Client.RabbitMQ/`:**

### `RabbitMqConsumerHost.cs`

Owns channel/consumer lifecycle and ack/nack orchestration.

- **State:** `IChannel? _model`, `AsyncEventingBasicConsumer? _consumer`, `int _messagesBeingProcessed`, `CancellationToken _consumingCt`, `ConsumerEventHandler? _consumerEventHandler`, plus queue/prefetch settings (`_prefetchCount`, `_disablePrefetch`, `_autoDelete`, `_queueArguments`, `_queueName`).
- **Collaborators (ctor-injected):** `MessageRetryHandler`, `MessageAuditPublisher`, `IServiceConnectConnection`, `IQueueConfiguration`, `ILogger`.
- **Public methods:**
  - `StartConsumingAsync(ConsumerEventHandler, string queueName, bool? exclusive, bool? autoDelete, CancellationToken)` — same signature as today's `Client.StartConsumingAsync`.
  - `ConsumeMessageTypeAsync(string messageTypeName)` — binds queue to exchange.
  - `DisposeAsync()` — drain loop, retry-queue delete if `_autoDelete`, channel close.
- **Private:** `Event(object, BasicDeliverEventArgs)` handler and `ProcessMessage(BasicDeliverEventArgs)` — both move here from `Client`. `Event` still owns the `Interlocked.Increment`/`Decrement` around `_messagesBeingProcessed` and the ack/nack decision. `ProcessMessage` invokes `_consumerEventHandler`, then routes to the retry handler on failure or the audit publisher on success — passing `_model` as the channel argument.

### `MessageRetryHandler.cs`

Failure-path policy, transport-free logic except for using `IChannel.BasicPublishAsync`.

- **Ctor args:** `int maxRetries`, `string errorExchange`, `ILogger`. (`retryQueueName` is dynamic per-call because the host only learns `queueName` at `StartConsumingAsync` time — see below.)
- **Public method:** `Task HandleFailureAsync(IChannel channel, string retryQueueName, BasicDeliverEventArgs args, Dictionary<string, object> headers, Exception? ex)`.
- **Behavior:** reads `HeaderKeys.RetryCount` from `headers` (treats missing or corrupt as 0, rejects values outside `[0, maxRetries+1]`), increments, publishes to `retryQueueName` if under `maxRetries`, otherwise serializes exception info (`ExceptionType` + `Message` only — no stack trace) into `HeaderKeys.Exception` header and publishes to the error exchange. Logs the exception + MessageId on error-exchange publish.
- **Channel and retry-queue name are passed per-call**, not injected, because both are only known after the host starts consuming.

### `MessageAuditPublisher.cs`

Success-path policy.

- **Ctor args:** `string auditExchange`, `IQueueConfiguration queueConfiguration`, `ILogger`.
- **Public method:** `Task PublishAuditIfEnabledAsync(IChannel channel, BasicDeliverEventArgs args, Dictionary<string, object> headers)`.
- **Behavior:** if `_queueConfiguration.AuditingEnabled && messageType != HeaderKeys.ByteStream`, publishes to the audit exchange. Otherwise no-op.

### `HeaderHelpers.cs` (internal static)

`SetHeader<T>`, `ToNullableHeaders`, `GetErrorMessage` — pulled out of `Client` and shared by the host and the retry handler.

### Collaborator lifecycle

- `MessageRetryHandler` and `MessageAuditPublisher` are constructed in `Client`'s ctor from `ITransportConfiguration` + `IQueueConfiguration` (they only need static config: `maxRetries`, `errorExchange`, `auditExchange`, `queueConfiguration`).
- `RabbitMqConsumerHost` is also constructed in `Client`'s ctor; it receives the two collaborators as dependencies.
- `retryQueueName` (`queueName + ".Retries"`) is stored on the host in `StartConsumingAsync` and passed to `MessageRetryHandler.HandleFailureAsync` per-call.

### `Client.cs` (facade)

Becomes a thin composition root — constructs the three collaborators from `ITransportConfiguration`/`IQueueConfiguration`/`IServiceConnectConnection`/`ILogger` and forwards public calls to `RabbitMqConsumerHost`:

```csharp
public async Task StartConsumingAsync(ConsumerEventHandler messageReceived, string queueName, bool? exclusive = null, bool? autoDelete = null, CancellationToken cancellationToken = default)
    => _host.StartConsumingAsync(messageReceived, queueName, exclusive, autoDelete, cancellationToken);

public Task ConsumeMessageTypeAsync(string messageTypeName) => _host.ConsumeMessageTypeAsync(messageTypeName);

public ValueTask DisposeAsync() => _host.DisposeAsync();
```

Public ctor signature unchanged.

---

## Architecture — ProcessManagerProcessor Refactor

**Files created in `src/ServiceConnect/Services/Processors/`:**

### `ProcessManagerDescriptor.cs`

Frozen per-`(messageType, dataType)` descriptor. Built once; contains compiled delegates, no reflection at call time.

```csharp
internal sealed record ProcessManagerDescriptor(
    Type MessageType,
    Type DataType,
    Type ProcessHandlerInterfaceType,
    Func<IProcessManagerData> CreateData,
    Action<IProcessManagerData, Guid> SetCorrelationId,
    Action<object, IConsumeContext> SetHandlerContext,
    Action<object, IProcessManagerPropertyMapper> ConfigureMapper,
    Func<IProcessManagerFinder, IProcessManagerPropertyMapper, Message, CancellationToken, Task<object?>> FindData,
    Func<object, object> GetPersistenceDataData,
    Func<IProcessManagerFinder, object, CancellationToken, Task> UpdateData,
    Func<object, Message, object, Task> InvokeHandleAsync);
```

Each delegate is built with `Expression.Lambda<T>.Compile()` closed over the concrete `TData` — no `MakeGenericMethod` or `MethodInfo.Invoke` per message.

### `ProcessManagerHandlerRegistry.cs`

Singleton in DI.

- **Ctor:** `(IList<HandlerReference> handlerRefs, ILogger<ProcessManagerHandlerRegistry> logger)`.
- **Construction:** walks `handlerRefs`, filters to handlers implementing `IProcessHandler<TData,TMessage>`, builds one descriptor per `TMessage`. Stores in a frozen `Dictionary<Type, ProcessManagerDescriptor>`.
- **Fail-fast:** duplicate `messageType → handler` mappings throw `InvalidOperationException` at construction.
- **Public API:** `bool TryGet(Type messageType, out ProcessManagerDescriptor descriptor)`.

### `ProcessManagerProcessor.cs` (rewritten)

```csharp
public sealed class ProcessManagerProcessor(
    ProcessManagerHandlerRegistry registry,
    IServiceProvider serviceProvider,
    ILogger<ProcessManagerProcessor> logger) : IMessageProcessor
{
    public async Task<ProcessResult> ProcessAsync(
        byte[] messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (message == null) return ProcessResult.NotHandled;

        if (!registry.TryGet(messageType, out var descriptor))
            return ProcessResult.NotHandled;

        var finder = serviceProvider.GetService<IProcessManagerFinder>();
        if (finder == null)
        {
            logger.LogWarning("IProcessManagerFinder not registered; cannot process process-manager message {MessageType}", messageType.Name);
            return ProcessResult.NotHandled;
        }

        var handler = serviceProvider.GetService(descriptor.ProcessHandlerInterfaceType);
        if (handler == null) return ProcessResult.NotHandled;

        var mapper = new DefaultProcessManagerPropertyMapper();
        descriptor.ConfigureMapper(handler, mapper);

        var persistenceData = await descriptor.FindData(finder, mapper, (Message)message, cancellationToken).ConfigureAwait(false);

        bool isNew = persistenceData == null;
        object data;
        if (isNew)
        {
            var newData = descriptor.CreateData();
            descriptor.SetCorrelationId(newData, ((Message)message).CorrelationId);
            data = newData;
        }
        else
        {
            data = descriptor.GetPersistenceDataData(persistenceData!);
        }

        var bus = serviceProvider.GetRequiredService<IBus>();
        descriptor.SetHandlerContext(handler, new ConsumeContext(bus, headers) { CancellationToken = cancellationToken });

        await descriptor.InvokeHandleAsync(handler, (Message)message, data).ConfigureAwait(false);

        if (isNew)
            await finder.InsertDataAsync((IProcessManagerData)data, cancellationToken).ConfigureAwait(false);
        else
            await descriptor.UpdateData(finder, persistenceData!, cancellationToken).ConfigureAwait(false);

        return ProcessResult.Handled;
    }
}
```

No `MethodInfo.Invoke`. No `GetProperty/GetValue`. All type-system gymnastics live in the registry.

---

## DI Registration & Wiring

### `ProcessManagerHandlerRegistry`

Registered as singleton in `AddServiceConnect` at `src/ServiceConnect/ServiceCollectionExtensions.cs:12`:

```csharp
services.AddSingleton<ProcessManagerHandlerRegistry>();
```

`IList<HandlerReference>` is already registered at `src/ServiceConnect/ServiceCollectionExtensions.cs:101` via `services.TryAddSingleton<IList<HandlerReference>>(handlerReferences)` and is resolved by the registry ctor.

**Eager warm-up:** `Bus.StartConsumingAsync` resolves the registry via `_serviceProvider.GetRequiredService<ProcessManagerHandlerRegistry>()` before entering the consume loop, so descriptor-build errors surface at application startup, not mid-traffic.

### Client-side DI

`Client` is instantiated by existing factories (not via DI for its internals). The three new collaborators (`RabbitMqConsumerHost`, `MessageRetryHandler`, `MessageAuditPublisher`) are plain classes constructed inside `Client`'s constructor from the same inputs it already receives. No new DI registrations required.

### Field/state ownership map

| Field | New owner |
|---|---|
| `_maxRetries`, `_retryQueueName` | `MessageRetryHandler` |
| `_errorExchange` | `MessageRetryHandler` |
| `_auditExchange`, `_errorsDisabled` | `MessageAuditPublisher` (`_errorsDisabled` drives whether audit-path runs) |
| `_prefetchCount`, `_disablePrefetch`, `_autoDelete`, `_queueArguments`, `_queueName`, `_messagesBeingProcessed`, `_consumerEventHandler`, `_consumingCt`, `_model`, `_consumer` | `RabbitMqConsumerHost` |

### Public API

- `IBus` — unchanged.
- `Client` public surface — unchanged (same ctor, same methods, thin-forwarding internally).
- `IMessageProcessor`, `IProcessManagerFinder` — unchanged.

---

## Testing Strategy

E2E suite runs via `sg docker -c "dotnet test src/ServiceConnect.EndToEndTests -v quiet"` — unchanged behavior means the existing E2E suite is the regression safety net. New tests are all unit tests.

New test files go in `src/ServiceConnect.UnitTests/Processors/` alongside the existing processor tests (`AggregatorProcessorTests.cs`, `HandlerProcessorTests.cs`, etc.). Client-collaborator tests go at the root of `src/ServiceConnect.UnitTests/`.

### `src/ServiceConnect.UnitTests/Processors/ProcessManagerHandlerRegistryTests.cs`

- `Construction_BuildsDescriptorsForProcessHandlers` — handler refs containing `IProcessHandler<FooData,FooMessage>` → descriptor exists for `FooMessage`.
- `Construction_IgnoresNonProcessHandlers` — plain `IMessageHandler<T>` skipped.
- `Construction_ThrowsOnDuplicateMessageMapping` — two handlers claiming the same message type → `InvalidOperationException` at construction.
- `TryGet_ReturnsFalse_ForUnknownMessageType`.
- `Descriptor_InvokesHandleAsync_WithRealHandler` — end-to-end delegate test: build descriptor, instantiate fake handler + data, call `InvokeHandleAsync`, assert handler received correct message + data references.
- `Descriptor_SetCorrelationId_WritesProperty` — verifies compiled setter round-trip.
- `Descriptor_CreateData_CreatesFreshInstance`.

### `src/ServiceConnect.UnitTests/Processors/ProcessManagerProcessorTests.cs` (update existing)

An existing `ProcessManagerProcessorTests.cs` already lives at this path and exercises the current reflection-based processor. Since the processor ctor changes (now takes `ProcessManagerHandlerRegistry`), the existing tests will need updating in the same commit. Replace the existing cases with (or extend them to) the set below; the file remains one fixture.

- `ProcessAsync_NullMessage_ReturnsNotHandled`.
- `ProcessAsync_NoDescriptor_ReturnsNotHandled`.
- `ProcessAsync_NoFinder_ReturnsNotHandled_LogsWarning`.
- `ProcessAsync_NoHandler_ReturnsNotHandled`.
- `ProcessAsync_NewData_InsertsAndReturnsHandled`.
- `ProcessAsync_ExistingData_UpdatesAndReturnsHandled`.
- `ProcessAsync_CancelledToken_ThrowsOCE`.

### `src/ServiceConnect.UnitTests/RabbitMqConsumerHostTests.cs`

Channel/consumer interactions via `Mock<IServiceConnectConnection>` and `Mock<IChannel>`. RabbitMQ Client v7 exposes interfaces so Moq setups work.

- `StartConsumingAsync_SetsBasicQos_WhenPrefetchEnabled`.
- `StartConsumingAsync_SkipsBasicQos_WhenPrefetchDisabled`.
- `DisposeAsync_DrainsInflightMessages_BeforeClose`.
- `DisposeAsync_DeletesRetryQueue_WhenAutoDelete`.
- `DisposeAsync_SwallowsObjectDisposedException_OnQueueDelete`.
- `ConsumeMessageTypeAsync_BindsQueueToExchange`.

Event/ack paths are covered transitively through E2E; adding dedicated unit tests for the `Event` handler would require deep `BasicDeliverEventArgs` mocking and is deferred.

### `src/ServiceConnect.UnitTests/MessageRetryHandlerTests.cs`

- `HandleFailureAsync_UnderMaxRetries_IncrementsCountAndPublishesToRetryQueue`.
- `HandleFailureAsync_AtMaxRetries_PublishesToErrorExchange`.
- `HandleFailureAsync_AtMaxRetries_IncludesExceptionTypeAndMessage_ButNoStackTrace` — security regression guard: error header must not leak internals.
- `HandleFailureAsync_NoException_StillPublishesToErrorExchange`.
- `HandleFailureAsync_ReadsExistingRetryCount_FromHeaders`.
- `HandleFailureAsync_IgnoresCorruptRetryCount`.

### `src/ServiceConnect.UnitTests/MessageAuditPublisherTests.cs`

- `PublishAuditIfEnabledAsync_Publishes_WhenAuditingEnabled`.
- `PublishAuditIfEnabledAsync_Skips_WhenAuditingDisabled`.
- `PublishAuditIfEnabledAsync_Skips_ForByteStreamMessageType`.

---

## Commit Strategy

Each extraction lands in its own commit for clean review:

1. **ProcessManagerProcessor refactor** — `ProcessManagerDescriptor` + `ProcessManagerHandlerRegistry` + rewritten processor + DI registration + warm-up call + unit tests. Atomic because the processor ctor signature changes.
2. **Extract `MessageRetryHandler`** — new class + `HeaderHelpers` + tests; `Client` updated to call it from the failure path.
3. **Extract `MessageAuditPublisher`** — new class + tests; `Client` updated to call it from the success path.
4. **Extract `RabbitMqConsumerHost`** — move channel/consumer state + `Event`/`ProcessMessage`/`DisposeAsync`/`StartConsumingAsync`/`ConsumeMessageTypeAsync` to the host; tests; `Client` becomes a thin forwarding facade.
5. **Doc update** — mark R-020/R-021 done in `docs/remaining-issues.md`.

Full unit suite passes before each commit. Full E2E suite (via `sg docker -c ...`) passes before commits 1, 4, and 5.
