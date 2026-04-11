# ServiceConnect v7 Gap Fixes — Design Spec

## Overview

This spec addresses 6 functional gaps identified in the v7 refactor where features existed in master (v6) but are missing or non-functional in the `improvements-and-fixes` branch. It also covers one removal (`IStartProcessManager`) and one config cleanup (`HeartbeatQueueName`).

All changes include both unit tests and E2E tests.

---

## 1. Process Manager Timeout Polling

### Problem
The data structures exist (`TimeoutData`, `IProcessManagerFinder.GetTimeoutsBatch/RemoveDispatchedTimeout`, `EnableProcessManagerTimeouts` config) but nothing drives the polling loop. Process manager timeouts never fire.

### Design

**New class: `ProcessManagerTimeoutService : IHostedService`** in `src/ServiceConnect/Services/`

- Registered in `ServiceCollectionExtensions` alongside other services.
- Constructor injects: `IBusConfiguration`, `IServiceProvider`, `MessageDispatcher`, `IMessageSerializer`, `ILogger<ProcessManagerTimeoutService>`.
- `StartAsync`: if `IBusConfiguration.EnableProcessManagerTimeouts` is `false`, return immediately (no-op). Resolve `IProcessManagerFinder` via `IServiceProvider.GetService<>()` (not `GetRequiredService`) — if not registered (no persistence provider configured), log a warning and return (no-op). Otherwise, start a `PeriodicTimer` polling loop.
- **Poll interval**: configurable via a constant, default 30 seconds. Use `PeriodicTimer` for clean async cancellation.
- **Poll logic**: call `GetTimeoutsBatch()`. For each `TimeoutData`:
  - Deserialize `TimeoutData.MessageData` using `IMessageSerializer`.
  - Build headers dictionary including `FullTypeName` from `TimeoutData.MessageType`.
  - Call `MessageDispatcher.Dispatch(messageBytes, typeName, headers)`.
  - On success, call `RemoveDispatchedTimeout(timeoutData.Id)`.
  - On failure, log and skip (will be retried next poll).
- `StopAsync`: cancel the timer, wait for in-flight poll to complete.

### Tests

**Unit tests** (`ProcessManagerTimeoutServiceTests.cs`):
- Service does nothing when `EnableProcessManagerTimeouts = false`.
- Service polls and dispatches expired timeouts.
- Service removes dispatched timeouts on success.
- Service skips (does not remove) timeouts that fail dispatch.

**E2E test** (`ProcessManagerTimeoutE2ETests.cs`):
- Process manager handler inserts a timeout via `IProcessManagerFinder.InsertTimeout`.
- Wait for timeout to expire + poll interval.
- Assert the timeout message was dispatched and handled by the appropriate handler.

---

## 2. Middleware Pipeline

### Problem
`IPipelineConfiguration.MessageProcessingMiddleware` and `SendMessageMiddleware` collections exist and are populatable via config, but neither `MessageDispatcher` nor `SendMessagePipeline` invoke them.

### Design

**Message Processing Middleware** — invoked in `MessageDispatcher.Dispatch()`:

- After deserialization and before-consuming filters, build a middleware chain.
- Each `IMessageProcessingMiddleware` receives: `byte[] messageBytes`, `Type messageType`, `object message`, `IDictionary<string, object> headers`, `Envelope envelope`, and a `Func<Task<ConsumeEventResult>> next` delegate.
- The innermost `next()` runs the existing processor loop (process manager → aggregator → handler → after-consuming filters).
- If no middleware is registered, call the processor loop directly (zero overhead).
- Middleware is invoked in registration order: first registered = outermost wrapper.

**Send Message Middleware** — invoked in `SendMessagePipeline`:

