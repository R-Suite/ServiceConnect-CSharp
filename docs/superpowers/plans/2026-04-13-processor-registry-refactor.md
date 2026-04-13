# Processor Registry Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate dispatch-time reflection from `HandlerProcessor`, `StreamProcessor`, and `AggregatorProcessor` by introducing three new internal `*HandlerRegistry` classes mirroring the shipped `ProcessManagerHandlerRegistry` pattern.

**Architecture:** Each processor gets a dedicated registry of compiled expression-tree delegates, built eagerly from `IList<HandlerReference>` at DI resolution. `Bus.StartConsumingAsync` touches all four registries (existing + three new) to force fail-fast on duplicate handler registrations. `AggregatorRegistry` additionally resolves each aggregator once at startup to capture `BatchSize` / `Timeout`.

**Tech Stack:** .NET (net6.0 / net8.0 / net10.0 multi-target), xUnit + Moq, `System.Linq.Expressions` for delegate compilation, Microsoft.Extensions.DependencyInjection.

**Reference spec:** [docs/superpowers/specs/2026-04-13-processor-registry-refactor-design.md](../specs/2026-04-13-processor-registry-refactor-design.md)

**Reference implementation to mirror:**
- [src/ServiceConnect/Services/Processors/ProcessManagerHandlerRegistry.cs](../../../src/ServiceConnect/Services/Processors/ProcessManagerHandlerRegistry.cs)
- [src/ServiceConnect/Services/Processors/ProcessManagerDescriptor.cs](../../../src/ServiceConnect/Services/Processors/ProcessManagerDescriptor.cs)
- [src/ServiceConnect.UnitTests/Processors/ProcessManagerHandlerRegistryTests.cs](../../../src/ServiceConnect.UnitTests/Processors/ProcessManagerHandlerRegistryTests.cs)

---

## File Structure

| File | Responsibility | Task |
|------|---------------|------|
| `src/ServiceConnect/Services/Processors/MessageHandlerDescriptor.cs` | Record holding compiled delegates for one message-handler interface type | 1 |
| `src/ServiceConnect/Services/Processors/MessageHandlerRegistry.cs` | Hybrid pre-compute + lazy registry; `TryGetOrBuild` returns descriptor with negative-cache | 1 |
| `src/ServiceConnect.UnitTests/Processors/MessageHandlerRegistryTests.cs` | Unit tests for the registry | 1 |
| `src/ServiceConnect/Services/Processors/HandlerProcessor.cs` (modify) | Drop reflection in dispatch, use registry + compiled delegates | 1 |
| `src/ServiceConnect.UnitTests/Processors/HandlerProcessorTests.cs` (modify) | Supply registry in ctor; existing behavioral coverage preserved | 1 |
| `src/ServiceConnect/ServiceCollectionExtensions.cs` (modify) | Register `MessageHandlerRegistry` via factory | 1 |
| `src/ServiceConnect/Services/Processors/StreamHandlerDescriptor.cs` | Record holding compiled delegates for one stream-handler interface type | 2 |
| `src/ServiceConnect/Services/Processors/StreamHandlerRegistry.cs` | Pure pre-compute registry; throws on duplicate message-type | 2 |
| `src/ServiceConnect.UnitTests/Processors/StreamHandlerRegistryTests.cs` | Unit tests for the registry | 2 |
| `src/ServiceConnect/Services/Processors/StreamProcessor.cs` (modify) | Drop reflection in completion branch, use registry | 2 |
| `src/ServiceConnect.UnitTests/Processors/StreamProcessorTests.cs` (modify) | Supply registry in ctor | 2 |
| `src/ServiceConnect/ServiceCollectionExtensions.cs` (modify) | Register `StreamHandlerRegistry` via factory | 2 |
| `src/ServiceConnect/Services/Processors/AggregatorDescriptor.cs` | Record with pre-captured `BatchSize`/`Timeout` and compiled Execute + List<T> factory | 3 |
| `src/ServiceConnect/Services/Processors/AggregatorRegistry.cs` | Pre-compute registry, materializes each aggregator once at startup | 3 |
| `src/ServiceConnect.UnitTests/Processors/AggregatorRegistryTests.cs` | Unit tests | 3 |
| `src/ServiceConnect/Services/Processors/AggregatorProcessor.cs` (modify) | Drop reflection, use registry | 3 |
| `src/ServiceConnect.UnitTests/Processors/AggregatorProcessorTests.cs` (modify) | Supply registry + handler refs in ctor | 3 |
| `src/ServiceConnect/ServiceCollectionExtensions.cs` (modify) | Register `AggregatorRegistry` via factory | 3 |
| `src/ServiceConnect/Bus.cs` (modify) | Inject three new registries + warm-up touches | 4 |
| `src/ServiceConnect/ServiceCollectionExtensions.cs` (modify) | Update `Bus` factory to pass three new registries | 4 |
| `src/ServiceConnect.UnitTests/BusTests.cs` (potentially modify) | Update any Bus ctor call sites | 4 |
| `docs/remaining-issues.md` (modify) | Mark R-009 done in Group C-4 | 5 |

---

## Important pattern notes

### Internal accessibility

All three new registries are `internal sealed class`. Register via **explicit factory** in `ServiceCollectionExtensions.AddServiceConnect` — MS DI's default ctor scan only sees public constructors, so `TryAddSingleton<T>()` alone will not work for internal types. Mirror the existing `ProcessManagerHandlerRegistry` factory registration.

`InternalsVisibleTo("ServiceConnect.UnitTests")` is already declared in the csproj — unit tests can construct the internal registries directly.

### Bus ctor accessibility

`Bus` already has an `internal` ctor and is registered via factory. Adding three internal registry parameters to the ctor needs to update the factory lambda in `ServiceCollectionExtensions`.

### E2E tests

When instructed to run E2E tests, the command is:

```bash
sg docker -c "dotnet test src/ServiceConnect.EndToEndTests -v quiet"
```

`sg docker -c "..."` is the only way to start containers — do not `cd` into `src/ServiceConnect.EndToEndTests` and run directly.

### Running the unit tests

```bash
dotnet test src/ServiceConnect.UnitTests -v quiet
```

---

## Task 1: MessageHandlerRegistry + HandlerProcessor rewire

**Commit message (at the end):**
```
refactor: introduce MessageHandlerRegistry and remove reflection from HandlerProcessor (R-009)

Mirrors the ProcessManagerHandlerRegistry pattern: compiled Expression-tree delegates
replace MethodInfo.Invoke and GetProperty/SetValue in the hot dispatch path. Registry
pre-builds descriptors from HandlerReferences at startup and lazily augments on demand
for ancestor/derived types encountered during the base-walk.
```

**Files:**
- Create: `src/ServiceConnect/Services/Processors/MessageHandlerDescriptor.cs`
- Create: `src/ServiceConnect/Services/Processors/MessageHandlerRegistry.cs`
- Create: `src/ServiceConnect.UnitTests/Processors/MessageHandlerRegistryTests.cs`
- Modify: `src/ServiceConnect/Services/Processors/HandlerProcessor.cs`
- Modify: `src/ServiceConnect.UnitTests/Processors/HandlerProcessorTests.cs`
- Modify: `src/ServiceConnect/ServiceCollectionExtensions.cs`

- [ ] **Step 1.1: Create `MessageHandlerDescriptor.cs`**

Path: `src/ServiceConnect/Services/Processors/MessageHandlerDescriptor.cs`

```csharp
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed record MessageHandlerDescriptor(
    Type MessageType,
    Type HandlerInterfaceType,
    Action<object, IConsumeContext> SetContext,
    Func<object, object, Task> InvokeHandleAsync);
```

- [ ] **Step 1.2: Write the registry tests (failing)**

