# Pre-release code review — round 2

**Date**: 2026-05-12 (same day as round 1, after 11 round-1 fixes applied and all tests green)
**Branch**: `v7-clean-architecture`
**Method**: 5 parallel reviewers — (1) audit of the round-1 fixes themselves, (2) filter/middleware/dispatch pipeline, (3) hosted services + lifecycle wiring, (4) cross-cutting concerns (logging, exceptions, CT propagation, leaks), (5) outbound send pipeline + serializer.
**Round-1 cross-check**: each agent was given `2026-05-12-pre-release-review.md` and instructed to skip duplicates.

## Top-line

Round 2 surfaced **15 Important findings, 2 Medium logging issues, and ~20 Minor issues**. The most consequential is the **MongoDb saga unique-index initialiser running after `BusHostedService` starts consuming** — the cross-process new-saga insert race the indexes were created to defend against is still open during cold-start. Two of the round-1 fixes have follow-on issues that should be tightened (the lifecycle semaphore in `Consumer.cs` covers the success path but not the failure-recovery dispose; the `BusConfiguration.Freeze` covers scalars but leaves the pipeline filter/middleware lists mutable). Several pre-existing issues that round 1 did not cover came up: poison-payload classification via `SerializationException` falls through to the transient-retry path; outbound calls from `IMessageProcessingMiddleware` bypass the routing-slip hop counter; `PublishOptions.RoutingKey` is stamped into headers but never reaches the wire.

---

## Critical — none

No data-loss-or-crash bugs. The Mongo index ordering issue below comes closest but is a window-narrowing problem rather than a guaranteed corruption.

---

## Important

