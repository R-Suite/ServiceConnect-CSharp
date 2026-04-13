# Processor Registry Refactor — R-009 (Group C-4)

**Status:** Design approved 2026-04-13

**Goal:** Eliminate dispatch-time reflection (`GetMethod`/`GetProperty`/`MethodInfo.Invoke`) and ancillary service-locator calls from `HandlerProcessor`, `StreamProcessor`, and `AggregatorProcessor` by introducing one registry-of-compiled-delegates per processor. Mirrors the pattern already shipped for `ProcessManagerProcessor` in Group C-3.

---

## Scope

**In scope:**

1. `src/ServiceConnect/Services/Processors/HandlerProcessor.cs` — rewire dispatch to `MessageHandlerRegistry` (new).
2. `src/ServiceConnect/Services/Processors/StreamProcessor.cs` — rewire completion branch to `StreamHandlerRegistry` (new).
3. `src/ServiceConnect/Services/Processors/AggregatorProcessor.cs` — rewire dispatch and flush to `AggregatorRegistry` (new).
4. Extend `Bus.StartConsumingAsync` warm-up to resolve the three new registries.
5. Update the three existing processor unit test files to construct and inject the new registries, and add one new test file per registry.

**Out of scope:**

- `ReplyProcessor` — already uses proper constructor DI, no reflection, not a candidate.
- Transport-agnostic refactoring of dispatch.
- Any change to `IMessageHandler<T>`, `IStreamHandler<T>`, `Aggregator<T>`, or `HandlerReference`.
- Renaming or moving the processors as public types.

**Success criteria:**

- No `MethodInfo.Invoke` or `GetProperty`/`SetValue` inside any of the three processors (`ForwardRoutingSlipAsync` in `HandlerProcessor` keeps its existing `MakeGenericMethod` — generic method dispatch on `IBus.RouteAsync` with no descriptor to compile against; this is a separate, small pattern not covered by R-009).
- `HandlerProcessor`, `StreamProcessor`, and `AggregatorProcessor` acquire handler-type metadata from their registries, not from `IServiceProvider` at dispatch time.
- Duplicate stream-handler or aggregator-to-message-type mappings fail fast at `Bus.StartConsumingAsync`, not at mid-traffic dispatch.
- All unit tests + E2E suite (`sg docker -c "dotnet test src/ServiceConnect.EndToEndTests -v quiet"`) green.

---

## Architecture

Three new `internal sealed class` registries live beside `ProcessManagerHandlerRegistry` in `src/ServiceConnect/Services/Processors/`:

```
Services/Processors/
├── ProcessManagerHandlerRegistry.cs         (exists)
├── ProcessManagerDescriptor.cs              (exists)
├── MessageHandlerRegistry.cs                (new)
├── MessageHandlerDescriptor.cs              (new)
├── StreamHandlerRegistry.cs                 (new)
├── StreamHandlerDescriptor.cs               (new)
├── AggregatorRegistry.cs                    (new)
└── AggregatorDescriptor.cs                  (new)
```

All three registries:

- `internal sealed class`, registered as singletons via explicit factory lambda in `ServiceCollectionExtensions.AddServiceConnect` (internal accessibility precludes the default public-ctor scan; matches `ProcessManagerHandlerRegistry`'s existing registration).
- Ctor takes `(IList<HandlerReference> handlerRefs, ILogger<T> logger)` — the `AggregatorRegistry` additionally takes `IServiceProvider` to materialize each aggregator once at startup for `BatchSize`/`Timeout` capture.
- Construction filters `HandlerReferences` to the kinds each cares about, builds one descriptor per distinct key, stores in a read-only dictionary.
- Each exposes a `TryGet(Type, [NotNullWhen(true)] out TDescriptor?)`.

The processors still resolve *handler instances* per dispatch via `IServiceProvider.GetService(descriptor.HandlerInterfaceType)` — unavoidable because dispatch is by runtime message type. What R-009 removes is the *reflection* (method/property lookup + `Invoke`) and the per-dispatch `MakeGenericType` walk that previously happened in every processor hot path.

### `InternalsVisibleTo`

`src/ServiceConnect/ServiceConnect.csproj` already declares `InternalsVisibleTo("ServiceConnect.UnitTests")`, allowing the unit tests to construct the internal registries directly. No project-file change required.

---

## `MessageHandlerRegistry` (for `HandlerProcessor`)

Hybrid pre-compute + lazy. Keyed by **message type**; each descriptor describes the closed-generic `IMessageHandler<T>` interface for that message type (not per handler instance — there may be multiple handler classes implementing that same interface, all described by one descriptor).

### `MessageHandlerDescriptor.cs`

```csharp
internal sealed record MessageHandlerDescriptor(
    Type MessageType,                                    // e.g. OrderCreated
    Type HandlerInterfaceType,                           // IMessageHandler<OrderCreated>
    Action<object, IConsumeContext> SetContext,          // Context property setter, compiled
    Func<object, object, Task> InvokeHandleAsync);       // handler.HandleAsync(message), compiled
```

Both delegates are built via `Expression.Lambda<T>.Compile()` closed over the concrete `TMessage` — no `MethodInfo.Invoke` or `SetValue` per dispatch.

### `MessageHandlerRegistry.cs`

- **State:** `ConcurrentDictionary<Type, MessageHandlerDescriptor?>` — nullable value encodes negative-cache entries (types for which a descriptor cannot be built; e.g. `typeof(Message)`, `typeof(object)`).
- **Ctor:** walks `HandlerReferences`, selects those whose `HandlerType` implements at least one `IMessageHandler<T>` closed-generic interface (ignores `IProcessHandler`, `IStreamHandler`, `Aggregator`), and pre-builds one descriptor per distinct `MessageType`. Does **not** throw on duplicates — multiple handler classes for the same message type are legitimate (all pulled via `serviceProvider.GetServices(interfaceType)`), and one descriptor per interface suffices.
- **Public API:** `bool TryGetOrBuild(Type messageType, [NotNullWhen(true)] out MessageHandlerDescriptor? descriptor)`. If the type is not in the map, attempts to build on the fly (covers ancestor types during `HandlerProcessor`'s base-walk, or derived types not listed in `HandlerReferences`). Negative results are cached to prevent repeat work on hot hierarchies (e.g. `typeof(Message)` will be hit on every dispatch).

### Descriptor construction — the compiled setters/invokers

```csharp
// SetContext: (object handler, IConsumeContext ctx) => ((IMessageHandler<TMsg>)handler).Context = ctx;
var handlerParam = Expression.Parameter(typeof(object), "handler");
var ctxParam = Expression.Parameter(typeof(IConsumeContext), "ctx");
var typedHandler = Expression.Convert(handlerParam, handlerInterfaceType);
var contextProp = Expression.Property(typedHandler, "Context");
var setContext = Expression.Lambda<Action<object, IConsumeContext>>(
    Expression.Assign(contextProp, ctxParam), handlerParam, ctxParam).Compile();

// InvokeHandleAsync: (object handler, object message) => ((IMessageHandler<TMsg>)handler).HandleAsync((TMsg)message);
var messageParam = Expression.Parameter(typeof(object), "message");
var typedMessage = Expression.Convert(messageParam, messageType);
var handleCall = Expression.Call(typedHandler, "HandleAsync", Type.EmptyTypes, typedMessage);
var invokeHandle = Expression.Lambda<Func<object, object, Task>>(
    handleCall, handlerParam, messageParam).Compile();
```

### `HandlerProcessor` — post-refactor dispatch

```csharp
public sealed class HandlerProcessor(
    MessageHandlerRegistry registry,
    IServiceProvider serviceProvider) : IMessageProcessor
{
    public async Task<ProcessResult> ProcessAsync(
        byte[] messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (message == null) return ProcessResult.NotHandled;

        var invocations = new List<(object Handler, MessageHandlerDescriptor Descriptor)>();
        var checkedType = messageType;
        while (checkedType != null && checkedType != typeof(Message) && checkedType != typeof(object))
        {
            if (registry.TryGetOrBuild(checkedType, out var descriptor))
            {
                foreach (var h in serviceProvider.GetServices(descriptor.HandlerInterfaceType))
                    if (h != null) invocations.Add((h, descriptor));
            }
            checkedType = checkedType.BaseType;
        }

        if (invocations.Count == 0) return ProcessResult.NotHandled;

        var bus = serviceProvider.GetRequiredService<IBus>();
        var context = new ConsumeContext(bus, headers) { CancellationToken = cancellationToken };

        foreach (var (handler, descriptor) in invocations)
        {
            descriptor.SetContext(handler, context);
            await descriptor.InvokeHandleAsync(handler, message).ConfigureAwait(false);
        }

        await ForwardRoutingSlipAsync(message, messageType, headers, bus, cancellationToken).ConfigureAwait(false);
        return ProcessResult.Handled;
    }

    // ForwardRoutingSlipAsync unchanged — it's a single MakeGenericMethod on IBus.RouteAsync,
    // separate concern from R-009, out of scope.
}
```

---

## `StreamHandlerRegistry` (for `StreamProcessor`)

Pure pre-compute. Keyed by message type. One handler per type — duplicates throw `InvalidOperationException` at construction.

### `StreamHandlerDescriptor.cs`

```csharp
internal sealed record StreamHandlerDescriptor(
    Type MessageType,
    Type HandlerInterfaceType,                          // IStreamHandler<TMsg>
    Action<object, MessageBusReadStream> SetStream,     // Stream property setter
    Func<object, object, Task> InvokeExecute);          // Execute(message), Task-wrapped
```

`IStreamHandler<T>.Execute` may return `Task`, `Task<T>`, or (legacy) `void`. The compiled `InvokeExecute` normalizes all three: if the `Execute` method's declared return type is assignable to `Task`, the delegate returns the result directly; if the return type is `void`, the delegate calls `Execute` and returns `Task.CompletedTask`. Callers can always `await` the result.

### `StreamHandlerRegistry.cs`

- **Ctor:** walks `HandlerReferences`, selects entries whose `HandlerType` implements some `IStreamHandler<T>`. Builds one descriptor per message type. Throws `InvalidOperationException` on duplicate `MessageType` with two distinct handler types. (Two references with the same handler class type for the same message are deduplicated silently — `HandlerScanner` can produce duplicates when a handler implements multiple interfaces.)
- **Public API:** `bool TryGet(Type messageType, [NotNullWhen(true)] out StreamHandlerDescriptor? descriptor)`.

### `StreamProcessor` — post-refactor completion branch

```csharp
if (stream.IsComplete())
{
    // ... remove stream from active-streams dict, existing code unchanged ...

    if (!headers.TryGetValue(HeaderKeys.FullTypeName, out var ftnRaw))
    {
        logger.LogWarning("Completed stream {SequenceId} missing FullTypeName header", sequenceId);
        return ProcessResult.Handled;
    }

    var fullTypeName = HeaderDecoder.Decode(ftnRaw);
    if (!typeRegistry.TryResolve(fullTypeName!, out var resolvedType))
    {
        logger.LogWarning("Unregistered type '{TypeName}' for completed stream. Rejecting", fullTypeName);
        return ProcessResult.Handled;
    }

    if (!registry.TryGet(resolvedType, out var descriptor))
    {
        logger.LogWarning("No IStreamHandler registered for {MessageType}", resolvedType.FullName);
        return ProcessResult.Handled;
    }

    var handler = serviceProvider.GetService(descriptor.HandlerInterfaceType);
    if (handler == null) return ProcessResult.Handled;

    descriptor.SetStream(handler, stream);

    var serializer = serviceProvider.GetRequiredService<IMessageSerializer>();
    var assembledBytes = stream.Read();
    var originalMessage = serializer.Deserialize(assembledBytes, resolvedType);

    await descriptor.InvokeExecute(handler, originalMessage!).ConfigureAwait(false);
}
```

Only structural change to `StreamProcessor`: ctor now takes `StreamHandlerRegistry`; all `MakeGenericType`/`GetProperty`/`GetMethod`/`Invoke` removed from the completion branch. Packet-accumulation and stream-eviction code paths unchanged.

---

## `AggregatorRegistry` (for `AggregatorProcessor`)

Pure pre-compute. Keyed by message type. One aggregator per message type — duplicates throw `InvalidOperationException` at construction.

`BatchSize` and `Timeout` are **resolved once at startup** — not per dispatch. This requires the registry to materialize each `Aggregator<T>` once via the `ServiceProvider`, read `BatchSize()` and `Timeout()`, and discard the reference. This is the intended semantics: both values are configuration, not runtime state, and the existing code invokes them on every message wastefully.

### `AggregatorDescriptor.cs`

```csharp
internal sealed record AggregatorDescriptor(
    Type MessageType,
    Type AggregatorBaseType,                            // closed Aggregator<TMsg>
    string AggregatorName,                              // AggregatorBaseType.FullName (non-null by construction)
    int BatchSize,
    TimeSpan Timeout,
    Func<IList<object>, IList> BuildTypedList,          // List<TMsg> factory, populated from raw
    Action<object, object> InvokeExecute);              // aggregator.Execute(typedList)
```

`BuildTypedList` takes the `IList<object>` returned from `IAggregatorPersistor.GetDataAsync` and returns a `List<TMsg>` populated from it. Compiled once per message type.

### `AggregatorRegistry.cs`

- **Ctor:** `(IList<HandlerReference>, IServiceProvider, ILogger<AggregatorRegistry>)`. Walks `HandlerReferences`, selects entries whose `HandlerType.BaseType` is a closed-generic `Aggregator<>`. For each distinct message type, materializes one instance via `IServiceProvider.GetRequiredService(aggregatorBaseType)`, reads `BatchSize` and `Timeout`, and builds a descriptor. Throws on duplicates.
- If an aggregator cannot be resolved (container returns null or throws), wraps the exception in `InvalidOperationException` with a clear message so startup fails with "AggregatorRegistry could not materialize aggregator for message X" rather than a runtime NRE.
- **Public API:** `bool TryGet(Type messageType, [NotNullWhen(true)] out AggregatorDescriptor? descriptor)`.

### `AggregatorProcessor` — post-refactor

```csharp
public sealed class AggregatorProcessor(
    AggregatorRegistry registry,
    IServiceProvider serviceProvider,
    ILogger<AggregatorProcessor> logger) : IMessageProcessor, IDisposable
{
    // _timers, _flushLock unchanged

    public async Task<ProcessResult> ProcessAsync(
        byte[] messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (message == null) return ProcessResult.NotHandled;

        if (!registry.TryGet(messageType, out var descriptor))
            return ProcessResult.NotHandled;

        var persistor = serviceProvider.GetService<IAggregatorPersistor>();
        if (persistor == null)
        {
            logger.LogWarning("IAggregatorPersistor not registered. Cannot aggregate {MessageType}", messageType.FullName);
            return ProcessResult.NotHandled;
        }

        await persistor.InsertDataAsync(message, descriptor.AggregatorName, cancellationToken).ConfigureAwait(false);

        var count = await persistor.CountAsync(descriptor.AggregatorName, cancellationToken).ConfigureAwait(false);
        if (descriptor.BatchSize > 0 && count >= descriptor.BatchSize)
        {
            if (_timers.TryRemove(descriptor.AggregatorName, out var timerToCancel))
                timerToCancel.Dispose();
            await FlushAggregatorAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }
        else if (descriptor.Timeout > TimeSpan.Zero)
        {
            lock (_flushLock)
            {
                if (_timers.TryRemove(descriptor.AggregatorName, out var existingTimer))
                    existingTimer.Dispose();
                _timers[descriptor.AggregatorName] = new Timer(
                    _ => OnTimerFired(descriptor), null, descriptor.Timeout, Timeout.InfiniteTimeSpan);
            }
        }

        return ProcessResult.Handled;
    }

    private async Task FlushAggregatorAsync(AggregatorDescriptor descriptor, CancellationToken cancellationToken)
    {
        lock (_flushLock)
        {
            if (_timers.TryRemove(descriptor.AggregatorName, out var activeTimer))
                activeTimer.Dispose();
        }

        var persistor = serviceProvider.GetService<IAggregatorPersistor>();
        if (persistor == null) return;

        var rawMessages = await persistor.GetDataAsync(descriptor.AggregatorName, cancellationToken).ConfigureAwait(false);
        if (rawMessages.Count == 0) return;

        var typedList = descriptor.BuildTypedList(rawMessages);

        var aggregator = serviceProvider.GetService(descriptor.AggregatorBaseType);
        if (aggregator == null) return;

        descriptor.InvokeExecute(aggregator, typedList);

        foreach (var msg in rawMessages)
        {
            if (msg is Message m)
                await persistor.RemoveDataAsync(descriptor.AggregatorName, m.CorrelationId, cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnTimerFired(AggregatorDescriptor descriptor)
    {
        if (!_timers.ContainsKey(descriptor.AggregatorName)) return;
        _ = FlushAggregatorAsync(descriptor, CancellationToken.None)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    logger.LogError(t.Exception, "Error flushing aggregator {AggregatorName} on timeout", descriptor.AggregatorName);
            }, TaskContinuationOptions.OnlyOnFaulted);
    }
}
```

Deleted: `FindAggregatorType` (replaced by registry), all `GetMethod`/`Invoke` calls, the `Activator.CreateInstance(listType)` + manual copy (replaced by `BuildTypedList`). The second `postFlushPersistor = serviceProvider.GetService<IAggregatorPersistor>()` lookup is also removed — we already have `persistor` in scope.

---

## DI Wiring & Bus Warm-up

### `ServiceCollectionExtensions.AddServiceConnect`

Add three factory registrations alongside the existing `ProcessManagerHandlerRegistry` factory:

```csharp
services.TryAddSingleton<MessageHandlerRegistry>(sp => new MessageHandlerRegistry(
    sp.GetRequiredService<IList<HandlerReference>>(),
    sp.GetRequiredService<ILogger<MessageHandlerRegistry>>()));

services.TryAddSingleton<StreamHandlerRegistry>(sp => new StreamHandlerRegistry(
    sp.GetRequiredService<IList<HandlerReference>>(),
    sp.GetRequiredService<ILogger<StreamHandlerRegistry>>()));

services.TryAddSingleton<AggregatorRegistry>(sp => new AggregatorRegistry(
    sp.GetRequiredService<IList<HandlerReference>>(),
    sp,
    sp.GetRequiredService<ILogger<AggregatorRegistry>>()));
```

Existing processor registrations (`HandlerProcessor`, `StreamProcessor`, `AggregatorProcessor`) — standard public-ctor scan works; the new registry constructor parameter resolves automatically.

### `Bus` ctor and warm-up

`Bus`'s existing explicit `internal` ctor (introduced in Group C-3 for `ProcessManagerHandlerRegistry`) gains three more registry parameters; the factory lambda that registers `Bus` in `ServiceCollectionExtensions` is updated accordingly. `Bus.StartConsumingAsync` warm-up touches all four:

```csharp
_ = _processManagerRegistry;
_ = _messageHandlerRegistry;
_ = _streamHandlerRegistry;
_ = _aggregatorRegistry;
```

This forces the `TryAddSingleton` factories to run before any message is dispatched, so any descriptor-construction errors surface at startup rather than mid-traffic.

### Public API

- `IBus` — unchanged.
- `Bus` class — unchanged public surface; ctor remains `internal`.
- `HandlerProcessor`, `StreamProcessor`, `AggregatorProcessor` — public; ctor parameters change but they're instantiated via DI only.
- `IMessageProcessor` — unchanged.

---

## Behavior Changes

Mostly invisible, but two semantic shifts worth noting:

- **Fail-fast on duplicate stream/aggregator mappings.** Today, `AggregatorProcessor.FindAggregatorType` returns the first match and `StreamProcessor` resolves whatever the DI container returns for `IStreamHandler<T>` when multiple are registered — silent first-wins behavior. The new registries throw `InvalidOperationException` at startup. Matches the policy `ProcessManagerHandlerRegistry` already enforces. Expected fallout: some unit-test assemblies that share handler classes across test fixtures may trip this during construction (same pattern observed when `ProcessManagerHandlerRegistry` shipped in Group C-3 — mitigated by `ConfigureBus(c => c.ScanForMessageHandlers = false)` in tests that don't exercise handler scanning).
- **`BatchSize`/`Timeout` resolved once per aggregator, at startup.** Previously called on every message. No behavioral change expected unless an aggregator returns different values from these methods on different calls — which would be a bug in the aggregator, not in the message bus.

Public `IBus` / `IMessageProcessor` / `HandlerReference` surfaces are untouched. No migration required for consumers of the library.

---

## Testing Strategy

All tests are xUnit, in `src/ServiceConnect.UnitTests/`. New files alongside the existing `ProcessManagerHandlerRegistryTests.cs`:

### `Processors/MessageHandlerRegistryTests.cs`

- `Construction_BuildsDescriptor_ForMessageHandlerReference`.
- `Construction_IgnoresNonMessageHandlers` (IProcessHandler / IStreamHandler / Aggregator).
- `Construction_DoesNotThrow_OnDuplicateMessageType` (multiple handler classes for one message type is legitimate).
- `TryGetOrBuild_ReturnsTrue_ForRegisteredType`.
- `TryGetOrBuild_LazilyBuilds_ForUnregisteredType`.
- `TryGetOrBuild_NegativeCaches_UnbuildableType` (e.g. `typeof(object)` returns false, second call returns false without rebuilding — verified via call-count on a spy base dict).
- `Descriptor_SetContext_WritesContextProperty`.
- `Descriptor_InvokeHandleAsync_CallsHandleOnHandler`.

### `Processors/StreamHandlerRegistryTests.cs`

- `Construction_BuildsDescriptor_ForStreamHandler`.
- `Construction_Throws_OnDuplicateMessageType_WithDifferentHandlers`.
- `Construction_Deduplicates_SameHandlerRegisteredTwice`.
- `Construction_IgnoresNonStreamHandlers`.
- `TryGet_ReturnsFalse_ForUnknownType`.
- `Descriptor_SetStream_WritesStreamProperty`.
- `Descriptor_InvokeExecute_CallsExecuteOnHandler_AwaitsTask`.
- `Descriptor_InvokeExecute_ReturnsCompletedTask_ForVoidExecute` (legacy path).

### `Processors/AggregatorRegistryTests.cs`

- `Construction_BuildsDescriptor_ForAggregator`.
- `Construction_Throws_OnDuplicateMessageType`.
- `Construction_Throws_WhenAggregatorCannotBeResolved`.
- `Construction_CapturesBatchSize_FromAggregatorInstance`.
- `Construction_CapturesTimeout_FromAggregatorInstance`.
- `TryGet_ReturnsFalse_ForUnknownType`.
- `Descriptor_BuildTypedList_ReturnsTypedList_PopulatedFromRaw`.
- `Descriptor_InvokeExecute_CallsExecuteOnAggregator`.

### Existing processor tests — updates

`src/ServiceConnect.UnitTests/Processors/HandlerProcessorTests.cs`, `StreamProcessorTests.cs`, `AggregatorProcessorTests.cs` each get:

- A small private helper `BuildRegistry(params HandlerReference[])` mirroring the pattern in `ProcessManagerProcessorTests.cs`.
- Existing tests updated to pass the registry through the ctor.
- Behavioral assertions preserved; where the old code mocked reflection boundaries, the new code uses real fake handlers and asserts against them.

### E2E

`sg docker -c "dotnet test src/ServiceConnect.EndToEndTests -v quiet"` — unchanged external behavior means the existing E2E suite remains the regression safety net. Must pass before commits 1, 3, and 5.

---

## Commit Strategy

Each commit is atomic (registry + descriptor + DI wiring + processor rewire + tests) so CI stays green on every hash.

1. **`MessageHandlerRegistry`** + descriptor + tests + DI factory + `HandlerProcessor` rewired + `HandlerProcessorTests` updated. Unit + E2E.
2. **`StreamHandlerRegistry`** + descriptor + tests + DI factory + `StreamProcessor` rewired + `StreamProcessorTests` updated. Unit only.
3. **`AggregatorRegistry`** + descriptor + tests + DI factory + `AggregatorProcessor` rewired + `AggregatorProcessorTests` updated. Unit + E2E.
4. **`Bus` warm-up** — inject the three new registries, add touches in `StartConsumingAsync`, update `Bus` factory in `ServiceCollectionExtensions`. Unit only.
5. **Doc update** — `docs/remaining-issues.md` marks R-009 done in Group C-4. Unit + E2E before tag.

Full unit suite passes before each commit. Full E2E suite passes before commits 1, 3, and 5.
