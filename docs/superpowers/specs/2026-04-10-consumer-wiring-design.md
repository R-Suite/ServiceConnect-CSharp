# Consumer Wiring and E2E Test Completion Design

## Overview

Wire up the consumer side of ServiceConnect so that `Bus.StartConsuming()` actually subscribes to RabbitMQ queues, dispatches received messages to registered `IMessageHandler<T>` implementations, and supports request/reply via `ConsumeContext.Reply()`. Then implement the full set of E2E tests covering all messaging patterns.

## Current State

- The RabbitMQ consumer infrastructure (`Client.cs`, `Consumer.cs`, `Connection.cs`) is fully built and functional
- `Bus.StartConsuming()` just sets a boolean flag — it never calls `IConsumer.StartConsumingAsync()`
- No `IConsumer` is registered in DI
- No `MessageDispatcher` exists to bridge `ConsumerEventHandler` to `IMessageHandler<T>`
- No `ConsumeContext` implementation exists (only the `IConsumeContext` interface)
- No handler scanning or registration logic exists
- `UseRabbitMQ()` only configures transport settings, doesn't register Producer or Consumer
- E2E tests can only verify producer-side behavior (send/publish/route)

## Goals

1. Wire `Bus.StartConsuming()` to actually consume from RabbitMQ via `IConsumer`
2. Dispatch received messages to `IMessageHandler<T>` implementations via `MessageDispatcher`
3. Support handler discovery via assembly scanning (`ScanForMessageHandlers = true`) and explicit DI registration
4. Implement `ConsumeContext` with `Reply<TReply>()` for request/reply pattern
5. Make `UseRabbitMQ()` register both `IProducer` and `IConsumer`
6. Add `StartConsumingAsync()` to `IBus` (keep sync `StartConsuming()` for backward compat)
7. Implement full E2E tests for all messaging patterns

## New Classes

### MessageDispatcher (`src/ServiceConnect/Services/MessageDispatcher.cs`)

Implements the `ConsumerEventHandler` delegate. Responsible for the full incoming message pipeline:

1. Resolve CLR `Type` from `FullTypeName` header (assembly-qualified name)
2. Deserialize `byte[]` to `Message` via `IMessageSerializer.Deserialize(bytes, type)`
3. Build `Envelope` with body and headers
4. Run `IFilterPipeline.ExecuteBeforeConsumingFilters(envelope)` — if blocked, return success (filtered out)
5. Check for `ResponseMessageId` in headers — if present, this is a reply to a pending request. Route to `IRequestReplyManager.ProcessReply(responseMessageId, bytes, type)` and return (don't dispatch to handlers)
6. Resolve all `IMessageHandler<T>` for the message type from `IServiceProvider`
7. Create `ConsumeContext` with bus reference, headers, message ID, correlation ID
8. Set context on each handler and call `handler.HandleAsync(message)`
9. Run `IFilterPipeline.ExecuteAfterConsumingFilters(envelope)`
10. Return `ConsumeEventResult { Success = true }`
11. On exception: return `ConsumeEventResult { Success = false, Exception = ex }`

Dependencies: `IServiceProvider`, `IMessageSerializer`, `IFilterPipeline`, `IRequestReplyManager`, `IBus` (for ConsumeContext)

### ConsumeContext (`src/ServiceConnect/Services/ConsumeContext.cs`)

Implements `IConsumeContext`:
- `IBus Bus` — reference to the bus for sending replies
- `IDictionary<string, object> Headers` — incoming message headers
- `string? MessageId` — from `HeaderKeys.MessageId` in headers
- `Guid CorrelationId` — from `HeaderKeys.CorrelationId` in headers
- `Reply<TReply>(message, headers)` — sends reply to `SourceAddress` endpoint from incoming headers. Sets `ResponseMessageId` header to the incoming `RequestMessageId` value so the requester's `RequestReplyManager.ProcessReply()` can correlate it. Uses `Bus.SendAsync()` with the source address as endpoint.

### HandlerScanner (`src/ServiceConnect/Services/HandlerScanner.cs`)

Static utility class:
- `IList<HandlerReference> ScanForHandlers(IEnumerable<Assembly> assemblies)` — finds all concrete, non-abstract types implementing `IMessageHandler<T>` for any T, extracts the message type, returns `HandlerReference` list
- Used by `ServiceCollectionExtensions` when `ScanForMessageHandlers` is true

## Modified Classes

### IBus (`src/ServiceConnect.Interfaces/IBus.cs`)

Add:
```csharp
Task StartConsumingAsync();
```

### Bus (`src/ServiceConnect/Bus.cs`)

- Add constructor parameters: `IConsumer? consumer`, `MessageDispatcher dispatcher`, `IQueueConfiguration queueConfig`, `IList<HandlerReference> handlerReferences`
- `IConsumer` is nullable — if no transport consumer is registered (e.g., unit tests with mock producer), consuming is not available
- `StartConsumingAsync()`: extract message type names from handler references, call `_consumer.StartConsumingAsync(queueName, messageTypeNames, _dispatcher.Dispatch)`, set `_consuming = true`
- `StartConsuming()`: call `StartConsumingAsync().GetAwaiter().GetResult()`
- `StopConsuming()`: dispose consumer if present, set `_consuming = false`
- `Dispose()`: also dispose consumer

### UseRabbitMQ() (`src/ServiceConnect.Client.RabbitMQ/RabbitMQExtensions.cs`)

Register both transport services:
```csharp
builder.AdditionalRegistrations.Add(services =>
{
    services.TryAddSingleton<IProducer, Producer>();
    services.TryAddSingleton<IConsumer, Consumer>();
});
```

### ServiceCollectionExtensions.AddServiceConnect()

- When `ScanForMessageHandlers` is true: call `HandlerScanner.ScanForHandlers()` on `AppDomain.CurrentDomain.GetAssemblies()`, register each handler type in DI as its `IMessageHandler<T>` interface
- Register `IList<HandlerReference>` as singleton (scan results)
- Register `MessageDispatcher` as singleton

## E2E Tests

All tests use TestContainers RabbitMQ, register handlers via explicit DI, call `StartConsumingAsync()`, and use `TaskCompletionSource` with 30-second timeout.

### PublishSubscribeTests (Messaging collection)
- **Publish_MultipleSubscribers_EachReceives**: Two bus instances on different queues both subscribe to same message type. Publish once, both handlers receive it.
- **Publish_NoSubscriber_DoesNotThrow**: Publish when no consumer is listening.

### RequestReplyE2ETests (Messaging collection)
- **SendRequest_ResponderReplies_RequesterGetsResponse**: Responder bus registers handler that calls `context.Reply(response)`. Requester calls `SendRequestAsync`, gets response.
- **SendRequest_Timeout_WhenNoResponder**: Requester sends request, no responder, timeout fires.

### CompetingConsumersTests (Messaging collection)
- **MultipleConsumers_SameQueue_LoadBalances**: Two bus instances on same queue, send 10 messages, each gets some but not all.

### PriorityQueueTests (Messaging collection)
- **HigherPriority_ConsumedFirst**: Send low then high priority messages, verify high priority consumed before low.

### ContentRoutingTests (Messaging collection)
- **DifferentMessageTypes_RouteToCorrectHandlers**: Register handler for TypeA and TypeB, send one of each, verify each handler gets the correct type.

### PolymorphicMessageTests (Messaging collection)
- **BaseTypeHandler_ReceivesDerivedMessage**: Register handler for base type, send derived type, handler receives it.

### FilterPipelineConsumerTests (Messaging collection)
- **BeforeConsumingFilter_Blocks_HandlerNotInvoked**: Register before-consuming filter that blocks, send message, verify handler never called.
- **AfterConsumingFilter_RunsAfterHandler**: Register after-consuming filter, verify it runs after handler completes.

### ProcessManagerTests (Persistence collection)
- **MultiStepWorkflow_StatePersistedAndAdvanced**: Send start message, handler creates process manager state in MongoDB. Send second message, handler reads and updates state. Verify final state.

### AggregatorTests (Persistence collection)
- **PartialMessages_AggregatedWhenBatchComplete**: Send N messages, aggregator collects them, verify batch handler fires when batch size reached.

## Technology Choices

- Assembly scanning via `AppDomain.CurrentDomain.GetAssemblies()` (consistent with existing `ScanForMessageHandlers` flag)
- Handler resolution via `IServiceProvider.GetService()` using open generic `typeof(IMessageHandler<>).MakeGenericType(messageType)`
- Reply routing via `SourceAddress` header (set by Producer on outgoing messages)
- 30-second test timeout via `CancellationTokenSource`