Path: `src/ServiceConnect.UnitTests/Processors/MessageHandlerRegistryTests.cs`

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using ServiceConnect.Interfaces;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class MessageHandlerRegistryTests
{
    [Fact]
    public void TryGetOrBuild_ReturnsTrue_ForRegisteredMessageType()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(MhrFooMsg), HandlerType = typeof(MhrFooHandler) }
        };
        var registry = new MessageHandlerRegistry(refs, NullLogger<MessageHandlerRegistry>.Instance);

        Assert.True(registry.TryGetOrBuild(typeof(MhrFooMsg), out var descriptor));
        Assert.Equal(typeof(MhrFooMsg), descriptor!.MessageType);
        Assert.Equal(typeof(IMessageHandler<MhrFooMsg>), descriptor.HandlerInterfaceType);
    }

    [Fact]
    public void Construction_IgnoresNonMessageHandlers()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(MhrFooMsg), HandlerType = typeof(MhrProcessHandler) },
            new() { MessageType = typeof(MhrFooMsg), HandlerType = typeof(MhrStreamHandler) }
        };
        var registry = new MessageHandlerRegistry(refs, NullLogger<MessageHandlerRegistry>.Instance);

        // Neither process nor stream handler should register an IMessageHandler descriptor
        Assert.False(registry.TryGetOrBuild(typeof(MhrFooMsg), out _));
    }

    [Fact]
    public void Construction_DoesNotThrow_OnMultipleHandlersForSameMessage()
    {
        // Two different handler classes for one message type is legitimate; descriptor describes interface not instance
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(MhrFooMsg), HandlerType = typeof(MhrFooHandler) },
            new() { MessageType = typeof(MhrFooMsg), HandlerType = typeof(MhrSecondFooHandler) }
        };

        var registry = new MessageHandlerRegistry(refs, NullLogger<MessageHandlerRegistry>.Instance);

        Assert.True(registry.TryGetOrBuild(typeof(MhrFooMsg), out _));
    }

    [Fact]
    public void TryGetOrBuild_LazilyBuilds_ForUnregisteredMessageType()
    {
        var registry = new MessageHandlerRegistry(
            new List<HandlerReference>(),
            NullLogger<MessageHandlerRegistry>.Instance);

        Assert.True(registry.TryGetOrBuild(typeof(MhrFooMsg), out var descriptor));
        Assert.Equal(typeof(IMessageHandler<MhrFooMsg>), descriptor!.HandlerInterfaceType);
    }

    [Fact]
    public void TryGetOrBuild_ReturnsFalse_ForMessageBaseType()
    {
        var registry = new MessageHandlerRegistry(
            new List<HandlerReference>(),
            NullLogger<MessageHandlerRegistry>.Instance);

        Assert.False(registry.TryGetOrBuild(typeof(Message), out var descriptor));
        Assert.Null(descriptor);
    }

    [Fact]
    public void TryGetOrBuild_ReturnsFalse_ForObject()
    {
        var registry = new MessageHandlerRegistry(
            new List<HandlerReference>(),
            NullLogger<MessageHandlerRegistry>.Instance);

        Assert.False(registry.TryGetOrBuild(typeof(object), out _));
    }

    [Fact]
    public void Descriptor_SetContext_WritesContextProperty()
    {
        var registry = BuildRegistry();
        Assert.True(registry.TryGetOrBuild(typeof(MhrFooMsg), out var descriptor));

        var handler = new MhrFooHandler();
        var ctx = new MhrFakeConsumeContext();
        descriptor!.SetContext(handler, ctx);

        Assert.Same(ctx, handler.Context);
    }

    [Fact]
    public async Task Descriptor_InvokeHandleAsync_CallsHandleAsyncOnHandler()
    {
        var registry = BuildRegistry();
        Assert.True(registry.TryGetOrBuild(typeof(MhrFooMsg), out var descriptor));

        var handler = new MhrFooHandler();
        var msg = new MhrFooMsg(Guid.NewGuid());

        await descriptor!.InvokeHandleAsync(handler, msg);

        Assert.Same(msg, handler.Received);
    }

    [Fact]
    public void TryGetOrBuild_CachesNegativeResults()
    {
        var registry = new MessageHandlerRegistry(
            new List<HandlerReference>(),
            NullLogger<MessageHandlerRegistry>.Instance);

        // Hit the unbuildable type twice; both return false without crashing
        Assert.False(registry.TryGetOrBuild(typeof(Message), out _));
        Assert.False(registry.TryGetOrBuild(typeof(Message), out _));
    }

    private static MessageHandlerRegistry BuildRegistry()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(MhrFooMsg), HandlerType = typeof(MhrFooHandler) }
        };
        return new MessageHandlerRegistry(refs, NullLogger<MessageHandlerRegistry>.Instance);
    }
}

file class MhrFooMsg : Message { public MhrFooMsg(Guid c) : base(c) { } }
file class MhrBarData : IProcessManagerData { public Guid CorrelationId { get; set; } }

file class MhrFooHandler : IMessageHandler<MhrFooMsg>
{
    public IConsumeContext? Context { get; set; }
    public MhrFooMsg? Received { get; private set; }
    public Task HandleAsync(MhrFooMsg message) { Received = message; return Task.CompletedTask; }
}

file class MhrSecondFooHandler : IMessageHandler<MhrFooMsg>
{
    public IConsumeContext? Context { get; set; }
    public Task HandleAsync(MhrFooMsg message) => Task.CompletedTask;
}

file class MhrProcessHandler : IProcessHandler<MhrBarData, MhrFooMsg>
{
    public IConsumeContext? Context { get; set; }
    public Task HandleAsync(MhrFooMsg message, MhrBarData data) => Task.CompletedTask;
}

file class MhrStreamHandler : IStreamHandler<MhrFooMsg>
{
    public IMessageBusReadStream Stream { get; set; } = null!;
    public void Execute(MhrFooMsg stream) { }
}

file class MhrFakeConsumeContext : IConsumeContext
{
    public IBus Bus => throw new NotImplementedException();
    public IDictionary<string, object> Headers { get; set; } = new Dictionary<string, object>();
    public string? MessageId => null;
    public Guid CorrelationId => Guid.Empty;
    public CancellationToken CancellationToken { get; set; }
    public Task ReplyAsync<TReply>(TReply message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default) where TReply : Message
        => throw new NotImplementedException();
}
```

- [ ] **Step 1.3: Run tests — expect failure (no registry class yet)**

```bash
dotnet test src/ServiceConnect.UnitTests -v quiet --filter "MessageHandlerRegistryTests"
```

Expected: compile failure, `MessageHandlerRegistry` type doesn't exist.

- [ ] **Step 1.4: Create `MessageHandlerRegistry.cs`**

Path: `src/ServiceConnect/Services/Processors/MessageHandlerRegistry.cs`

```csharp
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed class MessageHandlerRegistry
{
    private readonly ConcurrentDictionary<Type, MessageHandlerDescriptor?> _descriptors = new();
    private readonly ILogger<MessageHandlerRegistry> _logger;

    internal MessageHandlerRegistry(
        IList<HandlerReference> handlerReferences,
        ILogger<MessageHandlerRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(handlerReferences);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        foreach (var href in handlerReferences)
        {
            var messageHandlerInterface = FindMessageHandlerInterface(href.HandlerType, href.MessageType);
            if (messageHandlerInterface == null)
                continue;

            // Duplicates are legitimate — multiple handler classes for one message type are allowed.
            // Only one descriptor per message type (it describes the interface, not the instances).
            _descriptors.TryAdd(href.MessageType, BuildDescriptor(href.MessageType, messageHandlerInterface));

            _logger.LogDebug(
                "Registered message-handler descriptor: message={MessageType}, handler={HandlerType}",
                href.MessageType.Name, href.HandlerType.Name);
        }
    }

    internal bool TryGetOrBuild(Type messageType, [NotNullWhen(true)] out MessageHandlerDescriptor? descriptor)
    {
        if (_descriptors.TryGetValue(messageType, out descriptor))
            return descriptor != null;

        descriptor = TryBuild(messageType);
        _descriptors[messageType] = descriptor;
        return descriptor != null;
    }

    private static MessageHandlerDescriptor? TryBuild(Type messageType)
    {
        if (messageType == typeof(Message) || messageType == typeof(object))
            return null;

        var handlerInterfaceType = typeof(IMessageHandler<>).MakeGenericType(messageType);
        return BuildDescriptor(messageType, handlerInterfaceType);
    }

    private static Type? FindMessageHandlerInterface(Type handlerType, Type messageType)
    {
        return handlerType.GetInterfaces().FirstOrDefault(i =>
            i.IsGenericType
            && i.GetGenericTypeDefinition() == typeof(IMessageHandler<>)
            && i.GetGenericArguments()[0] == messageType);
    }

    private static MessageHandlerDescriptor BuildDescriptor(Type messageType, Type handlerInterfaceType)
    {
        return new MessageHandlerDescriptor(
            MessageType: messageType,
            HandlerInterfaceType: handlerInterfaceType,
            SetContext: CompileSetContext(handlerInterfaceType),
            InvokeHandleAsync: CompileInvokeHandleAsync(handlerInterfaceType, messageType));
    }

    private static Action<object, IConsumeContext> CompileSetContext(Type handlerInterface)
    {
        var handlerParam = Expression.Parameter(typeof(object), "handler");
        var ctxParam = Expression.Parameter(typeof(IConsumeContext), "ctx");
        var cast = Expression.Convert(handlerParam, handlerInterface);
        var prop = handlerInterface.GetProperty("Context")!;
        var assign = Expression.Assign(Expression.Property(cast, prop), ctxParam);
        return Expression.Lambda<Action<object, IConsumeContext>>(assign, handlerParam, ctxParam).Compile();
    }

    private static Func<object, object, Task> CompileInvokeHandleAsync(Type handlerInterface, Type messageType)
    {
        var handlerParam = Expression.Parameter(typeof(object), "handler");
        var messageParam = Expression.Parameter(typeof(object), "message");

        var handlerCast = Expression.Convert(handlerParam, handlerInterface);
        var messageCast = Expression.Convert(messageParam, messageType);

        var method = handlerInterface.GetMethod("HandleAsync")!;
        var call = Expression.Call(handlerCast, method, messageCast);

        return Expression.Lambda<Func<object, object, Task>>(call, handlerParam, messageParam).Compile();
    }
}
```

- [ ] **Step 1.5: Run the registry tests — expect pass**

```bash
dotnet test src/ServiceConnect.UnitTests -v quiet --filter "MessageHandlerRegistryTests"
```

Expected: all 9 tests green.

- [ ] **Step 1.6: Rewrite `HandlerProcessor.cs` to use the registry**

Path: `src/ServiceConnect/Services/Processors/HandlerProcessor.cs`

Full replacement:

```csharp
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

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

        // Walk up the message hierarchy — stop at Message and object.
        // All matching handlers in the hierarchy are invoked.
        var invocations = new List<(object Handler, MessageHandlerDescriptor Descriptor)>();
        var checkedType = messageType;
        while (checkedType != null && checkedType != typeof(Message) && checkedType != typeof(object))
        {
            if (registry.TryGetOrBuild(checkedType, out var descriptor))
            {
                foreach (var h in serviceProvider.GetServices(descriptor.HandlerInterfaceType))
                {
                    if (h != null)
                        invocations.Add((h, descriptor));
                }
            }
            checkedType = checkedType.BaseType;
        }

        if (invocations.Count == 0)
            return ProcessResult.NotHandled;

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

    private static async Task ForwardRoutingSlipAsync(object message, Type messageType, IDictionary<string, object> headers, IBus bus, CancellationToken cancellationToken)
    {
        if (!headers.TryGetValue(HeaderKeys.RoutingSlip, out var routingSlipRaw))
            return;

        var routingSlip = HeaderDecoder.Decode(routingSlipRaw);

        if (string.IsNullOrWhiteSpace(routingSlip))
            return;

        var destinations = routingSlip.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim())
            .ToList();

        if (destinations.Count == 0)
            return;

        // MakeGenericMethod here is intentionally retained — IBus.RouteAsync is a generic method
        // with no descriptor to compile against. Separate concern from R-009.
        var routeMethod = typeof(IBus).GetMethod(nameof(IBus.RouteAsync))!.MakeGenericMethod(messageType);
        var task = (Task)routeMethod.Invoke(bus, [message, destinations, cancellationToken])!;
        await task;
    }
}
```

- [ ] **Step 1.7: Update `HandlerProcessorTests.cs` — add registry to all ctor calls**

Path: `src/ServiceConnect.UnitTests/Processors/HandlerProcessorTests.cs`

Every `new HandlerProcessor(provider)` → `new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), provider)`. Add helper method at the bottom of the test class:

```csharp
    private static MessageHandlerRegistry BuildRegistry(params Type[] messageTypes)
    {
        var refs = messageTypes
            .Select(mt => new HandlerReference { MessageType = mt, HandlerType = typeof(TestHpHandler) })
            .ToList();
        return new MessageHandlerRegistry(
            refs,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MessageHandlerRegistry>.Instance);
    }