- `ExecutePublishMessagePipelineAsync` and `ExecuteSendMessagePipelineAsync` build a middleware chain before calling the producer.
- Each `ISendMessageMiddleware` receives: `Type messageType`, `byte[] messageBytes`, `IDictionary<string, string> headers`, `string? endpoint`, and a `Func<Task> next` delegate.
- The innermost `next()` calls `_producer.PublishAsync` or `_producer.SendAsync`.
- If no middleware is registered, call the producer directly.

**Interface details**:

`ISendMessageMiddleware` already exists with the correct pattern:
- `SendMessageDelegate Next { get; set; }` — set by the pipeline builder
- `Task Process(Type typeObject, byte[] messageBytes, Dictionary<string, string> headers, string? endPoint)` — calls `Next(...)` to continue the chain

`IMessageProcessingMiddleware` does **not exist** — only a `IList<Type>` slot in `IPipelineConfiguration`. Create a new interface following the same pattern as `ISendMessageMiddleware`:
- Define `MessageProcessingDelegate` as `delegate Task<ConsumeEventResult> MessageProcessingDelegate(byte[] messageBytes, Type messageType, object message, IDictionary<string, object> headers, Envelope envelope)`
- `MessageProcessingDelegate Next { get; set; }`
- `Task<ConsumeEventResult> Process(byte[] messageBytes, Type messageType, object message, IDictionary<string, object> headers, Envelope envelope)` — calls `Next(...)` to continue the chain

### Tests

**Unit tests** (`MiddlewarePipelineTests.cs`):
- Single middleware wraps processing — verify it sees the message and `next()` invokes the handler.
- Multiple middleware chain in correct order.
- Middleware can short-circuit by not calling `next()`.
- Send middleware wraps producer calls.
- No middleware registered — processor/producer called directly.

**E2E test** (`MiddlewarePipelineE2ETests.cs`):
- Register a message processing middleware that adds a custom header, and a handler that asserts the header exists.
- Register a send middleware that adds a header, consume the message and assert the header arrived.

---

## 3. AutoStartConsuming via IHostedService

### Problem
`IBusConfiguration.AutoStartConsuming` defaults to `true` but nothing reads it. Users must always call `StartConsumingAsync()` manually.

### Design

**New class: `BusHostedService : IHostedService`** in `src/ServiceConnect/Services/`

- Registered in `ServiceCollectionExtensions` always.
- Constructor injects: `IBus`, `IBusConfiguration`, `ILogger<BusHostedService>`.
- `StartAsync`: if `AutoStartConsuming` is `true`, call `bus.StartConsumingAsync()` (method is on `IBus` interface). Wrap in try-catch — if no consumer is registered (no `UseRabbitMQ` call), catch the `InvalidOperationException`, log a warning, and skip rather than crashing.
- `StopAsync`: call `bus.StopConsuming()`.
- Disposal of the bus is handled by the DI container (Bus implements IDisposable, registered as singleton).

### Tests

**Unit tests** (`BusHostedServiceTests.cs`):
- `AutoStartConsuming = true` → `StartConsumingAsync` is called.
- `AutoStartConsuming = false` → `StartConsumingAsync` is NOT called.
- No consumer registered + `AutoStartConsuming = true` → logs warning, does not throw.

**E2E test** (`AutoStartConsumingE2ETests.cs`):
- Configure bus with `AutoStartConsuming = true`, register a handler, publish a message.
- Assert handler receives message WITHOUT explicit `StartConsumingAsync()` call.
- This test will need to use `IHost` / `HostBuilder` to trigger the hosted service lifecycle.

---

## 4. ConsumerCount (renamed from Clients)

### Problem
`IBusConfiguration.Clients` exists (default 1) but `Consumer.cs` hardcodes `clientCount = 1`. Multiple consumer instances are never created regardless of config.

### Design

