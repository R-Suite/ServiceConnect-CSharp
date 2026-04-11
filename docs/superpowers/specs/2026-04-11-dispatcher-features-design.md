# Process Manager, Aggregator & Streaming — Design Spec

## Goal

Wire the remaining three messaging patterns into ServiceConnect's dispatcher: Process Manager (correlation-based stateful workflows), Aggregator (batch message collection), and Streaming (chunked large message transfer). Each integrates via a chain-of-responsibility dispatcher architecture.

## Architecture: Chain-of-Responsibility Dispatcher

The current `MessageDispatcher.Dispatch()` is refactored to delegate to an ordered chain of **message processors**:

```
MessageDispatcher.Dispatch()
  → ReplyProcessor             (ResponseMessageId header → RequestReplyManager)
  → StreamProcessor            (MessageType=ByteStream header → packet assembly)
  → ProcessManagerProcessor    (IProcessHandler<,> registered → correlation-based state)
  → AggregatorProcessor        (Aggregator<T> registered → batch collection)
  → HandlerProcessor           (IMessageHandler<T> → existing handler dispatch)
```

### IMessageProcessor Interface

```csharp
public interface IMessageProcessor
{
    Task<ProcessResult> ProcessAsync(
        byte[] messageBytes, 
        Type messageType, 
        object message,
        IDictionary<string, object> headers, 
        Envelope envelope);
}

public enum ProcessResult { Handled, NotHandled }
```

Each processor checks if it can handle the message and returns `Handled` or `NotHandled`. The dispatcher iterates in order; first `Handled` wins. If none handle it, log warning and return success.

### MessageDispatcher Refactored

Constructor takes `IList<IMessageProcessor>` instead of individual services. The `Dispatch()` method becomes:

1. Resolve CLR type from `FullTypeName` header
2. Deserialize the message
3. Build Envelope, run `BeforeConsumingFilters`
4. Iterate processors until one returns `Handled`
5. Run `AfterConsumingFilters`
6. Return success

**Exception:** `ReplyProcessor` operates on raw bytes (before deserialization) since replies route to `RequestReplyManager.ProcessReply()` which deserializes internally. So `ReplyProcessor` is checked before deserialization — the dispatcher calls it first with raw bytes, then deserializes and iterates the remaining processors.

---

## Feature 1: Process Manager

### New Interface

```csharp
public interface IProcessHandler<TData, TMessage> 
    where TData : class, IProcessManagerData, new()
    where TMessage : Message
{
    IConsumeContext? Context { get; set; }
    Task HandleAsync(TMessage message, TData data);
    
    /// <summary>
    /// Configure how the process manager data correlates with the message.
    /// Default maps CorrelationId to CorrelationId.
    /// </summary>
    void ConfigureMapper(IProcessManagerPropertyMapper mapper)
    {
        // Default implementation — override for custom correlation
        mapper.ConfigureMapping<TData, TMessage>(d => d.CorrelationId, m => m.CorrelationId);
    }
}
```

Single interface for both starting and continuing a process. The handler receives the data object — if it's new (no existing state found), the data is a fresh `new TData()` with `CorrelationId` set. If existing, it's the loaded state. The `ConfigureMapper` method has a default implementation that maps `CorrelationId → CorrelationId`, which is the common case.

### ProcessManagerProcessor Flow

1. Check if any `IProcessHandler<,>` is registered for the message type via DI. If not, return `NotHandled`.
2. Resolve the handler and its `TData` type via reflection on the generic interface.
3. Build or retrieve an `IProcessManagerPropertyMapper` for this handler. The `IProcessHandler<TData, TMessage>` interface includes a `ConfigureMapper(IProcessManagerPropertyMapper mapper)` default method — see below.
4. Call `IProcessManagerFinder.FindData<TData>(mapper, message)` to load existing state. The finder uses the mapper to extract the correlation property from the message and query persistence.
5. If no state found (`FindData` returns null), create `new TData()` and set `CorrelationId` from the message.
6. Set `Context` on the handler, call `HandleAsync(message, data)`.
7. If state was new, call `InsertData(data)`. If existing, call `UpdateData(persistenceData)`.
8. Return `Handled`.

### Correlation Strategy

`IProcessManagerFinder.FindData<T>` takes an `IProcessManagerPropertyMapper` and a `Message`. The mapper defines which property on the data maps to which property on the message (e.g., `data.CorrelationId ↔ message.CorrelationId`).

The `IProcessHandler<TData, TMessage>` interface includes a method to configure the mapper:

```csharp
void ConfigureMapper(IProcessManagerPropertyMapper mapper);
```

The processor creates a mapper instance, calls `handler.ConfigureMapper(mapper)`, then passes it to `FindData`. A default implementation maps `CorrelationId → CorrelationId`:

```csharp
mapper.ConfigureMapping<TData, TMessage>(d => d.CorrelationId, m => m.CorrelationId);
```