```

Tests to update (in order — one per `new HandlerProcessor(...)` call):

1. `ProcessAsync_WithRegisteredHandler_InvokesHandler`:
   ```csharp
   var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), provider);
   ```
2. `ProcessAsync_NoHandlers_ReturnsNotHandled`:
   ```csharp
   var processor = new HandlerProcessor(BuildRegistry(), provider);
   ```
3. `ProcessAsync_NullMessage_ReturnsNotHandled`:
   ```csharp
   var processor = new HandlerProcessor(BuildRegistry(), provider);
   ```
4. `ProcessAsync_SetsConsumeContext`:
   ```csharp
   var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), provider);
   ```
5. `ProcessAsync_WithRoutingSlip_ForwardsToNextDestination`:
   ```csharp
   var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), provider);
   ```
6. `ProcessAsync_WithRoutingSlipBytes_ForwardsToNextDestination`:
   ```csharp
   var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), provider);
   ```
7. `ProcessAsync_NoRoutingSlip_DoesNotCallRoute`:
   ```csharp
   var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), provider);
   ```

Also change the top-of-file using block to include:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
```

- [ ] **Step 1.8: Register `MessageHandlerRegistry` in DI**

Path: `src/ServiceConnect/ServiceCollectionExtensions.cs`

Add immediately after the existing `ProcessManagerHandlerRegistry` factory registration (around line 36). The block is currently:

```csharp
        services.TryAddSingleton<Services.Processors.ProcessManagerHandlerRegistry>(sp => new Services.Processors.ProcessManagerHandlerRegistry(
            sp.GetRequiredService<IList<HandlerReference>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.Processors.ProcessManagerHandlerRegistry>>()));
```

After it, add:

```csharp
        services.TryAddSingleton<Services.Processors.MessageHandlerRegistry>(sp => new Services.Processors.MessageHandlerRegistry(
            sp.GetRequiredService<IList<HandlerReference>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.Processors.MessageHandlerRegistry>>()));
```

`HandlerProcessor`'s `TryAddSingleton<HandlerProcessor>()` (around line 43) picks up the registry automatically via constructor injection — no change needed there.

- [ ] **Step 1.9: Run the full unit suite**

```bash
dotnet test src/ServiceConnect.UnitTests -v quiet
```

Expected: all tests pass. Any fail-fast from the new registry construction requires investigation — most likely a test assembly needs `ConfigureBus(c => c.ScanForMessageHandlers = false)` like Group C-3. Duplicates on `IMessageHandler<T>` should NOT throw (intentional, by design), so this is unlikely but possible if the test build somehow constructs the registry with cross-assembly `HandlerReferences`.

- [ ] **Step 1.10: Run the E2E suite**

```bash
sg docker -c "dotnet test src/ServiceConnect.EndToEndTests -v quiet"
```

Expected: all E2E tests pass.

- [ ] **Step 1.11: Commit**

```bash
git add src/ServiceConnect/Services/Processors/MessageHandlerDescriptor.cs \
        src/ServiceConnect/Services/Processors/MessageHandlerRegistry.cs \
        src/ServiceConnect/Services/Processors/HandlerProcessor.cs \
        src/ServiceConnect/ServiceCollectionExtensions.cs \
        src/ServiceConnect.UnitTests/Processors/MessageHandlerRegistryTests.cs \
        src/ServiceConnect.UnitTests/Processors/HandlerProcessorTests.cs
git commit -m "$(cat <<'EOF'
refactor: introduce MessageHandlerRegistry and remove reflection from HandlerProcessor (R-009)

Mirrors the ProcessManagerHandlerRegistry pattern: compiled Expression-tree delegates
replace MethodInfo.Invoke and GetProperty/SetValue in the hot dispatch path. Registry
pre-builds descriptors from HandlerReferences at startup and lazily augments on demand
for ancestor/derived types encountered during the base-walk.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: StreamHandlerRegistry + StreamProcessor rewire

**Commit message (at the end):**
```
refactor: introduce StreamHandlerRegistry and remove reflection from StreamProcessor (R-009)

Pre-computed registry keyed by message type throws InvalidOperationException on duplicate
stream-handler registrations, replacing silent first-match DI resolution. Compiled
Expression-tree delegates replace MethodInfo.Invoke / GetProperty / SetValue in the
completion branch.
```

**Files:**
- Create: `src/ServiceConnect/Services/Processors/StreamHandlerDescriptor.cs`
- Create: `src/ServiceConnect/Services/Processors/StreamHandlerRegistry.cs`
- Create: `src/ServiceConnect.UnitTests/Processors/StreamHandlerRegistryTests.cs`
- Modify: `src/ServiceConnect/Services/Processors/StreamProcessor.cs`
- Modify: `src/ServiceConnect.UnitTests/Processors/StreamProcessorTests.cs`
- Modify: `src/ServiceConnect/ServiceCollectionExtensions.cs`

- [ ] **Step 2.1: Create `StreamHandlerDescriptor.cs`**

Path: `src/ServiceConnect/Services/Processors/StreamHandlerDescriptor.cs`

```csharp
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed record StreamHandlerDescriptor(
    Type MessageType,
    Type HandlerInterfaceType,
    Action<object, IMessageBusReadStream> SetStream,
    Action<object, object> InvokeExecute);