### I1. MongoDb saga unique-index init runs AFTER `BusHostedService` starts consuming
**File**: [src/ServiceConnect.Persistence.MongoDb/MongoDbPersistenceExtensions.cs:139](src/ServiceConnect.Persistence.MongoDb/MongoDbPersistenceExtensions.cs#L139) (registration order) vs [src/ServiceConnect/DependencyInjection/ServiceCollectionExtensions.cs:172](src/ServiceConnect/DependencyInjection/ServiceCollectionExtensions.cs#L172)
**What**: `AddHostedService<MongoDbProcessManagerIndexInitializer>()` appends during `builder.AdditionalRegistrations` which runs AFTER `RegisterBus` has already added `BusHostedService`. `IHost` starts hosted services in registration order, so consuming goes online before unique CorrelationId indexes are created.
**Why it matters**: Defeats the documented purpose of the initialiser. Two cold-started services on the same Mongo can both insert duplicate saga rows on the first traffic burst — exactly the cross-process race the indexes were created to defend against.
**Fix**: `services.Insert(0, ServiceDescriptor.Singleton<IHostedService, MongoDbProcessManagerIndexInitializer>())` so the initialiser runs ahead of `BusHostedService`.

### I2. `ProcessManagerTimeoutService.StopAsync` disposes `_stoppingCts` while the poll task may still read `cts.Token`
**File**: [src/ServiceConnect/Services/ProcessManagerTimeoutService.cs:86-91](src/ServiceConnect/Services/ProcessManagerTimeoutService.cs#L86-L91)
**What**: When the host grace `cancellationToken` cancels the `_pollingTask.WaitAsync(cancellationToken)` call, the catch swallows the OCE and proceeds to `cts.Dispose()` — but the poll loop is still running and will read `_disposeCts.Token` on its next iteration, hitting `ObjectDisposedException`.
**Why it matters**: Manifests as `ObjectDisposedException` logged as "poll loop terminated unexpectedly" on every shutdown that hits the grace deadline; any in-flight Mongo call linked to the disposed token throws.
**Fix**: Don't dispose the CTS on the timeout path; let GC reclaim. Or attach a continuation that disposes after `_pollingTask` actually completes.

### I3. `MessageDispatcher` poison-payload classification misses `SerializationException`
**File**: [src/ServiceConnect/Services/MessageDispatcher.cs:203](src/ServiceConnect/Services/MessageDispatcher.cs#L203), [src/ServiceConnect/Services/SystemTextJsonMessageSerializer.cs](src/ServiceConnect/Services/SystemTextJsonMessageSerializer.cs)
**What**: The terminal-failure filter is `catch (Exception ex) when (ex is JsonException or NotSupportedException)`. The serializer wraps `JsonException` as `ServiceConnect.Interfaces.Exceptions.SerializationException`, so the dispatcher never sees the inner `JsonException` directly — the wrapped throw falls through to the generic `catch (Exception)` branch which marks the message as a TRANSIENT failure → broker nack/requeue → retry-budget burn on a permanently-poisoned payload.
**Why it matters**: Defeats the dispatcher's explicit poison short-circuit. Operators chasing a malformed-payload incident see N retry attempts where they should see one terminal-failure log + DLQ.
**Fix**: Add `SerializationException` to the terminal-failure `when` clause.

### I4. `PublishOptions.RoutingKey` is stamped into headers but never reaches the AMQP wire
**File**: [src/ServiceConnect.Interfaces/Bus/IProducer.cs:15](src/ServiceConnect.Interfaces/Bus/IProducer.cs#L15) (`PublishAsync` signature has no `routingKey` param), [src/ServiceConnect/Services/SendMessagePipeline.cs:62](src/ServiceConnect/Services/SendMessagePipeline.cs#L62) (call site), [src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs:275-311](src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs#L275)
**What**: `Bus.PublishAsync` writes `HeaderKeys.RoutingKey` into headers and the `SendContext.RoutingKey` slot, but `IProducer.PublishAsync` has no `routingKey` parameter and the RabbitMQ producer hard-codes `routingKey: string.Empty` to the fanout exchange. Topic-style routing via the option is silently dropped.
**Why it matters**: The API and the wire diverge silently. A caller specifying `RoutingKey = "foo.bar"` for topic routing gets fanout dispatch.
**Fix**: Either thread `routingKey` through `IProducer.PublishAsync` (and `BasicPublishAsync`), or reject non-empty `PublishOptions.RoutingKey` at the `Bus` surface with a clear error message.

### I5. `IMessageProcessingMiddleware` outbound calls bypass the routing-slip hop counter
**File**: [src/ServiceConnect/Services/MessageDispatcher.cs:337-353](src/ServiceConnect/Services/MessageDispatcher.cs#L337-L353) (middleware chain build) vs [src/ServiceConnect/Services/Processors/HandlerProcessor.cs:77](src/ServiceConnect/Services/Processors/HandlerProcessor.cs#L77) (where ambient headers are pushed)
**What**: `ConsumeContextAccessor.Push(headers)` happens inside `HandlerProcessor` and `ProcessManagerProcessor`, NOT in `MessageDispatcher`. Middleware runs BEFORE either processor, so a middleware that calls `Bus.RouteAsync` / `Bus.SendAsync` sees `CurrentHeaders == null` — `ReadInboundHopsCompleted` returns 0, and the outbound stamp is always `1` regardless of the inbound hop count.
**Why it matters**: `MaxRoutingSlipHops` is the only cross-service amplification defence. A user "auto-forward" middleware silently resets the hop counter — a flow that should terminate after 32 hops circulates indefinitely.
**Fix**: Push `ConsumeContextAccessor` in `MessageDispatcher.DispatchAsync` immediately after the envelope is built, before any middleware runs.

### I6. Pre-deserialisation processor success skips `OnConsumedSuccessfullyFilters`
**File**: [src/ServiceConnect/Services/MessageDispatcher.cs:105-109](src/ServiceConnect/Services/MessageDispatcher.cs#L105-L109)
**What**: When a pre-deserialisation processor (e.g. `StreamProcessor` accepting a stream-packet frame) returns `Handled`, the dispatcher short-circuits with `Success=true` and only runs `AfterConsumingFilters`. The reply branch and the handler-success branch both run `OnConsumedSuccessfullyFilters`; this branch does not.
**Why it matters**: User filters built on the `OnConsumedSuccessfully` stage for dedup-key recording, audit, or outbox commit silently miss every successful stream-frame consumption.
**Fix**: Run `ExecuteOnConsumedSuccessfullyFiltersAsync` on the `preDeserHandled` branch before the early return.

### I7. `BusConfiguration.Freeze()` covers scalars only — `PipelineConfiguration` filter / middleware lists stay mutable
**File**: [src/ServiceConnect/Configuration/BusConfiguration.cs:80](src/ServiceConnect/Configuration/BusConfiguration.cs#L80) (Freeze method) vs [src/ServiceConnect/Configuration/PipelineConfiguration.cs:20-40](src/ServiceConnect/Configuration/PipelineConfiguration.cs#L20-L40)
**Introduced by**: Round-1 B1 fix
**What**: The freeze rejects post-startup mutation of `DisposeTimeout`, `MaxRoutingSlipHops`, etc., but `Pipeline.OutgoingFilters`, `BeforeConsumingFilters`, `AfterConsumingFilters`, `OnConsumedSuccessfullyFilters`, `MessageProcessingMiddleware`, `SendMessageMiddleware` are still mutable `IList<Type>` exposed publicly. The DI registration shares the same instance by reference, so anyone resolving `IPipelineConfiguration` can append a filter type post-startup. The startup validator (`ValidateTypesRegistered`) has already run, so an appended type that's not in DI throws `InvalidOperationException` at every subsequent dispatch.
**Why it matters**: Same class of bug B1 was meant to prevent. The defence is partial.
**Fix**: Freeze the pipeline collections at the same point in `AddServiceConnect`. Either add a `_pipelineFrozen` flag with throwing `IList.Add/Insert/Remove/Clear` overrides, or replace the lists with `ImmutableArray<Type>` on freeze.

### I8. `Consumer.StartConsumingAsync` failure-recovery path holds the lifecycle semaphore through full host teardown
**File**: [src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs:240-273](src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs#L240-L273)
**Introduced by**: Round-1 R3 fix
**What**: The catch block in `StartConsumingAsync` (which runs while still holding `_lifecycleSemaphore`) serially disposes every partially-built host (each bounded by `gracefulShutdownTimeoutMs`, default 5s) and then the owned connection. With `ConsumerCount > 1` the cumulative dispose can exceed `DisposeTimeout`, causing the outer `Consumer.DisposeAsync` to log "timed out waiting" and force-tear-down `_model`/`_connection` while the failure recovery is still using them.
**Why it matters**: The lifecycle semaphore was added to prevent this very race; it covers the success path but not the failure-recovery path.
**Fix**: Release the lifecycle semaphore at the top of the catch (before the recovery dispose loop), OR use `Task.WhenAll` for per-host disposals and a separate budget for the connection close.

### I9. `ConsumeScopeAccessor.Push` leaks the scope to fire-and-forget continuations after dispatch completes
**File**: [src/ServiceConnect/Services/ConsumeScopeAccessor.cs:38-50](src/ServiceConnect/Services/ConsumeScopeAccessor.cs#L38-L50)
**What**: `Push` returns a `Popper` that on `Dispose` writes `_current.Value = previous`. AsyncLocal writes only propagate down the current `ExecutionContext`; if a handler started a fire-and-forget task while the scope was live, that task continues seeing the pushed scope after the using-block disposes. The DI scope itself is disposed in `MessageDispatcher`, so the leaked continuation holds an `IServiceProvider` whose backing scope has been disposed.
**Why it matters**: Anyone resolving via `_scopeAccessor.Current` from a leaked continuation (filters, middleware, telemetry) hits `ObjectDisposedException` on `GetService`. Surfaces as confusing post-dispatch errors.
**Fix**: At minimum, capture this in xmldoc as a hard constraint ("must not be read from fire-and-forget tasks"). Stronger: make `Popper.Dispose` null-out the value when the disposed scope is the current one and log a warning if a leaked reader observes a disposed scope.

### I10. `MessageTypeRegistry.Register` is partially non-atomic on FullName/AQN collisions
**File**: [src/ServiceConnect/Services/MessageTypeRegistry.cs:68-82](src/ServiceConnect/Services/MessageTypeRegistry.cs#L68-L82)
**What**: `Register` adds to the AQN-keyed dict first, then the FullName-keyed dict. If two distinct types share `FullName` but not AQN (different versions of the same assembly), the AQN add succeeds and the FullName add throws — leaving the registry half-populated. The cache invalidation (`Volatile.Write(ref _types, null)`) only runs after both succeed.
**Why it matters**: Inconsistent registry state under collision. Subsequent `TryResolve` calls return different results depending on lookup path; hard to debug.
**Fix**: Validate both candidate keys against a temporary dict first; commit to `_registeredTypes` only when both pass. Or roll back the AQN entry on FullName failure.

### I11. `messageType` dispatcher parameter is the operation discriminator, not the CLR type — misleading "unregistered type" log
**File**: [src/ServiceConnect/Services/MessageDispatcher.cs:68-115](src/ServiceConnect/Services/MessageDispatcher.cs#L68-L115)
**What**: The dispatcher takes a `messageType` parameter from the consumer host; the host derives it from the `MessageType` header which carries `"Publish" / "Send" / "ByteStream"` per `OutboundHeaderBuilder` line 80 — i.e. the OPERATION name, not the CLR type. The dispatcher tries this against the registry first, always misses, then falls back to `FullTypeName`/`TypeName`. Today it's just a wasted lookup, BUT the unregistered-type Warning log interpolates this value, telling operators "Unregistered message type 'Publish'" instead of the actual CLR type that failed to resolve.
**Why it matters**: Misleading triage signal — operators chasing a poison message see "Publish" / "Send" instead of the type name.
**Fix**: Skip the primary-candidate lookup when the value matches a known operation name, or rename the parameter and re-order so the AQN/FullName header is tried first.

### I12. `_consumer` field is never nulled in `RabbitMqConsumerHost.DisposeAsync`
**File**: [src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs:732-748,886-887](src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L732-L748)
**Introduced by**: Round-1 R1 (lambda capture); pre-existing asymmetry surfaced by it
**What**: The unsubscribe correctly does `_consumer.ReceivedAsync -= _receivedHandler; _receivedHandler = null;`, but the `_consumer` field is never nulled. The asymmetric `_model` and `_publishChannel` are both nulled by their respective close methods. The reference to `AsyncEventingBasicConsumer` (and its closure over `_model`) remains GC-rooted via the host.
**Why it matters**: Lifetime leak that defeats part of the R1 fix's GC-retention goal.
**Fix**: `_consumer = null;` immediately after the `ReceivedAsync -=` block.

### I13. Cancellation is not checked between pre-deserialisation processors
**File**: [src/ServiceConnect/Services/MessageDispatcher.cs:290-315](src/ServiceConnect/Services/MessageDispatcher.cs#L290-L315)
**What**: `RunPreDeserializationProcessorsAsync` iterates `_processors` without `cancellationToken.ThrowIfCancellationRequested()` between iterations. If a processor returns `NotHandled` synchronously, shutdown cancellation won't propagate until the next `await`.
**Why it matters**: Shutdown responsiveness regression on a busy broker — low-impact today since all built-in processors await internally, but a new synchronous processor would be invisibly uncancellable.
**Fix**: `cancellationToken.ThrowIfCancellationRequested();` at the top of each loop iteration in both `RunPreDeserializationProcessorsAsync` and `RunProcessors`.

### I14. `SendToManyAsync` aggregates middleware throws as per-endpoint failures even when no publish happened
**File**: [src/ServiceConnect/Bus.cs:301-304](src/ServiceConnect/Bus.cs#L301-L304) and [src/ServiceConnect/Services/SendMessagePipeline.cs:104-110](src/ServiceConnect/Services/SendMessagePipeline.cs#L104-L110)
**What**: When a `ISendMessageMiddleware` throws BEFORE delegating to `next` (e.g. throttle exhausted), the terminal producer call never runs. But the catch wraps the throw into `endpointFailures` and the next endpoint iteration tries again, getting the same exception. If the middleware deterministically throws, all N endpoints fail identically and the final `AggregateException` has N copies of the same exception.
**Why it matters**: Masks the root cause (a single middleware misconfig) under N stacked exceptions; burns wall-clock and metric points on a fault that will never resolve mid-call.
**Fix**: Track whether the terminal producer call actually started for the first endpoint; if a middleware throw short-circuits before it does, abort the fan-out with the single exception.

### I15. Header type drift: `Bus` lossily coerces all values to string, but the wire wants typed AMQP values
**File**: [src/ServiceConnect/Bus.cs:984-1007](src/ServiceConnect/Bus.cs#L984-L1007) (`ExtractHeaders`), [src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs:49-97](src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs#L49)
**What**: `Bus.ExtractHeaders` collapses every envelope header to a string via `IFormattable.ToString(null, Invariant)`. `OutboundHeaderBuilder.BuildHeaders` accepts `IReadOnlyDictionary<string, string>?` so even a downstream caller bypassing Bus has no path for typed values. Receivers always see strings, never AMQP-native numeric/binary types.
**Why it matters**: Wire-protocol compatibility with non-ServiceConnect AMQP consumers expecting typed headers (e.g. integer `x-message-ttl` from third parties) is broken on every outgoing publish.
**Fix**: Either thread `IReadOnlyDictionary<string, object?>` end-to-end from Bus → SendContext → Producer, or document "strings-only headers" as a deliberate ServiceConnect wire contract.

---

## Medium — diagnostic quality regressions

### M1. `RabbitMqTopologyProvisioner` logs `ex.Message` (string) instead of the exception object — loses stack trace
**File**: [src/ServiceConnect.Client.RabbitMQ/Topology/RabbitMqTopologyProvisioner.cs:51,86,115,125,137,177,187,208](src/ServiceConnect.Client.RabbitMQ/Topology/RabbitMqTopologyProvisioner.cs#L51)
**What**: Eight `_logger.LogWarning("Error ... {Message}", ..., ex.Message)` calls pass the message string instead of the exception. Loses `OperationInterruptedException.ShutdownReason.ReplyCode` / `ReplyText` and the stack.
**Fix**: `_logger.LogWarning(ex, "Error declaring queue {QueueName}", queueName)` (drop the `{Message}` template parameter).

### M2. Same topology-provisioner sites log twice (warn-then-rethrow) for the same exception
**File**: same — every catch is `LogWarning(...); throw;` and the outer `RabbitMqConsumerHost`/`Consumer` catches re-log at Warning/Error.
**What**: Duplicate stack traces in logs obscure incident timelines and inflate log volume per failure burst.
**Fix**: Drop the inner log (the rethrow already preserves the stack) or demote to Debug.

---

## Minor (the long tail — defer or fix opportunistically)

Grouped by area; agent reports have full reasoning.

**Audit of round-1 fixes**
- `Consume()` enrichment-catch's `SetTag("enrichment.exception", ...)` could itself throw if a custom listener rethrows — wrap in a nested `try{}catch{}` ([ServiceConnectActivitySource.cs:220-227](src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs#L220-L227))
- `Connection.cs` orphan-teardown doesn't detach lifecycle handlers — asymmetric with normal teardown, fragile against future Attach reorderings
- `MongoDbAggregatorPersistor.RemoveDataAsync` lease-violation warning uses client clock `DateTime.UtcNow` instead of server `$$NOW` — false-negative under skew ([MongoDbAggregatorPersistor.cs:451-458](src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs#L451))
- Mongo `GetSnapshotAsync` orphan-release runs outside session — bounded by lease duration, suboptimal under failover ([MongoDbAggregatorPersistor.cs:346-364](src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs#L346))
- `StreamProcessor` PacketNumber out-of-range path emits no counter — hostile probes silently absorbed ([StreamProcessor.cs:129-133](src/ServiceConnect/Services/Processors/StreamProcessor.cs#L129))
- `DefaultProcessManagerPropertyMapper.PropertiesHierarchy` is `Dictionary<string,Type>` — same-name parts in nested chains collapse (`d => d.Foo.Foo` becomes single-step) ([DefaultProcessManagerPropertyMapper.cs:16,42](src/ServiceConnect/Services/Processors/DefaultProcessManagerPropertyMapper.cs#L16))

**Filter / middleware / dispatch**
- `IFilter` xmldoc doesn't document expected lifetime — DI gives Transient by default; in-memory state on a Transient filter resets per dispatch
- `SendMessagePipeline.WrapMiddleware` lazily resolves once; chain build is "first-publish-wins" rather than eager. Move to eager init or document
- `SendMessagePipeline.ToReadOnly` allocates on every send when caller swaps headers to a non-`Dictionary<,>` shape (e.g. `ConcurrentDictionary`)
- `Bus.ExtractHeaders` silently coerces null → empty string for header values
- `MessageDispatcher` `AfterConsumingFilters` swallowed-exception log lacks dispatch context (success/fail, messageType)
- `SendMessagePipeline.DisposeAsync` only flips `_disposed` — drop `IAsyncDisposable` or document why
- `MessageDispatcher` "no processor handled" log fires at Warning unconditionally — should be Debug for registered-but-not-handled, Warning for unregistered

**Hosted services / lifecycle**
- `ConsumeContextAccessor.Scope.Dispose` is not thread-safe (plain bool flag) — inconsistent with `ConsumeScopeAccessor.Popper` ([ConsumeContextAccessor.cs:24-33](src/ServiceConnect/Services/ConsumeContextAccessor.cs#L24-L33))
- `ConsumeContextPool` retains the previous message's headers/bus reference between Release and Rent — bounded but unnecessary retention ([ConsumeContextPool.cs:172](src/ServiceConnect/Services/ConsumeContextPool.cs#L172))
- `StreamProcessor.DisposeAsync` doesn't await in-flight handler invocations (compare to `AggregatorProcessor` which tracks `_activeFlushes`)
- `BusHostedService.StopAsync` not idempotent — cosmetic
- `BusAccessor.Set` lacks CAS double-write detection — diagnostic

**Send pipeline / serializer**
- Serializer rebuilds `JsonReaderState` per call (`SystemTextJsonMessageSerializer.cs:120-128`) — cosmetic vs the existing `OutboundHeaderBuilder.TypeNameCache` style
- Serializer's `Serialize<T>` uses `message.GetType()` — polymorphic but interacts badly with `new`-hiding properties
- Send middleware silently promoted to singleton lifetime via Lazy resolution — fail loudly at registration if non-singleton
- `OutboundHeaderBuilder.OverwrittenHeaderKeys` doesn't include `RoutingSlip` / `RoutingSlipHopsCompleted` — middleware writing these silently corrupts framework state ([OutboundHeaderBuilder.cs:27-38](src/ServiceConnect.Client.RabbitMQ/Producer/OutboundHeaderBuilder.cs#L27))
- STJ `IncludeFields = false` permanently — fields on record types silently drop on the wire

---

## Themes worth a session

1. **Mutable-state escape part 2.** Round-1 B1 (Freeze BusConfiguration scalars) was partial — the pipeline filter/middleware lists are the actual dispatch-driving collections and they're still mutable. Add a freeze pass to `PipelineConfiguration` / `ITransportConfiguration` collections as well.

2. **Round-1 fixes' second-order effects.** R3 (lifecycle semaphore) introduced I8; R1 (lambda capture) surfaced I12. Worth a focused pass through each round-1 fix to verify the failure-recovery / asymmetric-cleanup paths.

3. **Hop counter / amplification controls.** I5 (middleware bypasses hop counter) and the OutboundHeaderBuilder mutable-keys gap (Minor) both bypass the routing-slip cap. Recommend a single audit of "who is authorised to stamp the hops header" and reject all other writers.

4. **Header type system.** I15 (Bus → string-only) and the related Minor on `BasicPropertiesCopier` aliasing (round 1) both stem from ambiguity about whether ServiceConnect commits to "strings only on the wire" or AMQP-native typed values. Either way, the contract should be explicit and enforced end-to-end, not silently lossy.

---

## Coverage gaps still unaddressed

- **Examples** (`examples/`) — not reviewed in either round.
- **E2E test design quality** — not reviewed in either round; have any tests been written that mask bugs (e.g. assertions that always pass under the InMemory persistor)?
- **Unit test gap-fill** — same.
- **Docs site** (`website/`) — not reviewed.
- **Serialization compat tests** — round-1 covered the serializer but not the compat-test design itself.

If any of these need a pass, ask for a focused review.