Users override `ConfigureMapper` when their correlation property differs from `CorrelationId`.

### DI Registration

Users register: `services.AddTransient<IProcessHandler<MyData, StartMessage>, MyHandler>()`. `HandlerScanner` extended to scan for `IProcessHandler<,>` implementations.

### Dependencies

`ProcessManagerProcessor` requires `IProcessManagerFinder` and `IProcessManagerPropertyMapper` from DI. If `IProcessManagerFinder` is not registered (no persistence configured), the processor logs a warning and returns `NotHandled`. `IProcessManagerPropertyMapper` is created per handler invocation internally.

---

## Feature 2: Aggregator

### Existing Interface (unchanged)

```csharp
public abstract class Aggregator<T> where T : Message
{
    public virtual TimeSpan Timeout() => TimeSpan.FromSeconds(30);
    public virtual int BatchSize() => 10;
    public abstract void Execute(IList<T> messages);
}
```

### AggregatorProcessor Flow

1. Check if any `Aggregator<T>` subclass is registered for the message type via DI. If not, return `NotHandled`.
2. Resolve the aggregator instance. Get `BatchSize()` and `Timeout()`.
3. Persist the message via `IAggregatorPersistor.InsertData(message, aggregatorName)` where `aggregatorName` is the aggregator class's full name.
4. Check count: `IAggregatorPersistor.Count(aggregatorName)`.
5. If count >= `BatchSize()`: load all via `GetData(aggregatorName)`, cast to `IList<T>`, call `Execute(messages)`, remove all from store.
6. If count < `BatchSize()`: start or reset an in-process timer. When the timer fires, load + execute + remove whatever has accumulated.
7. Return `Handled`.

### Timer Management

- `ConcurrentDictionary<string, Timer>` keyed by aggregator name.
- On each message: if timer exists, dispose and recreate with fresh timeout. If not, create one.
- Timer callback: lock on aggregator name, load messages, execute, remove, dispose timer.
- `AggregatorProcessor` implements `IDisposable` to clean up all timers.

### DI Registration

Users register: `services.AddTransient<Aggregator<MyMessage>, MyAggregator>()`. `HandlerScanner` extended to scan for `Aggregator<T>` subclasses.

### Dependencies

`AggregatorProcessor` requires `IAggregatorPersistor` from DI. If not registered, logs warning and returns `NotHandled`.

---

## Feature 3: Streaming

### Write Side — MessageBusWriteStream

Implements `IMessageBusWriteStream`. Created by `Bus.CreateStream<T>(endpoint, message)`:

1. Generates a unique `SequenceId` (GUID).
2. Each `Write(buffer, offset, count)` sends a packet via `IProducer.SendBytesAsync(endpoint, packet, headers)` with headers:
   - `SequenceId` — stream identifier
   - `PacketNumber` — incrementing counter (starting from 0)
   - `FullTypeName` — the message type's assembly-qualified name
   - `TypeName` — the message type's full name
   - `MessageType` — set to `"ByteStream"`
3. `Close()` sends a final (possibly empty) packet with `LastPacketNumber` header set to total packet count - 1. Disposes resources.

### Read Side — MessageBusReadStream

Implements `IMessageBusReadStream`. Held in memory by `StreamProcessor`:

1. Accumulates packets in a `ConcurrentDictionary<long, byte[]>` keyed by `PacketNumber`.
2. When `LastPacketNumber` header arrives, stores the expected final packet number.
3. `IsComplete()` returns true when all packets from 0 to `LastPacketNumber` are present.
4. `Read()` concatenates all packets in order and returns the full byte array.

### StreamProcessor Flow

1. Check if header `MessageType` equals `"ByteStream"`. If not, return `NotHandled`.
2. Extract `SequenceId` and `PacketNumber` from headers.
3. Look up or create `MessageBusReadStream` in `ConcurrentDictionary<string, MessageBusReadStream>` keyed by `SequenceId`.
4. Call `stream.Write(body, packetNumber)`.
5. If `LastPacketNumber` header is present, record it on the stream.
6. If `stream.IsComplete()`: resolve the message type from `FullTypeName` header, resolve `IStreamHandler<T>` from DI, set `Stream` property, call `Execute(message)`, remove stream from dictionary.
7. Return `Handled`.

### In-Memory Packet Storage

Partial streams held in `ConcurrentDictionary<string, MessageBusReadStream>` inside `StreamProcessor`. No persistence — streaming is transient. If the process dies mid-stream, the sender must resend.

### DI Registration

Users register: `services.AddTransient<IStreamHandler<MyMessage>, MyStreamHandler>()`. `HandlerScanner` extended to scan for `IStreamHandler<T>` implementations.

---

## DI Wiring Changes

### ServiceCollectionExtensions