```

(`IStreamHandler<T>.Execute` returns `void`, not `Task` — confirmed by [src/ServiceConnect.Interfaces/IStreamHandler.cs](../../../src/ServiceConnect.Interfaces/IStreamHandler.cs). So `Action<object, object>` is the correct delegate type.)

- [ ] **Step 2.2: Write the registry tests (failing)**

Path: `src/ServiceConnect.UnitTests/Processors/StreamHandlerRegistryTests.cs`

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class StreamHandlerRegistryTests
{
    [Fact]
    public void TryGet_ReturnsDescriptor_ForRegisteredType()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ShrFoo), HandlerType = typeof(ShrFooStreamHandler) }
        };
        var registry = new StreamHandlerRegistry(refs, NullLogger<StreamHandlerRegistry>.Instance);

        Assert.True(registry.TryGet(typeof(ShrFoo), out var descriptor));
        Assert.Equal(typeof(ShrFoo), descriptor!.MessageType);
        Assert.Equal(typeof(IStreamHandler<ShrFoo>), descriptor.HandlerInterfaceType);
    }

    [Fact]
    public void TryGet_ReturnsFalse_ForUnregisteredType()
    {
        var registry = new StreamHandlerRegistry(
            new List<HandlerReference>(),
            NullLogger<StreamHandlerRegistry>.Instance);

        Assert.False(registry.TryGet(typeof(ShrFoo), out var descriptor));
        Assert.Null(descriptor);
    }

    [Fact]
    public void Construction_IgnoresNonStreamHandlers()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ShrFoo), HandlerType = typeof(ShrFooMessageHandler) }
        };
        var registry = new StreamHandlerRegistry(refs, NullLogger<StreamHandlerRegistry>.Instance);

        Assert.False(registry.TryGet(typeof(ShrFoo), out _));
    }

    [Fact]
    public void Construction_ThrowsOnDuplicateMessageType_WithDistinctHandlers()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ShrFoo), HandlerType = typeof(ShrFooStreamHandler) },
            new() { MessageType = typeof(ShrFoo), HandlerType = typeof(ShrSecondFooStreamHandler) }
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new StreamHandlerRegistry(refs, NullLogger<StreamHandlerRegistry>.Instance));

        Assert.Contains(nameof(ShrFoo), ex.Message);
    }

    [Fact]
    public void Construction_Deduplicates_SameHandlerRegisteredTwice()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ShrFoo), HandlerType = typeof(ShrFooStreamHandler) },
            new() { MessageType = typeof(ShrFoo), HandlerType = typeof(ShrFooStreamHandler) }
        };

        // Same (MessageType, HandlerType) pair twice must not throw.
        var registry = new StreamHandlerRegistry(refs, NullLogger<StreamHandlerRegistry>.Instance);
        Assert.True(registry.TryGet(typeof(ShrFoo), out _));
    }

    [Fact]
    public void Descriptor_SetStream_WritesStreamProperty()
    {
        var registry = BuildRegistry();
        Assert.True(registry.TryGet(typeof(ShrFoo), out var descriptor));

        var handler = new ShrFooStreamHandler();
        var stream = new MessageBusReadStream { SequenceId = "seq" };

        descriptor!.SetStream(handler, stream);

        Assert.Same(stream, handler.Stream);
    }

    [Fact]
    public void Descriptor_InvokeExecute_CallsExecuteOnHandler()
    {
        var registry = BuildRegistry();
        Assert.True(registry.TryGet(typeof(ShrFoo), out var descriptor));

        var handler = new ShrFooStreamHandler();
        var msg = new ShrFoo(Guid.NewGuid());

        descriptor!.InvokeExecute(handler, msg);

        Assert.Same(msg, handler.Executed);
    }

    private static StreamHandlerRegistry BuildRegistry()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ShrFoo), HandlerType = typeof(ShrFooStreamHandler) }
        };
        return new StreamHandlerRegistry(refs, NullLogger<StreamHandlerRegistry>.Instance);
    }
}

file class ShrFoo : Message { public ShrFoo(Guid c) : base(c) { } }

file class ShrFooStreamHandler : IStreamHandler<ShrFoo>
{
    public IMessageBusReadStream Stream { get; set; } = null!;
    public ShrFoo? Executed { get; private set; }
    public void Execute(ShrFoo stream) { Executed = stream; }
}

file class ShrSecondFooStreamHandler : IStreamHandler<ShrFoo>
{
    public IMessageBusReadStream Stream { get; set; } = null!;
    public void Execute(ShrFoo stream) { }
}

file class ShrFooMessageHandler : IMessageHandler<ShrFoo>
{
    public IConsumeContext? Context { get; set; }
    public Task HandleAsync(ShrFoo message) => Task.CompletedTask;
}
```

- [ ] **Step 2.3: Run tests — expect compile failure**

```bash
dotnet test src/ServiceConnect.UnitTests -v quiet --filter "StreamHandlerRegistryTests"
```

Expected: `StreamHandlerRegistry` type doesn't exist.

- [ ] **Step 2.4: Create `StreamHandlerRegistry.cs`**

Path: `src/ServiceConnect/Services/Processors/StreamHandlerRegistry.cs`

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed class StreamHandlerRegistry
{
    private readonly Dictionary<Type, (StreamHandlerDescriptor Descriptor, Type HandlerType)> _descriptors = new();

    internal StreamHandlerRegistry(
        IList<HandlerReference> handlerReferences,
        ILogger<StreamHandlerRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(handlerReferences);
        ArgumentNullException.ThrowIfNull(logger);

        foreach (var href in handlerReferences)
        {
            var streamInterface = FindStreamHandlerInterface(href.HandlerType, href.MessageType);
            if (streamInterface == null)
                continue;

            if (_descriptors.TryGetValue(href.MessageType, out var existing))
            {
                if (existing.HandlerType == href.HandlerType)
                    continue; // identical (MessageType, HandlerType) pair registered twice — dedupe silently
                throw new InvalidOperationException(
                    $"Duplicate stream-handler registration for message type '{href.MessageType.FullName}'. " +
                    $"Only one IStreamHandler<T> may be registered per message type; found '{existing.HandlerType.FullName}' and '{href.HandlerType.FullName}'.");
            }

            var descriptor = BuildDescriptor(href.MessageType, streamInterface);
            _descriptors[href.MessageType] = (descriptor, href.HandlerType);

            logger.LogDebug(
                "Registered stream-handler descriptor: message={MessageType}, handler={HandlerType}",
                href.MessageType.Name, href.HandlerType.Name);
        }
    }

    internal bool TryGet(Type messageType, [NotNullWhen(true)] out StreamHandlerDescriptor? descriptor)
    {
        if (_descriptors.TryGetValue(messageType, out var entry))
        {
            descriptor = entry.Descriptor;
            return true;
        }
        descriptor = null;
        return false;
    }

    private static Type? FindStreamHandlerInterface(Type handlerType, Type messageType)
    {
        return handlerType.GetInterfaces().FirstOrDefault(i =>
            i.IsGenericType
            && i.GetGenericTypeDefinition() == typeof(IStreamHandler<>)
            && i.GetGenericArguments()[0] == messageType);
    }

    private static StreamHandlerDescriptor BuildDescriptor(Type messageType, Type handlerInterfaceType)
    {
        return new StreamHandlerDescriptor(
            MessageType: messageType,
            HandlerInterfaceType: handlerInterfaceType,
            SetStream: CompileSetStream(handlerInterfaceType),
            InvokeExecute: CompileInvokeExecute(handlerInterfaceType, messageType));
    }

    private static Action<object, IMessageBusReadStream> CompileSetStream(Type handlerInterface)
    {
        var handlerParam = Expression.Parameter(typeof(object), "handler");
        var streamParam = Expression.Parameter(typeof(IMessageBusReadStream), "stream");
        var cast = Expression.Convert(handlerParam, handlerInterface);
        var prop = handlerInterface.GetProperty("Stream")!;
        var assign = Expression.Assign(Expression.Property(cast, prop), streamParam);
        return Expression.Lambda<Action<object, IMessageBusReadStream>>(assign, handlerParam, streamParam).Compile();
    }

    private static Action<object, object> CompileInvokeExecute(Type handlerInterface, Type messageType)
    {
        var handlerParam = Expression.Parameter(typeof(object), "handler");
        var messageParam = Expression.Parameter(typeof(object), "message");

        var handlerCast = Expression.Convert(handlerParam, handlerInterface);
        var messageCast = Expression.Convert(messageParam, messageType);

        var method = handlerInterface.GetMethod("Execute")!;
        var call = Expression.Call(handlerCast, method, messageCast);

        return Expression.Lambda<Action<object, object>>(call, handlerParam, messageParam).Compile();
    }
}
```

- [ ] **Step 2.5: Run the registry tests — expect pass**

```bash
dotnet test src/ServiceConnect.UnitTests -v quiet --filter "StreamHandlerRegistryTests"
```

Expected: all 7 tests green.

- [ ] **Step 2.6: Rewire `StreamProcessor.cs`**

Path: `src/ServiceConnect/Services/Processors/StreamProcessor.cs`

Full replacement (key changes: ctor adds `StreamHandlerRegistry`, completion branch uses registry + descriptor instead of `MakeGenericType` + reflection):

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

public sealed class StreamProcessor : IMessageProcessor, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StreamProcessor> _logger;
    private readonly IMessageTypeRegistry _typeRegistry;
    private readonly StreamHandlerRegistry _streamHandlerRegistry;
    private readonly ConcurrentDictionary<string, MessageBusReadStream> _activeStreams = new();
    private readonly ConcurrentDictionary<string, DateTime> _streamTimestamps = new();
    private readonly Timer _cleanupTimer;
    private static readonly TimeSpan StreamTimeout = TimeSpan.FromMinutes(5);

    public StreamProcessor(
        IServiceProvider serviceProvider,
        ILogger<StreamProcessor> logger,
        IMessageTypeRegistry typeRegistry,
        StreamHandlerRegistry streamHandlerRegistry)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
        _streamHandlerRegistry = streamHandlerRegistry ?? throw new ArgumentNullException(nameof(streamHandlerRegistry));
        _cleanupTimer = new Timer(_ => EvictStaleStreams(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public bool RunBeforeDeserialization => true;

    public async Task<ProcessResult> ProcessAsync(
        byte[] messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!headers.TryGetValue(HeaderKeys.MessageType, out var msgTypeRaw))
            return ProcessResult.NotHandled;

        var msgType = HeaderDecoder.Decode(msgTypeRaw);
        if (msgType != HeaderKeys.ByteStream)
            return ProcessResult.NotHandled;

        if (!headers.TryGetValue(HeaderKeys.SequenceId, out var seqIdRaw))
            return ProcessResult.NotHandled;
        var sequenceId = HeaderDecoder.Decode(seqIdRaw)!;

        if (!headers.TryGetValue(HeaderKeys.PacketNumber, out var pnRaw))
            return ProcessResult.NotHandled;
        var pnString = HeaderDecoder.Decode(pnRaw);
        if (!long.TryParse(pnString, out var packetNumber))
        {
            _logger.LogWarning("Stream packet has invalid PacketNumber header '{Value}'; discarding", pnString);
            return ProcessResult.Handled;
        }

        var stream = _activeStreams.GetOrAdd(sequenceId, _ => new MessageBusReadStream { SequenceId = sequenceId });

        stream.Write(messageBytes, packetNumber);
        _streamTimestamps[sequenceId] = DateTime.UtcNow;

        if (headers.TryGetValue(HeaderKeys.LastPacketNumber, out var lpnRaw))
        {
            var lpnString = HeaderDecoder.Decode(lpnRaw);
            if (!long.TryParse(lpnString, out var lastPacketNumber))
            {
                _logger.LogWarning("Stream packet has invalid LastPacketNumber header '{Value}'; discarding", lpnString);
                return ProcessResult.Handled;
            }
            stream.LastPacketNumber = lastPacketNumber;
        }

        if (stream.IsComplete())
        {
            _activeStreams.TryRemove(sequenceId, out _);
            _streamTimestamps.TryRemove(sequenceId, out _);

            if (!headers.TryGetValue(HeaderKeys.FullTypeName, out var ftnRaw))
            {
                _logger.LogWarning("Completed stream {SequenceId} missing FullTypeName header", sequenceId);
                return ProcessResult.Handled;
            }

            var fullTypeName = HeaderDecoder.Decode(ftnRaw);
            if (!_typeRegistry.TryResolve(fullTypeName!, out var resolvedType))
            {
                _logger.LogWarning("Unregistered type '{TypeName}' for completed stream. Rejecting", fullTypeName);
                return ProcessResult.Handled;
            }

            if (!_streamHandlerRegistry.TryGet(resolvedType, out var descriptor))
            {
                _logger.LogWarning("No IStreamHandler registered for {MessageType}", resolvedType.FullName);
                return ProcessResult.Handled;
            }

            var handler = _serviceProvider.GetService(descriptor.HandlerInterfaceType);
            if (handler == null)
            {
                _logger.LogWarning("No IStreamHandler registered for {MessageType}", resolvedType.FullName);
                return ProcessResult.Handled;
            }

            descriptor.SetStream(handler, stream);

            var serializer = _serviceProvider.GetRequiredService<IMessageSerializer>();
            var assembledBytes = stream.Read();
            var originalMessage = serializer.Deserialize(assembledBytes, resolvedType);

            descriptor.InvokeExecute(handler, originalMessage!);

            await Task.CompletedTask;
        }

        return ProcessResult.Handled;
    }

    private void EvictStaleStreams()
    {
        var cutoff = DateTime.UtcNow - StreamTimeout;
        foreach (var kvp in _streamTimestamps)
        {
            if (kvp.Value < cutoff)
            {
                _activeStreams.TryRemove(kvp.Key, out _);
                _streamTimestamps.TryRemove(kvp.Key, out _);
                _logger.LogWarning("Evicted incomplete stream {SequenceId} after timeout", kvp.Key);
            }
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
    }
}
```