- **Rename** `IBusConfiguration.Clients` → `ConsumerCount` (default `1`). Update `BusConfiguration` accordingly.
- **Inject `IBusConfiguration` into `Consumer`** via constructor. The `Consumer` currently accepts `ITransportConfiguration` and `IQueueConfiguration` — add `IBusConfiguration`.
- **Replace** `int clientCount = 1;` at `Consumer.cs:101` with `int clientCount = _busConfiguration.ConsumerCount;`.
- **Update `ServiceCollectionExtensions`** to pass `IBusConfiguration` when constructing the consumer (if using factory registration).

### Tests

**Unit tests** (`ConsumerCountTests.cs`):
- `ConsumerCount = 1` → one Client created.
- `ConsumerCount = 3` → three Clients created.

**E2E test** — the existing `CompetingConsumersTests` already validates competing consumer behavior. Add a variant or extend it:
- Configure `ConsumerCount = 2`, send multiple messages, assert both consumers process messages (messages distributed across consumers).

---

## 5. ExceptionHandler

### Problem
`IBusConfiguration.ExceptionHandler` (type `Action<Exception>?`) exists but is never invoked anywhere in production code.

### Design

- **Inject `IBusConfiguration` into `MessageDispatcher`** via constructor.
- **In `Dispatch()` catch block** (currently lines 80-84), after `_logger.LogError(...)`, add: `_config.ExceptionHandler?.Invoke(ex);`
- No changes to the sending side — callers catch exceptions directly.

### Tests

**Unit tests** (`ExceptionHandlerTests.cs`):
- Handler throws → ExceptionHandler callback fires with the correct exception.
- ExceptionHandler is null → no error (just logging as today).
- ExceptionHandler itself throws → does not break message processing (wrap invocation in try-catch, log warning).

**E2E test** (`ExceptionHandlerE2ETests.cs`):
- Configure `ExceptionHandler` to capture exceptions into a `ConcurrentBag<Exception>`.
- Send a message to a handler that throws.
- Assert the ExceptionHandler received the exception with the correct message.

---

## 6. Remove IStartProcessManager

### Problem
`IStartProcessManager<T>` exists as an interface but is not scanned, registered, or dispatched in v7. It's a legacy pattern superseded by `IProcessHandler<TData, TMessage>`.

### Design

- **Delete** `src/ServiceConnect.Interfaces/IStartProcessManager.cs`.
- **Verify** no other code references it (HandlerScanner, ProcessManagerProcessor, tests).
- **Existing `IProcessHandler<TData, TMessage>` already covers the use case**: when `IProcessManagerFinder` returns no existing data, a `new TData()` is passed to `HandleAsync`. The handler checks if the data is fresh (e.g., `data.CorrelationId == default`) and initializes it.
- **Verify** `ProcessManagerProcessor` already creates `new TData()` when finder returns null. If not, add this behavior.

### Tests

- Existing process manager E2E tests already cover the `IProcessHandler` pattern.
- Add a unit test verifying that when no existing process manager data is found, `HandleAsync` receives a new `TData` instance.

---

## 7. Remove HeartbeatQueueName

### Problem
`IQueueConfiguration.HeartbeatQueueName` is vestigial — heartbeats are handled at the AMQP transport level via `Connection.cs` `RequestedHeartbeat`, not via a message queue.

### Design

- **Remove** `HeartbeatQueueName` from `IQueueConfiguration` interface.
- **Remove** from `QueueConfiguration` concrete class.
- **Remove** from any unit tests asserting its default value.

### Tests

- No new tests needed. Existing tests updated to remove references.

---

## Migration Notes (for documentation)

| v6 Pattern | v7 Replacement |
|---|---|
| `IStartProcessManager<T>.ExecuteAsync(msg)` | `IProcessHandler<TData, T>.HandleAsync(msg, data)` — check `data.CorrelationId == default` for initial state |
| `config.Clients = N` | `config.ConsumerCount = N` |
| `HeartbeatQueueName` config | Removed — use `ClientSettings["HeartbeatTime"]` for AMQP heartbeat interval |

---

*Generated: 2026-04-11*
*Branch: improvements-and-fixes*
