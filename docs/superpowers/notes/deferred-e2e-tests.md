# Deferred E2E Tests

## DONE

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
- Test-local `IFilter` implementation (legacy filter project uses incompatible `Common.Logging` + NuGet interfaces)
- Tracks seen MessageIds, blocks redelivered duplicates

### PolymorphicMessageTests ✅
- Implemented in `PolymorphicMessageTests.cs`
- `MessageDispatcher` now walks type hierarchy (base types) when resolving handlers
- Handler for base type receives derived message

---

## REMAINING (blocked on feature implementation)

### StreamingTests
- `Bus.CreateStream<T>()` currently throws `NotImplementedException`
- Needs a design for how streaming integrates with the RabbitMQ transport (chunked messages via `SendBytesAsync`, `IMessageBusWriteStream`, `IStreamHandler`)
- The `IProducer.SendBytesAsync()` and header keys (`SequenceId`, `PacketNumber`, `LastPacketNumber`) suggest a chunking protocol was planned
- **Blocked on**: implementing `CreateStream<T>()` in Bus and a corresponding stream receiver on the consumer side

### ProcessManagerTests
- Multi-step saga/workflow: send start message, handler creates process manager state in MongoDB, send second message, handler reads and updates state, verify final state
- Requires `MessageDispatcher` to integrate with `IProcessManagerFinder` — currently the dispatcher routes to `IMessageHandler<T>` but doesn't know about process managers
- Needs: a way for the dispatcher to detect process manager handlers (implement `IProcessManagerHandler<T>` or similar), call `IProcessManagerFinder.FindData()` before invoking, and `UpdateData()` / `InsertData()` after
- **Blocked on**: process manager dispatcher integration design

### AggregatorTests
- Partial messages collected, aggregated result emitted when batch complete
- Requires `MessageDispatcher` to integrate with `IAggregatorPersistor` — the dispatcher must detect `Aggregator<T>` handlers, store partial messages, and invoke the aggregator's `Execute()` when `BatchSize()` is reached or `Timeout()` expires
- **Blocked on**: aggregator dispatcher integration design