- [ ] **Step 2.7: Update `StreamProcessorTests.cs` — add registry to all ctor calls**

Path: `src/ServiceConnect.UnitTests/Processors/StreamProcessorTests.cs`

Add `using Microsoft.Extensions.Logging.Abstractions;` to the using block.

Replace each `new StreamProcessor(provider, new Mock<ILogger<StreamProcessor>>().Object, new MessageTypeRegistry())` with:

```csharp
var processor = new StreamProcessor(
    provider,
    new Mock<ILogger<StreamProcessor>>().Object,
    new MessageTypeRegistry(),
    new StreamHandlerRegistry(new List<HandlerReference>(), NullLogger<StreamHandlerRegistry>.Instance));
```

Three call sites to update (in `ProcessAsync_NonByteStream_ReturnsNotHandled`, `ProcessAsync_NoMessageTypeHeader_ReturnsNotHandled`, and `ProcessAsync_ByteStreamPacket_ReturnsHandled`).

- [ ] **Step 2.8: Register `StreamHandlerRegistry` in DI**

Path: `src/ServiceConnect/ServiceCollectionExtensions.cs`

Immediately after the `MessageHandlerRegistry` factory registration added in Task 1, add:

```csharp
        services.TryAddSingleton<Services.Processors.StreamHandlerRegistry>(sp => new Services.Processors.StreamHandlerRegistry(
            sp.GetRequiredService<IList<HandlerReference>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.Processors.StreamHandlerRegistry>>()));
```

`StreamProcessor`'s `TryAddSingleton<StreamProcessor>()` picks up the new parameter via constructor injection automatically.

- [ ] **Step 2.9: Run the full unit suite**

```bash
dotnet test src/ServiceConnect.UnitTests -v quiet
```

Expected: all tests pass.

- [ ] **Step 2.10: Commit**

```bash
git add src/ServiceConnect/Services/Processors/StreamHandlerDescriptor.cs \
        src/ServiceConnect/Services/Processors/StreamHandlerRegistry.cs \
        src/ServiceConnect/Services/Processors/StreamProcessor.cs \
        src/ServiceConnect/ServiceCollectionExtensions.cs \
        src/ServiceConnect.UnitTests/Processors/StreamHandlerRegistryTests.cs \
        src/ServiceConnect.UnitTests/Processors/StreamProcessorTests.cs
git commit -m "$(cat <<'EOF'
refactor: introduce StreamHandlerRegistry and remove reflection from StreamProcessor (R-009)

Pre-computed registry keyed by message type throws InvalidOperationException on duplicate
stream-handler registrations, replacing silent first-match DI resolution. Compiled
Expression-tree delegates replace MethodInfo.Invoke / GetProperty / SetValue in the
completion branch.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: AggregatorRegistry + AggregatorProcessor rewire

**Commit message (at the end):**
```
refactor: introduce AggregatorRegistry and remove reflection from AggregatorProcessor (R-009)

Pre-computed registry keyed by message type throws InvalidOperationException on duplicate
aggregator registrations. BatchSize and Timeout are captured once at startup rather than
per-message. Compiled Expression-tree delegates replace MethodInfo.Invoke / GetProperty.
FindAggregatorType service-locator scan is deleted; registry does the lookup once.
```

**Files:**
- Create: `src/ServiceConnect/Services/Processors/AggregatorDescriptor.cs`
- Create: `src/ServiceConnect/Services/Processors/AggregatorRegistry.cs`
- Create: `src/ServiceConnect.UnitTests/Processors/AggregatorRegistryTests.cs`
- Modify: `src/ServiceConnect/Services/Processors/AggregatorProcessor.cs`
- Modify: `src/ServiceConnect.UnitTests/Processors/AggregatorProcessorTests.cs`
- Modify: `src/ServiceConnect/ServiceCollectionExtensions.cs`

- [ ] **Step 3.1: Create `AggregatorDescriptor.cs`**

Path: `src/ServiceConnect/Services/Processors/AggregatorDescriptor.cs`

```csharp
namespace ServiceConnect.Services.Processors;

internal sealed record AggregatorDescriptor(
    Type MessageType,
    Type AggregatorBaseType,
    string AggregatorName,
    int BatchSize,
    TimeSpan Timeout,
    Func<IList<object>, System.Collections.IList> BuildTypedList,
    Action<object, object> InvokeExecute);
