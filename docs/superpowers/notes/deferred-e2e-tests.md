# Deferred E2E Tests

## StreamingTests
- `Bus.CreateStream<T>()` currently throws `NotImplementedException`
- Needs a design for how streaming integrates with the RabbitMQ transport (chunked messages via `SendBytesAsync`, `IMessageBusWriteStream`, `IStreamHandler`)
- The `IProducer.SendBytesAsync()` and header keys (`SequenceId`, `PacketNumber`, `LastPacketNumber`) suggest a chunking protocol was planned
- **Blocked on**: implementing `CreateStream<T>()` in Bus and a corresponding stream receiver on the consumer side

## ScatterGatherTests
- Requires `SendRequestMultiAsync<T, TReply>` to work end-to-end with real RabbitMQ consumers
- Multiple responder bus instances must each receive the request (via pub/sub), call `context.Reply()`, and the requester must collect all replies
- `RequestReplyManager.SendRequestMultiAsync` exists and uses `ExpectedReplyCount` + `ConcurrentBag<TReply>` — the logic is there
- **Blocked on**: basic request/reply E2E working first, then spinning up multiple responder bus instances in a single test

## MessageDeduplicationTests
- Needs the `ServiceConnect.Filters.MessageDeduplication` filter project wired into the E2E test
- Tests would verify: send same message twice with `Redelivered` header, handler only invoked once
- **Blocked on**: consumer wiring (in progress) + adding the dedup filter project as a dependency of the E2E test project

## ProcessManagerTests
- Multi-step saga/workflow: send start message, handler creates process manager state in MongoDB, send second message, handler reads and updates state, verify final state
- Requires `MessageDispatcher` to integrate with `IProcessManagerFinder` — currently the dispatcher routes to `IMessageHandler<T>` but doesn't know about process managers
- Needs: a way for the dispatcher to detect process manager handlers (implement `IProcessManagerHandler<T>` or similar), call `IProcessManagerFinder.FindData()` before invoking, and `UpdateData()` / `InsertData()` after
- **Blocked on**: consumer wiring + process manager dispatcher integration design

## AggregatorTests
- Partial messages collected, aggregated result emitted when batch complete
- Requires `MessageDispatcher` to integrate with `IAggregatorPersistor` — the dispatcher must detect `Aggregator<T>` handlers, store partial messages, and invoke the aggregator's `Execute()` when `BatchSize()` is reached or `Timeout()` expires
- **Blocked on**: consumer wiring + aggregator dispatcher integration design

## PolymorphicMessageTests
- Handler registered for base type receives derived message type
- Requires `MessageDispatcher` to resolve handlers not just for the exact message type but also for base types in the hierarchy
- **Blocked on**: consumer wiring + enhancing MessageDispatcher handler resolution to walk the type hierarchy
