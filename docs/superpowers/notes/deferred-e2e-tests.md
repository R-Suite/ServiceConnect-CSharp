# Deferred E2E Tests

## ALL DONE

### CompetingConsumersTests ✅
- Implemented in `CompetingConsumersTests.cs`
- Two bus instances share same queue, 10 messages sent, all received exactly once

### PriorityQueueTests ✅
- Implemented in `PriorityQueueTests.cs`
- Pre-declared priority queue with `x-max-priority`, messages with alternating priorities, higher consumed first

### ScatterGatherTests ✅
- Implemented in `ScatterGatherTests.cs`
- Two responder buses, one requester, `SendRequestMultiAsync` collects both replies

### MessageDeduplicationTests ✅
- Implemented in `MessageDeduplicationTests.cs`
- Test-local `IFilter` implementation tracks seen MessageIds, blocks redelivered duplicates

### PolymorphicMessageTests ✅
- Implemented in `PolymorphicMessageTests.cs`
- `MessageDispatcher` walks type hierarchy when resolving handlers
- Handler for base type receives derived message

### ProcessManagerTests ✅
- Implemented in `ProcessManagerTests.cs` (InMemory) and `ProcessManagerMongoDbTests.cs` (MongoDB)
- `IProcessHandler<TData, TMessage>` interface for correlation-based stateful workflows
- `ProcessManagerProcessor` in chain-of-responsibility dispatcher
- Two messages with same CorrelationId → state loaded and updated correctly

### AggregatorTests ✅
- Implemented in `AggregatorTests.cs` (InMemory) and `AggregatorMongoDbTests.cs` (MongoDB)
- `AggregatorProcessor` in chain-of-responsibility dispatcher
- Batch test: 3 messages → Execute fires with all 3
- Timeout test: 2 messages (batch size 10, timeout 2s) → Execute fires after timeout

### StreamingTests ✅
- Implemented in `StreamingTests.cs`
- `Bus.CreateStream<T>()` → `MessageBusWriteStream` sends chunked packets
- `StreamProcessor` reassembles packets by SequenceId
- `IStreamHandler<T>` receives complete reassembled data