```

- [ ] **Step 3.2: Write the registry tests (failing)**

Path: `src/ServiceConnect.UnitTests/Processors/AggregatorRegistryTests.cs`

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ServiceConnect.Interfaces;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class AggregatorRegistryTests
{
    [Fact]
    public void TryGet_ReturnsDescriptor_ForRegisteredType()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ArgFoo), HandlerType = typeof(ArgFooAggregator) }
        };
        var sp = BuildServiceProvider<ArgFoo, ArgFooAggregator>();
        var registry = new AggregatorRegistry(refs, sp, NullLogger<AggregatorRegistry>.Instance);

        Assert.True(registry.TryGet(typeof(ArgFoo), out var descriptor));
        Assert.Equal(typeof(ArgFoo), descriptor!.MessageType);
        Assert.Equal(typeof(Aggregator<ArgFoo>), descriptor.AggregatorBaseType);
        Assert.Equal(typeof(Aggregator<ArgFoo>).FullName, descriptor.AggregatorName);
    }

    [Fact]
    public void TryGet_ReturnsFalse_ForUnregisteredType()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var registry = new AggregatorRegistry(
            new List<HandlerReference>(),
            sp,
            NullLogger<AggregatorRegistry>.Instance);

        Assert.False(registry.TryGet(typeof(ArgFoo), out var descriptor));
        Assert.Null(descriptor);
    }

    [Fact]
    public void Construction_CapturesBatchSize_FromAggregatorInstance()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ArgFoo), HandlerType = typeof(ArgFooAggregator) }
        };
        var sp = BuildServiceProvider<ArgFoo, ArgFooAggregator>();
        var registry = new AggregatorRegistry(refs, sp, NullLogger<AggregatorRegistry>.Instance);

        Assert.True(registry.TryGet(typeof(ArgFoo), out var descriptor));
        Assert.Equal(42, descriptor!.BatchSize);
    }

    [Fact]
    public void Construction_CapturesTimeout_FromAggregatorInstance()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ArgFoo), HandlerType = typeof(ArgFooAggregator) }
        };
        var sp = BuildServiceProvider<ArgFoo, ArgFooAggregator>();
        var registry = new AggregatorRegistry(refs, sp, NullLogger<AggregatorRegistry>.Instance);

        Assert.True(registry.TryGet(typeof(ArgFoo), out var descriptor));
        Assert.Equal(TimeSpan.FromSeconds(7), descriptor!.Timeout);
    }

    [Fact]
    public void Construction_ThrowsOnDuplicateMessageType()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ArgFoo), HandlerType = typeof(ArgFooAggregator) },
            new() { MessageType = typeof(ArgFoo), HandlerType = typeof(ArgSecondFooAggregator) }
        };

        var services = new ServiceCollection();
        services.AddTransient<Aggregator<ArgFoo>, ArgFooAggregator>();
        // Second registration would be lost in a single-service-descriptor container, so build manually.
        var sp = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new AggregatorRegistry(refs, sp, NullLogger<AggregatorRegistry>.Instance));
        Assert.Contains(nameof(ArgFoo), ex.Message);
    }

    [Fact]
    public void Construction_IgnoresNonAggregators()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ArgFoo), HandlerType = typeof(ArgFooMessageHandler) }
        };
        var sp = new ServiceCollection().BuildServiceProvider();
        var registry = new AggregatorRegistry(refs, sp, NullLogger<AggregatorRegistry>.Instance);

        Assert.False(registry.TryGet(typeof(ArgFoo), out _));
    }

    [Fact]
    public void Descriptor_BuildTypedList_ReturnsPopulatedTypedList()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ArgFoo), HandlerType = typeof(ArgFooAggregator) }
        };
        var sp = BuildServiceProvider<ArgFoo, ArgFooAggregator>();
        var registry = new AggregatorRegistry(refs, sp, NullLogger<AggregatorRegistry>.Instance);

        Assert.True(registry.TryGet(typeof(ArgFoo), out var descriptor));

        var raw = new List<object> { new ArgFoo(Guid.NewGuid()) { Val = "a" }, new ArgFoo(Guid.NewGuid()) { Val = "b" } };
        var typed = descriptor!.BuildTypedList(raw);

        Assert.IsType<List<ArgFoo>>(typed);
        Assert.Equal(2, typed.Count);
        Assert.Equal("a", ((ArgFoo)typed[0]!).Val);
    }

    [Fact]
    public void Descriptor_InvokeExecute_CallsExecuteOnAggregator()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ArgFoo), HandlerType = typeof(ArgFooAggregator) }
        };
        var sp = BuildServiceProvider<ArgFoo, ArgFooAggregator>();
        var registry = new AggregatorRegistry(refs, sp, NullLogger<AggregatorRegistry>.Instance);

        Assert.True(registry.TryGet(typeof(ArgFoo), out var descriptor));

        var agg = new ArgFooAggregator();
        var list = new List<ArgFoo> { new(Guid.NewGuid()) { Val = "x" } };
        descriptor!.InvokeExecute(agg, list);

        Assert.NotNull(agg.Executed);
        Assert.Single(agg.Executed!);
        Assert.Equal("x", agg.Executed![0].Val);
    }

    private static IServiceProvider BuildServiceProvider<TMsg, TAgg>()
        where TMsg : Message where TAgg : Aggregator<TMsg>
    {
        var services = new ServiceCollection();
        services.AddTransient<Aggregator<TMsg>, TAgg>();
        return services.BuildServiceProvider();
    }
}

file class ArgFoo : Message { public ArgFoo(Guid c) : base(c) { } public string Val { get; set; } = ""; }

file class ArgFooAggregator : Aggregator<ArgFoo>
{
    public IList<ArgFoo>? Executed { get; private set; }
    public override int BatchSize() => 42;
    public override TimeSpan Timeout() => TimeSpan.FromSeconds(7);
    public override void Execute(IList<ArgFoo> messages) { Executed = messages; }
}

file class ArgSecondFooAggregator : Aggregator<ArgFoo>
{
    public override void Execute(IList<ArgFoo> messages) { }
}

file class ArgFooMessageHandler : IMessageHandler<ArgFoo>
{
    public IConsumeContext? Context { get; set; }
    public Task HandleAsync(ArgFoo message) => Task.CompletedTask;
}
```

- [ ] **Step 3.3: Run tests — expect compile failure**

```bash
dotnet test src/ServiceConnect.UnitTests -v quiet --filter "AggregatorRegistryTests"
```

Expected: `AggregatorRegistry` type doesn't exist.

- [ ] **Step 3.4: Create `AggregatorRegistry.cs`**

Path: `src/ServiceConnect/Services/Processors/AggregatorRegistry.cs`

```csharp
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed class AggregatorRegistry
{
    private readonly Dictionary<Type, AggregatorDescriptor> _descriptors = new();

    internal AggregatorRegistry(
        IList<HandlerReference> handlerReferences,
        IServiceProvider serviceProvider,
        ILogger<AggregatorRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(handlerReferences);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(logger);

        foreach (var href in handlerReferences)
        {
            var aggregatorBaseType = FindAggregatorBaseType(href.HandlerType, href.MessageType);
            if (aggregatorBaseType == null)
                continue;

            if (_descriptors.ContainsKey(href.MessageType))
            {
                throw new InvalidOperationException(
                    $"Duplicate aggregator registration for message type '{href.MessageType.FullName}'. Only one Aggregator<T> may be registered per message type.");
            }

            var descriptor = BuildDescriptor(href.MessageType, aggregatorBaseType, serviceProvider);
            _descriptors[href.MessageType] = descriptor;

            logger.LogDebug(
                "Registered aggregator descriptor: message={MessageType}, aggregator={AggregatorType}, batchSize={BatchSize}, timeout={Timeout}",
                href.MessageType.Name, href.HandlerType.Name, descriptor.BatchSize, descriptor.Timeout);
        }
    }

    internal bool TryGet(Type messageType, [NotNullWhen(true)] out AggregatorDescriptor? descriptor)
        => _descriptors.TryGetValue(messageType, out descriptor);

    private static Type? FindAggregatorBaseType(Type handlerType, Type messageType)
    {
        if (handlerType.BaseType is not { IsGenericType: true } baseType)
            return null;
        if (baseType.GetGenericTypeDefinition() != typeof(Aggregator<>))
            return null;
        if (baseType.GetGenericArguments()[0] != messageType)
            return null;
        return baseType;
    }

    private static AggregatorDescriptor BuildDescriptor(Type messageType, Type aggregatorBaseType, IServiceProvider sp)
    {
        object aggregator;
        try
        {
            aggregator = sp.GetRequiredService(aggregatorBaseType);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"AggregatorRegistry could not materialize aggregator for message type '{messageType.FullName}'. Ensure the aggregator is registered in DI.", ex);
        }

        var batchSize = (int)aggregatorBaseType.GetMethod(nameof(Aggregator<Message>.BatchSize))!.Invoke(aggregator, null)!;
        var timeout = (TimeSpan)aggregatorBaseType.GetMethod(nameof(Aggregator<Message>.Timeout))!.Invoke(aggregator, null)!;

        return new AggregatorDescriptor(
            MessageType: messageType,
            AggregatorBaseType: aggregatorBaseType,
            AggregatorName: aggregatorBaseType.FullName!,
            BatchSize: batchSize,
            Timeout: timeout,
            BuildTypedList: CompileBuildTypedList(messageType),
            InvokeExecute: CompileInvokeExecute(aggregatorBaseType, messageType));
    }

    // One-time reflection at startup is acceptable — reading BatchSize/Timeout from an instance.
    // The compiled delegates below are what get called per-message.

    private static Func<IList<object>, IList> CompileBuildTypedList(Type messageType)
    {
        // Build: (IList<object> raw) => { var list = new List<TMsg>(); foreach (var m in raw) list.Add((TMsg)m); return list; }
        var listType = typeof(List<>).MakeGenericType(messageType);

        var rawParam = Expression.Parameter(typeof(IList<object>), "raw");
        var listVar = Expression.Variable(listType, "list");
        var itemVar = Expression.Variable(typeof(object), "item");

        var listCtor = listType.GetConstructor(Type.EmptyTypes)!;
        var addMethod = listType.GetMethod("Add")!;
        var getEnumeratorMethod = typeof(IEnumerable<object>).GetMethod("GetEnumerator")!;
        var moveNextMethod = typeof(System.Collections.IEnumerator).GetMethod("MoveNext")!;
        var currentProp = typeof(IEnumerator<object>).GetProperty("Current")!;

        var enumeratorVar = Expression.Variable(typeof(IEnumerator<object>), "enumerator");
        var breakLabel = Expression.Label("break");

        var block = Expression.Block(
            new[] { listVar, enumeratorVar },
            Expression.Assign(listVar, Expression.New(listCtor)),
            Expression.Assign(enumeratorVar, Expression.Call(rawParam, getEnumeratorMethod)),
            Expression.Loop(
                Expression.IfThenElse(
                    Expression.Call(enumeratorVar, moveNextMethod),
                    Expression.Call(listVar, addMethod, Expression.Convert(Expression.Property(enumeratorVar, currentProp), messageType)),
                    Expression.Break(breakLabel)),
                breakLabel),
            Expression.Convert(listVar, typeof(IList)));

        return Expression.Lambda<Func<IList<object>, IList>>(block, rawParam).Compile();
    }

    private static Action<object, object> CompileInvokeExecute(Type aggregatorBaseType, Type messageType)
    {
        var aggParam = Expression.Parameter(typeof(object), "aggregator");
        var listParam = Expression.Parameter(typeof(object), "list");

        var aggCast = Expression.Convert(aggParam, aggregatorBaseType);
        var listCast = Expression.Convert(listParam, typeof(IList<>).MakeGenericType(messageType));

        var method = aggregatorBaseType.GetMethod(nameof(Aggregator<Message>.Execute))!;
        var call = Expression.Call(aggCast, method, listCast);

        return Expression.Lambda<Action<object, object>>(call, aggParam, listParam).Compile();
    }
}
```