- Register processor chain as `IList<IMessageProcessor>` in order: `ReplyProcessor`, `StreamProcessor`, `ProcessManagerProcessor`, `AggregatorProcessor`, `HandlerProcessor`
- `MessageDispatcher` constructor takes `IList<IMessageProcessor>` instead of individual services
- `HandlerScanner` extended to scan for: `IProcessHandler<,>`, `Aggregator<T>` subclasses, `IStreamHandler<T>` (in addition to existing `IMessageHandler<T>`)

### Persistence Registration

Users call `builder.UseInMemoryPersistence()` or `builder.UseMongoDbPersistence(...)` to register `IProcessManagerFinder` and `IAggregatorPersistor`. Extension methods already exist on persistence projects. If no persistence registered and a process manager / aggregator handler is found, the processor logs a warning and returns `NotHandled`.

### New Registrations in AddServiceConnect

- `ReplyProcessor` — singleton, needs `IRequestReplyManager`, `IMessageSerializer`
- `StreamProcessor` — singleton (holds packet buffers)
- `ProcessManagerProcessor` — singleton, needs `IProcessManagerFinder` (optional), `IServiceProvider`
- `AggregatorProcessor` — singleton (holds timers), needs `IAggregatorPersistor` (optional), `IServiceProvider`
- `HandlerProcessor` — singleton, needs `IServiceProvider`, `IFilterPipeline` (no — filters stay in dispatcher)

---

## E2E Tests

### Process Manager Tests

**InMemory variant** (`[Collection(nameof(MessagingCollection))]`, `[Trait("Category", "Docker")]`):
- Register `IProcessHandler<TestProcessData, TestMessage>` with handler that increments `Counter` on data
- Configure `InMemoryProcessManagerFinder`
- Send message with CorrelationId X → new state created, Counter = 1
- Send second message with same CorrelationId X → existing state loaded, Counter = 2
- Assert final state via `FindData` has Counter = 2

**MongoDB variant** (`[Collection(nameof(PersistenceCollection))]`, `[Trait("Category", "Docker")]`):
- Same test logic but with `MongoDbProcessManagerFinder` via `PersistenceFixture`

### Aggregator Tests

**InMemory variant** (`[Collection(nameof(MessagingCollection))]`, `[Trait("Category", "Docker")]`):
- Register `Aggregator<TestMessage>` subclass with `BatchSize() = 3`
- `Execute()` signals a `TaskCompletionSource` with collected messages
- Configure `InMemoryAggregatorPersistor`
- Send 3 messages → aggregator fires, list has all 3

**MongoDB variant** (`[Collection(nameof(PersistenceCollection))]`, `[Trait("Category", "Docker")]`):
- Same test logic with `MongoDbAggregatorPersistor`

**Timeout test** (InMemory, `[Collection(nameof(MessagingCollection))]`, `[Trait("Category", "Docker")]`):
- `BatchSize() = 10`, `Timeout() = 2 seconds`
- Send 2 messages → wait 3 seconds → aggregator fires with 2 messages

### Streaming Tests

`[Collection(nameof(MessagingCollection))]`, `[Trait("Category", "Docker")]`:
- Publisher bus creates stream via `Bus.CreateStream<T>()`, writes 3 chunks, closes
- Consumer bus has `IStreamHandler<T>` registered
- Handler receives complete reassembled data
- Assert bytes match original chunks concatenated

---

## Files to Create or Modify

### New Files
- `src/ServiceConnect.Interfaces/IMessageProcessor.cs` — interface + ProcessResult enum
- `src/ServiceConnect.Interfaces/IProcessHandler.cs` — process handler interface
- `src/ServiceConnect/Services/Processors/ReplyProcessor.cs`
- `src/ServiceConnect/Services/Processors/StreamProcessor.cs`
- `src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs`
- `src/ServiceConnect/Services/Processors/AggregatorProcessor.cs`
- `src/ServiceConnect/Services/Processors/HandlerProcessor.cs`
- `src/ServiceConnect/Services/MessageBusWriteStream.cs`
- `src/ServiceConnect/Services/MessageBusReadStream.cs`
- `src/ServiceConnect.EndToEndTests/ProcessManagerTests.cs`
- `src/ServiceConnect.EndToEndTests/ProcessManagerMongoDbTests.cs`
- `src/ServiceConnect.EndToEndTests/AggregatorTests.cs`
- `src/ServiceConnect.EndToEndTests/AggregatorMongoDbTests.cs`
- `src/ServiceConnect.EndToEndTests/StreamingTests.cs`

### Modified Files
- `src/ServiceConnect/Services/MessageDispatcher.cs` — refactor to use processor chain
- `src/ServiceConnect/Bus.cs` — implement `CreateStream<T>()`
- `src/ServiceConnect/ServiceCollectionExtensions.cs` — register processors, extend scanner
- `src/ServiceConnect/Services/HandlerScanner.cs` — scan for new handler types
- `src/ServiceConnect.EndToEndTests/Messages/TestMessage.cs` — add TestProcessData if needed