- [ ] **Step 3.5: Run the registry tests — expect pass**

```bash
dotnet test src/ServiceConnect.UnitTests -v quiet --filter "AggregatorRegistryTests"
```

Expected: all 8 tests green.

- [ ] **Step 3.6: Rewire `AggregatorProcessor.cs`**

Path: `src/ServiceConnect/Services/Processors/AggregatorProcessor.cs`

Full replacement:

```csharp
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

public sealed class AggregatorProcessor(
    AggregatorRegistry registry,
    IServiceProvider serviceProvider,
    ILogger<AggregatorProcessor> logger) : IMessageProcessor, IDisposable
{
    private readonly ConcurrentDictionary<string, Timer> _timers = new();
#if NET9_0_OR_GREATER
    private readonly Lock _flushLock = new();
#else
    private readonly object _flushLock = new();
#endif

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

                var timer = new Timer(_ => OnTimerFired(descriptor),
                    null, descriptor.Timeout, Timeout.InfiniteTimeSpan);
                _timers[descriptor.AggregatorName] = timer;
            }
        }

        return ProcessResult.Handled;
    }

    private void OnTimerFired(AggregatorDescriptor descriptor)
    {
        if (!_timers.ContainsKey(descriptor.AggregatorName))
            return;

        _ = FlushAggregatorAsync(descriptor, CancellationToken.None)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    logger.LogError(t.Exception, "Error flushing aggregator {AggregatorName} on timeout", descriptor.AggregatorName);
            }, TaskContinuationOptions.OnlyOnFaulted);
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

    public void Dispose()
    {
        foreach (var kvp in _timers)
            kvp.Value.Dispose();
        _timers.Clear();
    }
}
```

- [ ] **Step 3.7: Update `AggregatorProcessorTests.cs` — supply registry**

Path: `src/ServiceConnect.UnitTests/Processors/AggregatorProcessorTests.cs`

Add using:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
```

Replace each `new AggregatorProcessor(provider, ...)` with a version that takes a registry.

**`ProcessAsync_NoAggregator_ReturnsNotHandled`** — empty registry:

```csharp
var registry = new AggregatorRegistry(new List<HandlerReference>(), provider, NullLogger<AggregatorRegistry>.Instance);
var processor = new AggregatorProcessor(registry, provider, new Mock<ILogger<AggregatorProcessor>>().Object);
```

**`ProcessAsync_BatchComplete_ExecutesAggregator`** — real registry with the aggregator. Replace the `using var processor = new AggregatorProcessor(provider, ...)` with:

```csharp
var registry = new AggregatorRegistry(handlerRefs, provider, NullLogger<AggregatorRegistry>.Instance);
using var processor = new AggregatorProcessor(registry, provider, new Mock<ILogger<AggregatorProcessor>>().Object);
```

- [ ] **Step 3.8: Register `AggregatorRegistry` in DI**

Path: `src/ServiceConnect/ServiceCollectionExtensions.cs`

Immediately after the `StreamHandlerRegistry` factory registration added in Task 2, add:

```csharp
        services.TryAddSingleton<Services.Processors.AggregatorRegistry>(sp => new Services.Processors.AggregatorRegistry(
            sp.GetRequiredService<IList<HandlerReference>>(),
            sp,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.Processors.AggregatorRegistry>>()));
```

`AggregatorProcessor`'s `TryAddSingleton<AggregatorProcessor>()` picks up the new parameter via constructor injection.

- [ ] **Step 3.9: Run the full unit suite**

```bash
dotnet test src/ServiceConnect.UnitTests -v quiet
```

Expected: all tests pass.

- [ ] **Step 3.10: Run the E2E suite**

```bash
sg docker -c "dotnet test src/ServiceConnect.EndToEndTests -v quiet"
```

Expected: all E2E tests pass.

- [ ] **Step 3.11: Commit**

```bash
git add src/ServiceConnect/Services/Processors/AggregatorDescriptor.cs \
        src/ServiceConnect/Services/Processors/AggregatorRegistry.cs \
        src/ServiceConnect/Services/Processors/AggregatorProcessor.cs \
        src/ServiceConnect/ServiceCollectionExtensions.cs \
        src/ServiceConnect.UnitTests/Processors/AggregatorRegistryTests.cs \
        src/ServiceConnect.UnitTests/Processors/AggregatorProcessorTests.cs
git commit -m "$(cat <<'EOF'
refactor: introduce AggregatorRegistry and remove reflection from AggregatorProcessor (R-009)

Pre-computed registry keyed by message type throws InvalidOperationException on duplicate
aggregator registrations. BatchSize and Timeout are captured once at startup rather than
per-message. Compiled Expression-tree delegates replace MethodInfo.Invoke / GetProperty.
FindAggregatorType service-locator scan is deleted; registry does the lookup once.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Bus warm-up for the three new registries

**Commit message (at the end):**
```
refactor: warm up MessageHandler, StreamHandler, and Aggregator registries in Bus (R-009)

Bus.StartConsumingAsync now touches the three new registries alongside the existing
ProcessManagerHandlerRegistry, so registry construction errors surface at application
startup rather than mid-traffic.
```

**Files:**
- Modify: `src/ServiceConnect/Bus.cs`
- Modify: `src/ServiceConnect/ServiceCollectionExtensions.cs`

- [ ] **Step 4.1: Update `Bus.cs` ctor and fields**

Path: `src/ServiceConnect/Bus.cs`

Add three fields after `_processManagerRegistry` (around line 19):

```csharp
    private readonly Services.Processors.MessageHandlerRegistry _messageHandlerRegistry;
    private readonly Services.Processors.StreamHandlerRegistry _streamHandlerRegistry;
    private readonly Services.Processors.AggregatorRegistry _aggregatorRegistry;
```

Update the internal ctor signature to add three parameters immediately after `processManagerRegistry`:

```csharp
    internal Bus(
        IMessageSerializer serializer,
        IFilterPipeline filterPipeline,
        ISendMessagePipeline sendPipeline,
        IRequestReplyManager requestReplyManager,
        ILogger<Bus> logger,
        IQueueConfiguration queueConfig,
        IMessageDispatcher dispatcher,
        IList<HandlerReference> handlerReferences,
        Services.Processors.ProcessManagerHandlerRegistry processManagerRegistry,
        Services.Processors.MessageHandlerRegistry messageHandlerRegistry,
        Services.Processors.StreamHandlerRegistry streamHandlerRegistry,
        Services.Processors.AggregatorRegistry aggregatorRegistry,
        IConsumer? consumer = null,
        IProducer? producer = null)
```

Add the three assignments in the ctor body, immediately after `_processManagerRegistry = ...`:

```csharp
        _messageHandlerRegistry = messageHandlerRegistry ?? throw new ArgumentNullException(nameof(messageHandlerRegistry));
        _streamHandlerRegistry = streamHandlerRegistry ?? throw new ArgumentNullException(nameof(streamHandlerRegistry));
        _aggregatorRegistry = aggregatorRegistry ?? throw new ArgumentNullException(nameof(aggregatorRegistry));
```

- [ ] **Step 4.2: Add warm-up touches in `StartConsumingAsync`**

Path: `src/ServiceConnect/Bus.cs`

Locate the existing touch (around line 211):

```csharp
            _ = _processManagerRegistry; // touch singleton; duplicate-handler registration would have thrown at DI resolution time
```

Replace with the four-registry block (keep the comment, update it to describe all four):

```csharp
            // touch singletons; any duplicate-handler registration in the registries would have thrown at DI resolution time
            _ = _processManagerRegistry;
            _ = _messageHandlerRegistry;
            _ = _streamHandlerRegistry;
            _ = _aggregatorRegistry;
```

- [ ] **Step 4.3: Update the `Bus` factory in DI**

Path: `src/ServiceConnect/ServiceCollectionExtensions.cs`

Locate the factory (around line 124). Current:

```csharp
        services.TryAddSingleton<IBus>(sp => new Bus(
            sp.GetRequiredService<IMessageSerializer>(),
            sp.GetRequiredService<IFilterPipeline>(),
            sp.GetRequiredService<ISendMessagePipeline>(),
            sp.GetRequiredService<IRequestReplyManager>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Bus>>(),
            sp.GetRequiredService<IQueueConfiguration>(),
            sp.GetRequiredService<IMessageDispatcher>(),
            sp.GetRequiredService<IList<HandlerReference>>(),
            sp.GetRequiredService<Services.Processors.ProcessManagerHandlerRegistry>(),
            sp.GetService<IConsumer>(),
            sp.GetService<IProducer>()));
```

Replace with:

```csharp
        services.TryAddSingleton<IBus>(sp => new Bus(
            sp.GetRequiredService<IMessageSerializer>(),
            sp.GetRequiredService<IFilterPipeline>(),
            sp.GetRequiredService<ISendMessagePipeline>(),
            sp.GetRequiredService<IRequestReplyManager>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Bus>>(),
            sp.GetRequiredService<IQueueConfiguration>(),
            sp.GetRequiredService<IMessageDispatcher>(),
            sp.GetRequiredService<IList<HandlerReference>>(),
            sp.GetRequiredService<Services.Processors.ProcessManagerHandlerRegistry>(),
            sp.GetRequiredService<Services.Processors.MessageHandlerRegistry>(),
            sp.GetRequiredService<Services.Processors.StreamHandlerRegistry>(),
            sp.GetRequiredService<Services.Processors.AggregatorRegistry>(),
            sp.GetService<IConsumer>(),
            sp.GetService<IProducer>()));
```

- [ ] **Step 4.4: Update `BusTests.cs`**

Path: `src/ServiceConnect.UnitTests/BusTests.cs`

There are multiple `new Bus(...)` call sites. Handle them in three groups:

**Group A — the shared setup around line 48–61.** Add three fields after `_processManagerRegistry`:

```csharp
        private readonly MessageHandlerRegistry _messageHandlerRegistry;
        private readonly StreamHandlerRegistry _streamHandlerRegistry;
        private readonly AggregatorRegistry _aggregatorRegistry;
```

Initialize them in the ctor immediately after `_processManagerRegistry = ...`:

```csharp
            _messageHandlerRegistry = new MessageHandlerRegistry(
                new List<HandlerReference>(),
                NullLogger<MessageHandlerRegistry>.Instance);
            _streamHandlerRegistry = new StreamHandlerRegistry(
                new List<HandlerReference>(),
                NullLogger<StreamHandlerRegistry>.Instance);
            _aggregatorRegistry = new AggregatorRegistry(
                new List<HandlerReference>(),
                new ServiceCollection().BuildServiceProvider(),
                NullLogger<AggregatorRegistry>.Instance);
```

Update the main `_bus = new Bus(...)` call to pass all four registries in order (ProcessManager, Message, Stream, Aggregator):

```csharp
            _bus = new Bus(
                _mockSerializer.Object,
                _mockFilterPipeline.Object,
                _mockSendPipeline.Object,
                _mockRequestReplyManager.Object,
                _mockLogger.Object,
                _mockQueueConfig.Object,
                _mockDispatcher.Object,
                _handlerReferences,
                _processManagerRegistry,
                _messageHandlerRegistry,
                _streamHandlerRegistry,
                _aggregatorRegistry);
```

Add required usings at the top of the file:

```csharp
using Microsoft.Extensions.DependencyInjection;
```

(`NullLogger<T>.Instance` and `ServiceConnect.Services.Processors` are likely already imported; add them if not.)

**Group B — the one-off `var bus = new Bus(...)` around line 84.** It currently passes an extra `mockConsumer.Object` after `_processManagerRegistry`. Update to pass all four registries and keep the optional consumer:

```csharp
            var bus = new Bus(
                _mockSerializer.Object,
                _mockFilterPipeline.Object,
                _mockSendPipeline.Object,
                _mockRequestReplyManager.Object,
                _mockLogger.Object,
                _mockQueueConfig.Object,
                _mockDispatcher.Object,
                _handlerReferences,
                _processManagerRegistry,
                _messageHandlerRegistry,
                _streamHandlerRegistry,
                _aggregatorRegistry,
                mockConsumer.Object);
```

**Group C — the `Constructor_ShouldThrow_WhenDependencyIsNull` test.** It currently has ~9 `Assert.Throws<ArgumentNullException>(() => new Bus(..., _processManagerRegistry))` lines, each nulling a different required dependency. Update each line to also pass the three new registries at the end (before the optional consumer/producer params), and add three new lines asserting each of the new registries throws when null. The test's final set of lines should look like:

```csharp
            // Existing lines: add trailing registry args to each
            Assert.Throws<ArgumentNullException>(() => new Bus(null!, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, _processManagerRegistry, _messageHandlerRegistry, _streamHandlerRegistry, _aggregatorRegistry));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, null!, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, _processManagerRegistry, _messageHandlerRegistry, _streamHandlerRegistry, _aggregatorRegistry));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, null!, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, _processManagerRegistry, _messageHandlerRegistry, _streamHandlerRegistry, _aggregatorRegistry));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, null!, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, _processManagerRegistry, _messageHandlerRegistry, _streamHandlerRegistry, _aggregatorRegistry));
            // ... same pattern for each existing null-check line (logger, queueConfig, dispatcher, handlerReferences, processManagerRegistry)
            // Three new null-check lines:
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, _processManagerRegistry, null!, _streamHandlerRegistry, _aggregatorRegistry));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, _processManagerRegistry, _messageHandlerRegistry, null!, _aggregatorRegistry));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, _processManagerRegistry, _messageHandlerRegistry, _streamHandlerRegistry, null!));
```

- [ ] **Step 4.5: Run the full unit suite**

```bash
dotnet test src/ServiceConnect.UnitTests -v quiet
```

Expected: all tests pass. Any failures are most likely missed `new Bus(...)` sites from the previous step.

- [ ] **Step 4.6: Run the E2E suite**

```bash
sg docker -c "dotnet test src/ServiceConnect.EndToEndTests -v quiet"
```

Expected: all E2E tests pass.

- [ ] **Step 4.7: Commit**

```bash
git add src/ServiceConnect/Bus.cs src/ServiceConnect/ServiceCollectionExtensions.cs src/ServiceConnect.UnitTests
git commit -m "$(cat <<'EOF'
refactor: warm up MessageHandler, StreamHandler, and Aggregator registries in Bus (R-009)

Bus.StartConsumingAsync now touches the three new registries alongside the existing
ProcessManagerHandlerRegistry, so registry construction errors surface at application
startup rather than mid-traffic.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Doc update — mark R-009 done in Group C-4

**Files:**
- Modify: `docs/remaining-issues.md`

- [ ] **Step 5.1: Update the header paragraph**

Path: `docs/remaining-issues.md`

Replace the current opening paragraph (lines 1–3 approximately):

```markdown
# Remaining Issues — Verified but Deferred

Issues verified against source code on 2026-04-12. R-016/B-01 (CancellationToken) and R-034 (race condition) completed in Group C-1 on 2026-04-13. R-017/R-018 (async filter pipeline + fail-closed dedup) and R-032 (settings DI) completed in Group C-2 on 2026-04-13. R-020/R-021 (Client + ProcessManagerProcessor SRP refactor) completed in Group C-3 on 2026-04-13. Tackle remaining items after further discussion.
```

With:

```markdown
# Remaining Issues — Verified but Deferred

Issues verified against source code on 2026-04-12. R-016/B-01 (CancellationToken) and R-034 (race condition) completed in Group C-1 on 2026-04-13. R-017/R-018 (async filter pipeline + fail-closed dedup) and R-032 (settings DI) completed in Group C-2 on 2026-04-13. R-020/R-021 (Client + ProcessManagerProcessor SRP refactor) completed in Group C-3 on 2026-04-13. R-009 (service locator + reflection in remaining three processors) completed in Group C-4 on 2026-04-13. Tackle remaining items after further discussion.
```

- [ ] **Step 5.2: Update the R-009 row in the "From Deferred Issues (Confirmed Real)" table**

Path: `docs/remaining-issues.md`

Current row:

```markdown
| R-009 | Architecture | Service locator anti-pattern in all Processors (HandlerProcessor, ProcessManagerProcessor, StreamProcessor, AggregatorProcessor) | Large — inherent to message dispatch design |
```

Replace with:

```markdown
| R-009 | Architecture | Service locator anti-pattern in all Processors (HandlerProcessor, ProcessManagerProcessor, StreamProcessor, AggregatorProcessor) | **Done** (Groups C-3 + C-4) — reflection + discovery moved to four internal registries with compiled expression-tree delegates; dispatch-time `IServiceProvider.GetService` for handler types unavoidable (dispatch is by runtime type) |
```

- [ ] **Step 5.3: Run the full suites one last time**

```bash
dotnet test src/ServiceConnect.UnitTests -v quiet
sg docker -c "dotnet test src/ServiceConnect.EndToEndTests -v quiet"
```

Expected: both suites pass.

- [ ] **Step 5.4: Commit**

```bash
git add docs/remaining-issues.md
git commit -m "$(cat <<'EOF'
docs: mark R-009 done (processor registry refactor, Group C-4)

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```
