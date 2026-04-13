# SRP Refactor — Client + ProcessManagerProcessor — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor `Client.cs` (313 lines, RabbitMQ transport) into three collaborators and `ProcessManagerProcessor.cs` into a thin orchestrator over a cached descriptor registry. Closes R-020/R-021.

**Architecture:** Client becomes a thin forwarding facade over `RabbitMqConsumerHost` (lifecycle + ack/nack), `MessageRetryHandler` (failure path), and `MessageAuditPublisher` (success path). ProcessManagerProcessor delegates all reflection to a singleton `ProcessManagerHandlerRegistry` that eagerly builds compiled-delegate descriptors per message type at first use. No public-API changes.

**Tech Stack:** C# 12 / .NET 8–10, xUnit, Moq, RabbitMQ.Client 7.2.1, `Microsoft.Extensions.Logging.Abstractions`, `System.Linq.Expressions` for delegate compilation.

**Design spec:** `docs/superpowers/specs/2026-04-13-srp-client-and-processmanager-design.md`.

---

## File Structure

**Created:**
- `src/ServiceConnect/Services/Processors/ProcessManagerDescriptor.cs` — internal `sealed record` holding compiled delegates for one `(messageType, dataType)` pair.
- `src/ServiceConnect/Services/Processors/ProcessManagerHandlerRegistry.cs` — singleton; builds descriptors at construction, exposes `TryGet`.
- `src/ServiceConnect.UnitTests/Processors/ProcessManagerHandlerRegistryTests.cs` — new test fixture.
- `src/ServiceConnect.Client.RabbitMQ/HeaderHelpers.cs` — internal static helpers extracted from `Client`.
- `src/ServiceConnect.Client.RabbitMQ/MessageRetryHandler.cs` — failure-path policy.
- `src/ServiceConnect.Client.RabbitMQ/MessageAuditPublisher.cs` — success-path policy.
- `src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs` — channel/consumer lifecycle + `Event`/`ProcessMessage`.
- `src/ServiceConnect.UnitTests/MessageRetryHandlerTests.cs`.
- `src/ServiceConnect.UnitTests/MessageAuditPublisherTests.cs`.
- `src/ServiceConnect.UnitTests/RabbitMqConsumerHostTests.cs`.

**Modified:**
- `src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs` — rewritten.
- `src/ServiceConnect/ServiceCollectionExtensions.cs` — registers the registry.
- `src/ServiceConnect/Bus.cs` — warm up registry inside `StartConsumingAsync`.
- `src/ServiceConnect.UnitTests/Processors/ProcessManagerProcessorTests.cs` — updated for new ctor.
- `src/ServiceConnect.Client.RabbitMQ/Client.cs` — shrinks to thin facade.
- `docs/remaining-issues.md` — mark R-020/R-021 done.

---

## Test commands (used throughout)

- **Unit tests (main):** `dotnet test src/ServiceConnect.UnitTests -v quiet`
- **Unit tests (single fixture):** `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~<FixtureName>" -v normal`
- **Build only:** `dotnet build src/ServiceConnect.sln`
- **E2E (requires docker group):** `sg docker -c "dotnet test src/ServiceConnect.EndToEndTests -v quiet"`

---

## Task 1: Introduce `ProcessManagerDescriptor` + `ProcessManagerHandlerRegistry`

Ships the new registry as a self-contained component. No caller rewires to it yet — that happens in Task 2. Keeps this commit small and independently reviewable.

**Files:**
- Create: `src/ServiceConnect/Services/Processors/ProcessManagerDescriptor.cs`
- Create: `src/ServiceConnect/Services/Processors/ProcessManagerHandlerRegistry.cs`
- Create: `src/ServiceConnect.UnitTests/Processors/ProcessManagerHandlerRegistryTests.cs`

### Step 1.1: Write the failing test fixture

Create `src/ServiceConnect.UnitTests/Processors/ProcessManagerHandlerRegistryTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using ServiceConnect.Interfaces;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class ProcessManagerHandlerRegistryTests
{
    [Fact]
    public void TryGet_ReturnsDescriptor_ForRegisteredMessageType()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(FooMessage), HandlerType = typeof(FooHandler) }
        };

        var registry = new ProcessManagerHandlerRegistry(refs, NullLogger<ProcessManagerHandlerRegistry>.Instance);

        Assert.True(registry.TryGet(typeof(FooMessage), out var descriptor));
        Assert.Equal(typeof(FooMessage), descriptor!.MessageType);
        Assert.Equal(typeof(FooData), descriptor.DataType);
        Assert.Equal(typeof(IProcessHandler<FooData, FooMessage>), descriptor.ProcessHandlerInterfaceType);
    }

    [Fact]
    public void TryGet_ReturnsFalse_ForUnknownMessageType()
    {
        var registry = new ProcessManagerHandlerRegistry(
            new List<HandlerReference>(),
            NullLogger<ProcessManagerHandlerRegistry>.Instance);

        Assert.False(registry.TryGet(typeof(FooMessage), out var descriptor));
        Assert.Null(descriptor);
    }

    [Fact]
    public void Construction_IgnoresNonProcessHandlers()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(FooMessage), HandlerType = typeof(PlainFooHandler) }
        };

        var registry = new ProcessManagerHandlerRegistry(refs, NullLogger<ProcessManagerHandlerRegistry>.Instance);

        Assert.False(registry.TryGet(typeof(FooMessage), out _));
    }

    [Fact]
    public void Construction_ThrowsOnDuplicateMessageMapping()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(FooMessage), HandlerType = typeof(FooHandler) },
            new() { MessageType = typeof(FooMessage), HandlerType = typeof(SecondFooHandler) }
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ProcessManagerHandlerRegistry(refs, NullLogger<ProcessManagerHandlerRegistry>.Instance));

        Assert.Contains(nameof(FooMessage), ex.Message);
    }

    [Fact]
    public void Descriptor_CreateData_CreatesFreshInstance()
    {
        var registry = BuildFooRegistry();
        Assert.True(registry.TryGet(typeof(FooMessage), out var descriptor));

        var a = descriptor!.CreateData();
        var b = descriptor.CreateData();

        Assert.IsType<FooData>(a);
        Assert.NotSame(a, b);
    }

    [Fact]
    public void Descriptor_SetCorrelationId_WritesProperty()
    {
        var registry = BuildFooRegistry();
        Assert.True(registry.TryGet(typeof(FooMessage), out var descriptor));

        var data = descriptor!.CreateData();
        var correlationId = Guid.NewGuid();
        descriptor.SetCorrelationId(data, correlationId);

        Assert.Equal(correlationId, data.CorrelationId);
    }

    [Fact]
    public void Descriptor_SetHandlerContext_WritesHandlerContext()
    {
        var registry = BuildFooRegistry();
        Assert.True(registry.TryGet(typeof(FooMessage), out var descriptor));

        var handler = new FooHandler();
        var ctx = new FakeConsumeContext();
        descriptor!.SetHandlerContext(handler, ctx);

        Assert.Same(ctx, handler.Context);
    }

    [Fact]
    public void Descriptor_ConfigureMapper_InvokesHandlerConfigureMapper()
    {
        var registry = BuildFooRegistry();
        Assert.True(registry.TryGet(typeof(FooMessage), out var descriptor));

        var handler = new FooHandler();
        var mapper = new DefaultProcessManagerPropertyMapperStub();
        descriptor!.ConfigureMapper(handler, mapper);

        // FooHandler uses the default interface method which adds one mapping (CorrelationId → CorrelationId).
        Assert.Single(mapper.Mappings);
    }

    [Fact]
    public async Task Descriptor_InvokeHandleAsync_PassesMessageAndData()
    {
        var registry = BuildFooRegistry();
        Assert.True(registry.TryGet(typeof(FooMessage), out var descriptor));

        var handler = new FooHandler();
        var msg = new FooMessage(Guid.NewGuid());
        var data = new FooData { CorrelationId = Guid.NewGuid() };

        await descriptor!.InvokeHandleAsync(handler, msg, data);

        Assert.Same(msg, handler.ReceivedMessage);
        Assert.Same(data, handler.ReceivedData);
    }

    [Fact]
    public void Descriptor_GetPersistenceDataData_ReadsDataProperty()
    {
        var registry = BuildFooRegistry();
        Assert.True(registry.TryGet(typeof(FooMessage), out var descriptor));

        var data = new FooData();
        var persistence = new FooPersistenceData { Data = data };

        var result = descriptor!.GetPersistenceDataData(persistence);

        Assert.Same(data, result);
    }

    private static ProcessManagerHandlerRegistry BuildFooRegistry()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(FooMessage), HandlerType = typeof(FooHandler) }
        };
        return new ProcessManagerHandlerRegistry(refs, NullLogger<ProcessManagerHandlerRegistry>.Instance);
    }
}

file class FooMessage : Message
{
    public FooMessage() : base(Guid.Empty) { }
    public FooMessage(Guid correlationId) : base(correlationId) { }
}

file class FooData : IProcessManagerData
{
    public Guid CorrelationId { get; set; }
}

file class FooPersistenceData : IPersistenceData<FooData>
{
    public FooData Data { get; set; } = new();
}

file class FooHandler : IProcessHandler<FooData, FooMessage>
{
    public IConsumeContext? Context { get; set; }
    public FooMessage? ReceivedMessage { get; private set; }
    public FooData? ReceivedData { get; private set; }

    public Task HandleAsync(FooMessage message, FooData data)
    {
        ReceivedMessage = message;
        ReceivedData = data;
        return Task.CompletedTask;
    }
}

file class SecondFooHandler : IProcessHandler<FooData, FooMessage>
{
    public IConsumeContext? Context { get; set; }
    public Task HandleAsync(FooMessage message, FooData data) => Task.CompletedTask;
}

file class PlainFooHandler : IMessageHandler<FooMessage>
{
    public Task ExecuteAsync(FooMessage message) => Task.CompletedTask;
}

file class FakeConsumeContext : IConsumeContext
{
    public IBus Bus => throw new NotImplementedException();
    public IDictionary<string, object> Headers { get; set; } = new Dictionary<string, object>();
    public string? MessageId => null;
    public Guid CorrelationId => Guid.Empty;
    public CancellationToken CancellationToken { get; set; }
    public Task ReplyAsync<TReply>(TReply message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default) where TReply : Message
        => throw new NotImplementedException();
}

file class DefaultProcessManagerPropertyMapperStub : IProcessManagerPropertyMapper
{
    public List<ProcessManagerToMessageMap> Mappings { get; set; } = [];

    public void ConfigureMapping<TProcessManagerData, TMessage>(
        System.Linq.Expressions.Expression<Func<TProcessManagerData, object>> processManagerProperty,
        System.Linq.Expressions.Expression<Func<TMessage, object>> messageExpression)
        where TProcessManagerData : IProcessManagerData
    {
        Mappings.Add(new ProcessManagerToMessageMap { MessageType = typeof(TMessage) });
    }
}
```

- [ ] **Step 1.1: Create the failing test file** — paste the code above.

- [ ] **Step 1.2: Confirm build fails** — `ProcessManagerHandlerRegistry` and `ProcessManagerDescriptor` don't exist yet.

Run: `dotnet build src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj`
Expected: FAIL with CS0246 `type 'ProcessManagerHandlerRegistry' could not be found`.

### Step 1.3: Create the descriptor record

Create `src/ServiceConnect/Services/Processors/ProcessManagerDescriptor.cs`:

```csharp
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

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

- [ ] **Step 1.3: Create descriptor file.**

### Step 1.4: Create the registry

Create `src/ServiceConnect/Services/Processors/ProcessManagerHandlerRegistry.cs`:

```csharp
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

public sealed class ProcessManagerHandlerRegistry
{
    private readonly Dictionary<Type, ProcessManagerDescriptor> _descriptors = new();

    public ProcessManagerHandlerRegistry(
        IList<HandlerReference> handlerReferences,
        ILogger<ProcessManagerHandlerRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(handlerReferences);
        ArgumentNullException.ThrowIfNull(logger);

        foreach (var href in handlerReferences)
        {
            var processHandlerInterface = href.HandlerType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType
                    && i.GetGenericTypeDefinition() == typeof(IProcessHandler<,>)
                    && i.GetGenericArguments()[1] == href.MessageType);

            if (processHandlerInterface == null)
                continue;

            if (_descriptors.ContainsKey(href.MessageType))
            {
                throw new InvalidOperationException(
                    $"Duplicate process-manager handler registration for message type '{href.MessageType.FullName}'. Only one IProcessHandler<TData,TMessage> may be registered per message type.");
            }

            var dataType = processHandlerInterface.GetGenericArguments()[0];
            _descriptors[href.MessageType] = BuildDescriptor(href.MessageType, dataType, processHandlerInterface);

            logger.LogDebug(
                "Registered process-manager descriptor: message={MessageType}, data={DataType}, handler={HandlerType}",
                href.MessageType.Name, dataType.Name, href.HandlerType.Name);
        }
    }

    public bool TryGet(Type messageType, out ProcessManagerDescriptor? descriptor)
        => _descriptors.TryGetValue(messageType, out descriptor);

    internal static ProcessManagerDescriptor BuildDescriptor(
        Type messageType, Type dataType, Type processHandlerInterfaceType)
    {
        var persistenceInterfaceType = typeof(IPersistenceData<>).MakeGenericType(dataType);

        return new ProcessManagerDescriptor(
            MessageType: messageType,
            DataType: dataType,
            ProcessHandlerInterfaceType: processHandlerInterfaceType,
            CreateData: CompileCreateData(dataType),
            SetCorrelationId: CompileSetCorrelationId(),
            SetHandlerContext: CompileSetHandlerContext(processHandlerInterfaceType),
            ConfigureMapper: CompileConfigureMapper(processHandlerInterfaceType),
            FindData: CompileFindData(dataType),
            GetPersistenceDataData: CompileGetPersistenceDataData(persistenceInterfaceType),
            UpdateData: CompileUpdateData(dataType, persistenceInterfaceType),
            InvokeHandleAsync: CompileInvokeHandleAsync(processHandlerInterfaceType, messageType, dataType));
    }

    private static Func<IProcessManagerData> CompileCreateData(Type dataType)
    {
        var newExpr = Expression.New(dataType);
        var cast = Expression.Convert(newExpr, typeof(IProcessManagerData));
        return Expression.Lambda<Func<IProcessManagerData>>(cast).Compile();
    }

    private static Action<IProcessManagerData, Guid> CompileSetCorrelationId()
    {
        var dataParam = Expression.Parameter(typeof(IProcessManagerData), "data");
        var guidParam = Expression.Parameter(typeof(Guid), "id");
        var prop = typeof(IProcessManagerData).GetProperty(nameof(IProcessManagerData.CorrelationId))!;
        var assign = Expression.Assign(Expression.Property(dataParam, prop), guidParam);
        return Expression.Lambda<Action<IProcessManagerData, Guid>>(assign, dataParam, guidParam).Compile();
    }

    private static Action<object, IConsumeContext> CompileSetHandlerContext(Type handlerInterface)
    {
        var handlerParam = Expression.Parameter(typeof(object), "handler");
        var ctxParam = Expression.Parameter(typeof(IConsumeContext), "ctx");
        var cast = Expression.Convert(handlerParam, handlerInterface);
        var prop = handlerInterface.GetProperty(nameof(IProcessHandler<IProcessManagerData, Message>.Context))!;
        var assign = Expression.Assign(Expression.Property(cast, prop), ctxParam);
        return Expression.Lambda<Action<object, IConsumeContext>>(assign, handlerParam, ctxParam).Compile();
    }

    private static Action<object, IProcessManagerPropertyMapper> CompileConfigureMapper(Type handlerInterface)
    {
        var handlerParam = Expression.Parameter(typeof(object), "handler");
        var mapperParam = Expression.Parameter(typeof(IProcessManagerPropertyMapper), "mapper");
        var cast = Expression.Convert(handlerParam, handlerInterface);
        var method = handlerInterface.GetMethod(nameof(IProcessHandler<IProcessManagerData, Message>.ConfigureMapper))!;
        var call = Expression.Call(cast, method, mapperParam);
        return Expression.Lambda<Action<object, IProcessManagerPropertyMapper>>(call, handlerParam, mapperParam).Compile();
    }

    private static Func<IProcessManagerFinder, IProcessManagerPropertyMapper, Message, CancellationToken, Task<object?>> CompileFindData(Type dataType)
    {
        var closedFindData = typeof(IProcessManagerFinder).GetMethod(nameof(IProcessManagerFinder.FindDataAsync))!
            .MakeGenericMethod(dataType);

        var finderParam = Expression.Parameter(typeof(IProcessManagerFinder), "finder");
        var mapperParam = Expression.Parameter(typeof(IProcessManagerPropertyMapper), "mapper");
        var messageParam = Expression.Parameter(typeof(Message), "message");
        var ctParam = Expression.Parameter(typeof(CancellationToken), "ct");

        var call = Expression.Call(finderParam, closedFindData, mapperParam, messageParam, ctParam);

        // The call returns Task<IPersistenceData<TData>?>. We bridge to Task<object?> via a generic helper
        // so the lambda's return type matches the delegate signature.
        var helper = typeof(ProcessManagerHandlerRegistry)
            .GetMethod(nameof(ToObjectTask), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(IPersistenceData<>).MakeGenericType(dataType));

        var wrapped = Expression.Call(helper, call);

        return Expression.Lambda<Func<IProcessManagerFinder, IProcessManagerPropertyMapper, Message, CancellationToken, Task<object?>>>(
            wrapped, finderParam, mapperParam, messageParam, ctParam).Compile();
    }

    private static async Task<object?> ToObjectTask<T>(Task<T?> task) where T : class
        => await task.ConfigureAwait(false);

    private static Func<object, object> CompileGetPersistenceDataData(Type persistenceInterfaceType)
    {
        var persistenceParam = Expression.Parameter(typeof(object), "persistence");
        var cast = Expression.Convert(persistenceParam, persistenceInterfaceType);
        var dataProp = persistenceInterfaceType.GetProperty("Data")!;
        var access = Expression.Property(cast, dataProp);
        var toObject = Expression.Convert(access, typeof(object));
        return Expression.Lambda<Func<object, object>>(toObject, persistenceParam).Compile();
    }

    private static Func<IProcessManagerFinder, object, CancellationToken, Task> CompileUpdateData(
        Type dataType, Type persistenceInterfaceType)
    {
        var closedUpdate = typeof(IProcessManagerFinder).GetMethod(nameof(IProcessManagerFinder.UpdateDataAsync))!
            .MakeGenericMethod(dataType);

        var finderParam = Expression.Parameter(typeof(IProcessManagerFinder), "finder");
        var persistenceParam = Expression.Parameter(typeof(object), "persistence");
        var ctParam = Expression.Parameter(typeof(CancellationToken), "ct");

        var cast = Expression.Convert(persistenceParam, persistenceInterfaceType);
        var call = Expression.Call(finderParam, closedUpdate, cast, ctParam);

        return Expression.Lambda<Func<IProcessManagerFinder, object, CancellationToken, Task>>(
            call, finderParam, persistenceParam, ctParam).Compile();
    }

    private static Func<object, Message, object, Task> CompileInvokeHandleAsync(
        Type handlerInterface, Type messageType, Type dataType)
    {
        var handlerParam = Expression.Parameter(typeof(object), "handler");
        var messageParam = Expression.Parameter(typeof(Message), "message");
        var dataParam = Expression.Parameter(typeof(object), "data");

        var handlerCast = Expression.Convert(handlerParam, handlerInterface);
        var messageCast = Expression.Convert(messageParam, messageType);
        var dataCast = Expression.Convert(dataParam, dataType);

        var method = handlerInterface.GetMethod(nameof(IProcessHandler<IProcessManagerData, Message>.HandleAsync))!;
        var call = Expression.Call(handlerCast, method, messageCast, dataCast);

        return Expression.Lambda<Func<object, Message, object, Task>>(
            call, handlerParam, messageParam, dataParam).Compile();
    }
}
```

- [ ] **Step 1.4: Create the registry file.**

### Step 1.5: Build and run the new tests

- [ ] **Step 1.5: Run the registry tests**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~ProcessManagerHandlerRegistryTests" -v normal`
Expected: all 10 tests PASS.

- [ ] **Step 1.6: Run the full unit-test suite** — nothing else should be touched.

Run: `dotnet test src/ServiceConnect.UnitTests -v quiet`
Expected: all tests PASS (count matches pre-change baseline + 10 new).

### Step 1.7: Commit

```bash
git add src/ServiceConnect/Services/Processors/ProcessManagerDescriptor.cs \
        src/ServiceConnect/Services/Processors/ProcessManagerHandlerRegistry.cs \
        src/ServiceConnect.UnitTests/Processors/ProcessManagerHandlerRegistryTests.cs
git commit -m "$(cat <<'EOF'
refactor: add ProcessManagerHandlerRegistry with compiled delegates (R-020)

Introduces a descriptor + registry that precompute per-(messageType,dataType)
invocation delegates via System.Linq.Expressions. Fails fast on duplicate
message-type registrations. ProcessManagerProcessor rewires to it in the
next commit.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 1.7: Commit.**

---

## Task 2: Rewire `ProcessManagerProcessor` to use the registry

Swaps the reflection-heavy processor for a thin orchestrator that depends on the registry. Registers the registry in DI. Adds a registry warm-up inside `Bus.StartConsumingAsync` so misconfiguration fails at startup. Updates the existing `ProcessManagerProcessorTests` to match the new ctor.

**Files:**
- Modify: `src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs`
- Modify: `src/ServiceConnect/ServiceCollectionExtensions.cs`
- Modify: `src/ServiceConnect/Bus.cs`
- Modify: `src/ServiceConnect.UnitTests/Processors/ProcessManagerProcessorTests.cs`

### Step 2.1: Update the existing processor tests (red)

Replace the entire contents of `src/ServiceConnect.UnitTests/Processors/ProcessManagerProcessorTests.cs` with:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class ProcessManagerProcessorTests
{
    private static (ServiceCollection services, Mock<IBus> mockBus, Mock<IProcessManagerFinder> mockFinder) CreateBaseServices()
    {
        var services = new ServiceCollection();
        var mockBus = new Mock<IBus>();
        var mockFinder = new Mock<IProcessManagerFinder>();
        services.AddSingleton(mockBus.Object);
        services.AddSingleton<IProcessManagerFinder>(mockFinder.Object);
        return (services, mockBus, mockFinder);
    }

    private static ProcessManagerHandlerRegistry BuildRegistry(params HandlerReference[] refs)
        => new(refs.ToList(), NullLogger<ProcessManagerHandlerRegistry>.Instance);

    [Fact]
    public async Task ProcessAsync_NullMessage_ReturnsNotHandled()
    {
        var registry = BuildRegistry();
        var provider = new ServiceCollection().BuildServiceProvider();
        var processor = new ProcessManagerProcessor(registry, provider, new Mock<ILogger<ProcessManagerProcessor>>().Object);

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), null,
            new Dictionary<string, object>(), new Envelope());

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_NoDescriptor_ReturnsNotHandled()
    {
        var (services, _, _) = CreateBaseServices();
        var registry = BuildRegistry(); // empty
        var provider = services.BuildServiceProvider();
        var processor = new ProcessManagerProcessor(registry, provider, new Mock<ILogger<ProcessManagerProcessor>>().Object);

        var msg = new PmTestMessage(Guid.NewGuid()) { Content = "x" };
        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), msg,
            new Dictionary<string, object>(), new Envelope());

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_NoFinder_ReturnsNotHandled()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<IBus>().Object);
        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(PmTestMessage), HandlerType = typeof(PmTestHandler)
        });
        // IProcessHandler<PmTestData, PmTestMessage> intentionally not registered? We still need it absent for a
        // cleaner "no finder" check: register the handler so the check passes the "no descriptor" and "no handler"
        // branches, then is stopped by the missing finder.
        services.AddSingleton<IProcessHandler<PmTestData, PmTestMessage>>(new PmTestHandler());
        var provider = services.BuildServiceProvider();
        var processor = new ProcessManagerProcessor(registry, provider, new Mock<ILogger<ProcessManagerProcessor>>().Object);

        var msg = new PmTestMessage(Guid.NewGuid()) { Content = "x" };
        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), msg,
            new Dictionary<string, object>(), new Envelope());

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_NoHandlerInDi_ReturnsNotHandled()
    {
        var (services, _, _) = CreateBaseServices();
        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(PmTestMessage), HandlerType = typeof(PmTestHandler)
        });
        var provider = services.BuildServiceProvider();
        var processor = new ProcessManagerProcessor(registry, provider, new Mock<ILogger<ProcessManagerProcessor>>().Object);

        var msg = new PmTestMessage(Guid.NewGuid()) { Content = "x" };
        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), msg,
            new Dictionary<string, object>(), new Envelope());

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_NewData_InsertsAndReturnsHandled()
    {
        var (services, _, mockFinder) = CreateBaseServices();
        var handler = new PmTestHandler();
        services.AddSingleton<IProcessHandler<PmTestData, PmTestMessage>>(handler);

        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(PmTestMessage), HandlerType = typeof(PmTestHandler)
        });

        mockFinder.Setup(f => f.FindDataAsync<PmTestData>(
            It.IsAny<IProcessManagerPropertyMapper>(), It.IsAny<Message>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IPersistenceData<PmTestData>?)null);

        var provider = services.BuildServiceProvider();
        var processor = new ProcessManagerProcessor(registry, provider, new Mock<ILogger<ProcessManagerProcessor>>().Object);

        var correlationId = Guid.NewGuid();
        var msg = new PmTestMessage(correlationId) { Content = "test" };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), msg,
            new Dictionary<string, object>(), new Envelope());

        Assert.Equal(ProcessResult.Handled, result);
        Assert.True(handler.Invoked);
        mockFinder.Verify(f => f.InsertDataAsync(
            It.Is<IProcessManagerData>(d => d.CorrelationId == correlationId),
            It.IsAny<CancellationToken>()), Times.Once);
        mockFinder.Verify(f => f.UpdateDataAsync(
            It.IsAny<IPersistenceData<PmTestData>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_ExistingData_UpdatesAndReturnsHandled()
    {
        var (services, _, mockFinder) = CreateBaseServices();
        var handler = new PmTestHandler();
        services.AddSingleton<IProcessHandler<PmTestData, PmTestMessage>>(handler);

        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(PmTestMessage), HandlerType = typeof(PmTestHandler)
        });

        var existingData = new PmTestData { CorrelationId = Guid.NewGuid(), Counter = 5 };
        var persistence = new PmTestPersistenceData { Data = existingData };

        mockFinder.Setup(f => f.FindDataAsync<PmTestData>(
            It.IsAny<IProcessManagerPropertyMapper>(), It.IsAny<Message>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(persistence);

        var provider = services.BuildServiceProvider();
        var processor = new ProcessManagerProcessor(registry, provider, new Mock<ILogger<ProcessManagerProcessor>>().Object);

        var msg = new PmTestMessage(existingData.CorrelationId) { Content = "update" };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), msg,
            new Dictionary<string, object>(), new Envelope());

        Assert.Equal(ProcessResult.Handled, result);
        Assert.True(handler.Invoked);
        Assert.Equal(6, existingData.Counter);
        mockFinder.Verify(f => f.UpdateDataAsync(persistence, It.IsAny<CancellationToken>()), Times.Once);
        mockFinder.Verify(f => f.InsertDataAsync(It.IsAny<IProcessManagerData>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_CancelledToken_ThrowsOce()
    {
        var (services, _, _) = CreateBaseServices();
        var registry = BuildRegistry();
        var provider = services.BuildServiceProvider();
        var processor = new ProcessManagerProcessor(registry, provider, new Mock<ILogger<ProcessManagerProcessor>>().Object);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), new PmTestMessage(Guid.NewGuid()),
                new Dictionary<string, object>(), new Envelope(), cts.Token));
    }
}

file class PmTestMessage : Message
{
    public PmTestMessage(Guid correlationId) : base(correlationId) { }
    public string Content { get; set; } = string.Empty;
}

file class PmTestData : IProcessManagerData
{
    public Guid CorrelationId { get; set; }
    public int Counter { get; set; }
}

file class PmTestPersistenceData : IPersistenceData<PmTestData>
{
    public PmTestData Data { get; set; } = new();
}

file class PmTestHandler : IProcessHandler<PmTestData, PmTestMessage>
{
    public bool Invoked { get; private set; }
    public IConsumeContext? Context { get; set; }

    public void ConfigureMapper(IProcessManagerPropertyMapper mapper) { }

    public Task HandleAsync(PmTestMessage message, PmTestData data)
    {
        data.Counter++;
        Invoked = true;
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2.1: Overwrite the existing test file** with the content above.

- [ ] **Step 2.2: Confirm build fails** — `ProcessManagerProcessor` ctor still takes `(IServiceProvider, ILogger)`.

Run: `dotnet build src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj`
Expected: FAIL with CS1729 ("no constructor that takes 3 arguments") or similar.

### Step 2.3: Rewrite the processor

Replace the contents of `src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs` with:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

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

        if (!registry.TryGet(messageType, out var descriptor) || descriptor == null)
            return ProcessResult.NotHandled;

        var finder = serviceProvider.GetService<IProcessManagerFinder>();
        if (finder == null)
        {
            logger.LogWarning(
                "IProcessManagerFinder not registered; cannot process process-manager message {MessageType}",
                messageType.Name);
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
        descriptor.SetHandlerContext(
            handler,
            new ConsumeContext(bus, headers) { CancellationToken = cancellationToken });

        await descriptor.InvokeHandleAsync(handler, (Message)message, data).ConfigureAwait(false);

        if (isNew)
        {
            await finder.InsertDataAsync((IProcessManagerData)data, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await descriptor.UpdateData(finder, persistenceData!, cancellationToken).ConfigureAwait(false);
        }

        return ProcessResult.Handled;
    }
}
```

- [ ] **Step 2.3: Overwrite the processor file.**

### Step 2.4: Register the registry in DI

Modify `src/ServiceConnect/ServiceCollectionExtensions.cs`. Find the "Message processors" block (around line 32-45) and add the registry registration immediately before it:

```csharp
// Process manager descriptor registry (eagerly built, singleton)
services.TryAddSingleton<ProcessManagerHandlerRegistry>();

// Message processors (order matters: ReplyProcessor first, then HandlerProcessor last)
services.TryAddSingleton<ReplyProcessor>();
// ... existing lines unchanged ...
```

- [ ] **Step 2.4: Add `services.TryAddSingleton<ProcessManagerHandlerRegistry>();`** immediately before the `ReplyProcessor` registration (line ~33).

### Step 2.5: Warm up the registry in Bus.StartConsumingAsync

Modify `src/ServiceConnect/Bus.cs`. The current `Bus` ctor takes `IList<HandlerReference>` but not `ProcessManagerHandlerRegistry`. We need the bus to *resolve* the registry at `StartConsumingAsync` time so duplicate-registration errors surface at startup.

Add the registry to the primary constructor:

**Find** (line ~9-19):
```csharp
public sealed class Bus(
    IMessageSerializer serializer,
    IFilterPipeline filterPipeline,
    ISendMessagePipeline sendPipeline,
    IRequestReplyManager requestReplyManager,
    ILogger<Bus> logger,
    IQueueConfiguration queueConfig,
    IMessageDispatcher dispatcher,
    IList<HandlerReference> handlerReferences,
    IConsumer? consumer = null,
    IProducer? producer = null) : IBus
```

**Replace with:**
```csharp
public sealed class Bus(
    IMessageSerializer serializer,
    IFilterPipeline filterPipeline,
    ISendMessagePipeline sendPipeline,
    IRequestReplyManager requestReplyManager,
    ILogger<Bus> logger,
    IQueueConfiguration queueConfig,
    IMessageDispatcher dispatcher,
    IList<HandlerReference> handlerReferences,
    ServiceConnect.Services.Processors.ProcessManagerHandlerRegistry processManagerRegistry,
    IConsumer? consumer = null,
    IProducer? producer = null) : IBus
```

**Add the backing field** near the other `private readonly` fields (right after `_handlerReferences`):
```csharp
private readonly ServiceConnect.Services.Processors.ProcessManagerHandlerRegistry _processManagerRegistry = processManagerRegistry ?? throw new ArgumentNullException(nameof(processManagerRegistry));
```

**Reference the field inside `StartConsumingAsync`** to force eager construction. Find (line ~194-197):

```csharp
            _logger.LogInformation("Bus starting to consume on queue {QueueName} for {Count} message types.",
                _queueConfig.QueueName, messageTypeNames.Count);

            await localConsumer.StartConsumingAsync(_queueConfig.QueueName, messageTypeNames, _dispatcher.Dispatch, cancellationToken).ConfigureAwait(false);
```

Insert a warm-up line immediately above the `LogInformation` call:

```csharp
            _ = _processManagerRegistry; // eagerly resolved by DI; any duplicate-handler error has already thrown
            _logger.LogInformation("Bus starting to consume on queue {QueueName} for {Count} message types.",
                _queueConfig.QueueName, messageTypeNames.Count);
```

The singleton resolution happens when `Bus` itself is resolved by the container, so the assignment above is a guard that documents intent and pins the dependency (also prevents an over-eager trimmer from removing the constructor argument).

- [ ] **Step 2.5: Add registry to Bus ctor, add field, add warm-up line.**

### Step 2.6: Update `BusTests` if it constructs `Bus` directly

Existing `src/ServiceConnect.UnitTests/BusTests.cs` likely constructs `Bus` in tests — the new required ctor argument will break compilation. Find every `new Bus(...)` call and add a trivial registry as the new positional argument.

- [ ] **Step 2.6.a: Search the test project for direct `Bus` construction.**

Run:
```
grep -rn "new Bus(" src/ServiceConnect.UnitTests/
```

- [ ] **Step 2.6.b:** For each hit, add `new ProcessManagerHandlerRegistry(new List<HandlerReference>(), NullLogger<ProcessManagerHandlerRegistry>.Instance)` as the new argument in the correct positional slot (after `handlerReferences`, before `consumer`). Add `using Microsoft.Extensions.Logging.Abstractions;` and `using ServiceConnect.Services.Processors;` to any test file that did not already have them.

Example mechanical change:
```csharp
// Before
new Bus(serializer, pipeline, sendPipeline, rrm, logger, queueCfg, dispatcher, handlerRefs, consumer: fakeConsumer);

// After
new Bus(serializer, pipeline, sendPipeline, rrm, logger, queueCfg, dispatcher, handlerRefs,
    new ProcessManagerHandlerRegistry(new List<HandlerReference>(), NullLogger<ProcessManagerHandlerRegistry>.Instance),
    consumer: fakeConsumer);
```

If the `consumer`/`producer` arguments were named, the change is order-independent. If positional, make sure the new registry slot goes before them.

### Step 2.7: Run unit tests

- [ ] **Step 2.7: Run the full unit test suite.**

Run: `dotnet test src/ServiceConnect.UnitTests -v quiet`
Expected: all tests PASS (including the 7 updated processor tests + all registry tests from Task 1).

### Step 2.8: Run E2E tests

- [ ] **Step 2.8: Run the E2E suite.**

Run: `sg docker -c "dotnet test src/ServiceConnect.EndToEndTests -v quiet"`
Expected: all tests PASS — the process-manager E2E tests exercise the full pipeline against MongoDB-backed persistence and are the regression safety net for the refactor.

### Step 2.9: Commit

```bash
git add src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs \
        src/ServiceConnect/ServiceCollectionExtensions.cs \
        src/ServiceConnect/Bus.cs \
        src/ServiceConnect.UnitTests/Processors/ProcessManagerProcessorTests.cs \
        src/ServiceConnect.UnitTests/BusTests.cs
git commit -m "$(cat <<'EOF'
refactor: rewire ProcessManagerProcessor to use ProcessManagerHandlerRegistry (R-020)

Processor becomes a thin orchestrator; all reflection glue moves to the
registry. DI now registers the singleton registry; Bus.StartConsumingAsync
resolves it eagerly so duplicate-handler misconfiguration fails at startup
rather than mid-traffic.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 2.9: Commit.**

---

## Task 3: Extract `HeaderHelpers` + `MessageRetryHandler`

Pulls the retry/error-publish policy out of `Client.ProcessMessage` into its own class. Extracts the three static helpers (`SetHeader`, `ToNullableHeaders`, `GetErrorMessage`) into `HeaderHelpers` since they're now shared. Client is updated in the same commit to call the new handler.

**Files:**
- Create: `src/ServiceConnect.Client.RabbitMQ/HeaderHelpers.cs`
- Create: `src/ServiceConnect.Client.RabbitMQ/MessageRetryHandler.cs`
- Create: `src/ServiceConnect.UnitTests/MessageRetryHandlerTests.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Client.cs`

### Step 3.1: Create `HeaderHelpers`

Create `src/ServiceConnect.Client.RabbitMQ/HeaderHelpers.cs`:

```csharp
using System.Text;

namespace ServiceConnect.Client.RabbitMQ;

internal static class HeaderHelpers
{
    public static void SetHeader<T>(IDictionary<string, object> headers, string key, T value)
    {
        if (value is null) _ = headers.Remove(key);
        else headers[key] = value;
    }

    public static Dictionary<string, object?> ToNullableHeaders(IDictionary<string, object> headers)
        => headers.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value);

    public static string GetErrorMessage(Exception exception)
    {
        var sb = new StringBuilder();
        sb.AppendLine(exception.Message);
        var ie = exception.InnerException;
        while (ie != null)
        {
            sb.AppendLine(ie.Message);
            ie = ie.InnerException;
        }
        return sb.ToString();
    }
}
```

- [ ] **Step 3.1: Create `HeaderHelpers.cs`.**

### Step 3.2: Write the failing `MessageRetryHandler` tests

Create `src/ServiceConnect.UnitTests/MessageRetryHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MessageRetryHandlerTests
{
    private static BasicDeliverEventArgs MakeArgs(byte[]? body = null)
    {
        var props = new BasicProperties();
        return new BasicDeliverEventArgs(
            consumerTag: "tag",
            deliveryTag: 1,
            redelivered: false,
            exchange: "",
            routingKey: "q",
            properties: props,
            body: body ?? new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task HandleFailureAsync_UnderMaxRetries_IncrementsCountAndPublishesToRetryQueue()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var handler = new MessageRetryHandler(
            maxRetries: 3, errorExchange: "err", NullLogger.Instance);

        var args = MakeArgs();
        var headers = new Dictionary<string, object>();

        await handler.HandleFailureAsync(channel.Object, "q.Retries", args, headers, ex: null);

        Assert.Equal(1, (int)headers[HeaderKeys.RetryCount]);
        channel.Verify(c => c.BasicPublishAsync(
            string.Empty, "q.Retries", false,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleFailureAsync_AtMaxRetries_PublishesToErrorExchange()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var handler = new MessageRetryHandler(maxRetries: 1, errorExchange: "err", NullLogger.Instance);
        var args = MakeArgs();
        var headers = new Dictionary<string, object> { [HeaderKeys.RetryCount] = 1 };

        await handler.HandleFailureAsync(channel.Object, "q.Retries", args, headers, ex: new InvalidOperationException("oops"));

        channel.Verify(c => c.BasicPublishAsync(
            "err", string.Empty, false,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleFailureAsync_AtMaxRetries_IncludesExceptionTypeAndMessage_ButNoStackTrace()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var handler = new MessageRetryHandler(maxRetries: 0, errorExchange: "err", NullLogger.Instance);
        var args = MakeArgs();
        var headers = new Dictionary<string, object>();

        try { throw new InvalidOperationException("boom"); }
        catch (InvalidOperationException caught)
        {
            await handler.HandleFailureAsync(channel.Object, "q.Retries", args, headers, ex: caught);
        }

        Assert.True(headers.ContainsKey(HeaderKeys.Exception));
        var payload = JObject.Parse((string)headers[HeaderKeys.Exception]);
        Assert.Equal(typeof(InvalidOperationException).FullName, (string?)payload["ExceptionType"]);
        Assert.Contains("boom", (string?)payload["Message"] ?? "");
        Assert.Null(payload["StackTrace"]);
    }

    [Fact]
    public async Task HandleFailureAsync_NoException_StillPublishesToErrorExchange()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var handler = new MessageRetryHandler(maxRetries: 0, errorExchange: "err", NullLogger.Instance);
        var args = MakeArgs();
        var headers = new Dictionary<string, object>();

        await handler.HandleFailureAsync(channel.Object, "q.Retries", args, headers, ex: null);

        channel.Verify(c => c.BasicPublishAsync(
            "err", string.Empty, false,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(headers.ContainsKey(HeaderKeys.Exception));
    }

    [Fact]
    public async Task HandleFailureAsync_ReadsExistingRetryCount_FromHeaders()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var handler = new MessageRetryHandler(maxRetries: 5, errorExchange: "err", NullLogger.Instance);
        var args = MakeArgs();
        var headers = new Dictionary<string, object> { [HeaderKeys.RetryCount] = 2 };

        await handler.HandleFailureAsync(channel.Object, "q.Retries", args, headers, ex: null);

        Assert.Equal(3, (int)headers[HeaderKeys.RetryCount]);
    }

    [Fact]
    public async Task HandleFailureAsync_IgnoresCorruptRetryCount()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var handler = new MessageRetryHandler(maxRetries: 5, errorExchange: "err", NullLogger.Instance);
        var args = MakeArgs();
        var headers = new Dictionary<string, object> { [HeaderKeys.RetryCount] = "not-a-number" };

        await handler.HandleFailureAsync(channel.Object, "q.Retries", args, headers, ex: null);

        Assert.Equal(1, (int)headers[HeaderKeys.RetryCount]);
    }
}
```

- [ ] **Step 3.2: Create the failing test file.**

- [ ] **Step 3.3: Confirm build fails** — `MessageRetryHandler` does not yet exist.

Run: `dotnet build src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj`
Expected: FAIL with CS0246 `MessageRetryHandler`.

### Step 3.4: Create `MessageRetryHandler`

Create `src/ServiceConnect.Client.RabbitMQ/MessageRetryHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Failure-path policy for a RabbitMQ client. Given a failed delivery, either re-publishes
/// the message to the per-queue ".Retries" queue (incrementing the retry counter) or,
/// once max retries are exhausted, publishes to the configured error exchange with
/// redacted exception info in the header.
/// </summary>
internal sealed class MessageRetryHandler
{
    private readonly int _maxRetries;
    private readonly string _errorExchange;
    private readonly ILogger _logger;

    public MessageRetryHandler(int maxRetries, string errorExchange, ILogger logger)
    {
        _maxRetries = maxRetries;
        _errorExchange = errorExchange ?? throw new ArgumentNullException(nameof(errorExchange));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task HandleFailureAsync(
        IChannel channel, string retryQueueName,
        BasicDeliverEventArgs args, Dictionary<string, object> headers, Exception? ex)
    {
        int retryCount = 0;
        if (headers.TryGetValue(HeaderKeys.RetryCount, out var raw)
            && int.TryParse(raw?.ToString(), out int parsed)
            && parsed >= 0 && parsed <= _maxRetries + 1)
        {
            retryCount = parsed;
        }

        if (retryCount < _maxRetries)
        {
            retryCount++;
            HeaderHelpers.SetHeader(headers, HeaderKeys.RetryCount, retryCount);
            var props = new BasicProperties(args.BasicProperties) { Headers = HeaderHelpers.ToNullableHeaders(headers) };
            await channel.BasicPublishAsync(string.Empty, retryQueueName, mandatory: false, props, args.Body).ConfigureAwait(false);
            return;
        }

        if (ex != null)
        {
            // Only include type + message in headers. No stack traces or internal details
            // that could leak sensitive information to error-queue consumers. Full diagnostics
            // are logged server-side below.
            HeaderHelpers.SetHeader(headers, HeaderKeys.Exception, JsonConvert.SerializeObject(new
            {
                TimeStamp = DateTime.UtcNow,
                ExceptionType = ex.GetType().FullName,
                Message = HeaderHelpers.GetErrorMessage(ex)
            }));

            _logger.LogError(ex, "Max retries exceeded for MessageId {MessageId}", args.BasicProperties.MessageId);
        }

        _logger.LogError("Max number of retries exceeded. MessageId: {MessageId}", args.BasicProperties.MessageId);
        var errorProps = new BasicProperties(args.BasicProperties) { Headers = HeaderHelpers.ToNullableHeaders(headers) };
        await channel.BasicPublishAsync(_errorExchange, string.Empty, mandatory: false, errorProps, args.Body).ConfigureAwait(false);
    }
}
```

- [ ] **Step 3.4: Create the file.**

### Step 3.5: Wire `Client` to use the handler

Modify `src/ServiceConnect.Client.RabbitMQ/Client.cs`:

1. **Add a field** near the other private readonly fields (after `_logger`):
   ```csharp
   private readonly MessageRetryHandler _retryHandler;
   ```

2. **Initialize the field in the ctor**, after existing initialization:
   ```csharp
   _retryHandler = new MessageRetryHandler(_maxRetries, queueConfiguration.ErrorQueueName, logger);
   ```
   (The existing `_errorExchange` field is set inside `StartConsumingAsync`, but the handler needs the exchange name at ctor time — the exchange name is `queueConfiguration.ErrorQueueName`, which is stable.)

3. **Replace the failure branch inside `ProcessMessage`** (currently lines ~144-184). Find:
   ```csharp
        if (!result.Success)
        {
            int retryCount = 0;

            if (headers.TryGetValue(HeaderKeys.RetryCount, out var retryCountVal)
                ...
                await _model!.BasicPublishAsync(_errorExchange, string.Empty, mandatory: false, errorProps, args.Body).ConfigureAwait(false);
            }
        }
   ```
   Replace the entire `if (!result.Success) { ... }` block with:
   ```csharp
        if (!result.Success)
        {
            await _retryHandler.HandleFailureAsync(_model!, _retryQueueName, args, headers, result.Exception).ConfigureAwait(false);
        }
   ```

4. **Remove the now-unused static helpers** (`SetHeader`, `ToNullableHeaders`, `GetErrorMessage`) from `Client.cs` — they live in `HeaderHelpers` now. Any remaining `SetHeader(...)` calls inside `Client.cs` (e.g., in `ProcessMessage` — `SetHeader(headers, HeaderKeys.TimeReceived, ...)`, etc.) must be rewritten to `HeaderHelpers.SetHeader(...)`.

- [ ] **Step 3.5.a: Add `MessageRetryHandler` field and ctor init.**
- [ ] **Step 3.5.b: Replace the `if (!result.Success)` block with the single-line delegate.**
- [ ] **Step 3.5.c: Delete the static `SetHeader`/`ToNullableHeaders`/`GetErrorMessage` methods from `Client.cs`.**
- [ ] **Step 3.5.d: Rewrite remaining `SetHeader(...)` call sites inside `Client.cs` to `HeaderHelpers.SetHeader(...)`. After the failure branch has moved to `MessageRetryHandler`, five call sites remain in `Client.ProcessMessage`: `Redelivered`, `TimeReceived`, `DestinationMachine`, `DestinationAddress`, `TimeProcessed`.**

Also remove the now-unused `using Newtonsoft.Json;` and `using System.Text;` from `Client.cs` if they become orphaned.

### Step 3.6: Build + run tests

- [ ] **Step 3.6.a: Build the transport project** — `dotnet build src/ServiceConnect.Client.RabbitMQ`. Expected PASS.

- [ ] **Step 3.6.b: Build the test project** — `dotnet build src/ServiceConnect.UnitTests`. Expected PASS.

- [ ] **Step 3.6.c: Run the retry-handler tests** — `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~MessageRetryHandlerTests" -v normal`. Expected: 6/6 PASS.

- [ ] **Step 3.6.d: Run the full unit suite** — `dotnet test src/ServiceConnect.UnitTests -v quiet`. Expected: all PASS.

- [ ] **Step 3.6.e: Run the E2E suite** — `sg docker -c "dotnet test src/ServiceConnect.EndToEndTests -v quiet"`. Expected: all PASS. `RetryAndErrorQueueTests` is the key coverage for this change.

### Step 3.7: Commit

```bash
git add src/ServiceConnect.Client.RabbitMQ/HeaderHelpers.cs \
        src/ServiceConnect.Client.RabbitMQ/MessageRetryHandler.cs \
        src/ServiceConnect.Client.RabbitMQ/Client.cs \
        src/ServiceConnect.UnitTests/MessageRetryHandlerTests.cs
git commit -m "$(cat <<'EOF'
refactor: extract MessageRetryHandler + HeaderHelpers from Client (R-021)

Failure-path policy (retry counter, retry-queue publish, error-exchange
publish on exhaustion) moves to MessageRetryHandler. Shared header
utilities move to HeaderHelpers. Client ProcessMessage delegates the
failure branch to the new class in a single call. Behaviour unchanged:
exception header still excludes stack traces.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 3.7: Commit.**

---

## Task 4: Extract `MessageAuditPublisher`

Mirrors Task 3 shape for the success path.

**Files:**
- Create: `src/ServiceConnect.Client.RabbitMQ/MessageAuditPublisher.cs`
- Create: `src/ServiceConnect.UnitTests/MessageAuditPublisherTests.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Client.cs`

### Step 4.1: Write failing tests

Create `src/ServiceConnect.UnitTests/MessageAuditPublisherTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MessageAuditPublisherTests
{
    private static BasicDeliverEventArgs MakeArgs()
    {
        var props = new BasicProperties();
        return new BasicDeliverEventArgs("tag", 1, false, "", "q", props, new byte[] { 1, 2, 3 });
    }

    private static Mock<IQueueConfiguration> MakeQueueCfg(bool auditingEnabled)
    {
        var cfg = new Mock<IQueueConfiguration>();
        cfg.SetupGet(c => c.AuditingEnabled).Returns(auditingEnabled);
        return cfg;
    }

    [Fact]
    public async Task PublishAuditIfEnabledAsync_Publishes_WhenAuditingEnabled()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var publisher = new MessageAuditPublisher("audit", MakeQueueCfg(true).Object, NullLogger.Instance);
        var headers = new Dictionary<string, object> { [HeaderKeys.MessageType] = "SomeMessage" };

        await publisher.PublishAuditIfEnabledAsync(channel.Object, MakeArgs(), headers);

        channel.Verify(c => c.BasicPublishAsync(
            "audit", string.Empty, false,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishAuditIfEnabledAsync_Skips_WhenAuditingDisabled()
    {
        var channel = new Mock<IChannel>();
        var publisher = new MessageAuditPublisher("audit", MakeQueueCfg(false).Object, NullLogger.Instance);

        await publisher.PublishAuditIfEnabledAsync(channel.Object, MakeArgs(), new Dictionary<string, object>());

        channel.Verify(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PublishAuditIfEnabledAsync_Skips_ForByteStreamMessageType()
    {
        var channel = new Mock<IChannel>();
        var publisher = new MessageAuditPublisher("audit", MakeQueueCfg(true).Object, NullLogger.Instance);
        var headers = new Dictionary<string, object> { [HeaderKeys.MessageType] = HeaderKeys.ByteStream };

        await publisher.PublishAuditIfEnabledAsync(channel.Object, MakeArgs(), headers);

        channel.Verify(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
```

- [ ] **Step 4.1: Create the failing test file.**

- [ ] **Step 4.2: Build fails** — `MessageAuditPublisher` does not exist.

Run: `dotnet build src/ServiceConnect.UnitTests`
Expected: FAIL `CS0246 MessageAuditPublisher`.

### Step 4.3: Create `MessageAuditPublisher`

Create `src/ServiceConnect.Client.RabbitMQ/MessageAuditPublisher.cs`:

```csharp
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Success-path policy for a RabbitMQ client. Publishes a copy of a successfully
/// processed message to the audit exchange when auditing is enabled. Skips byte-stream
/// messages to avoid auditing raw stream frames.
/// </summary>
internal sealed class MessageAuditPublisher
{
    private readonly string _auditExchange;
    private readonly IQueueConfiguration _queueConfiguration;
    private readonly ILogger _logger;

    public MessageAuditPublisher(string auditExchange, IQueueConfiguration queueConfiguration, ILogger logger)
    {
        _auditExchange = auditExchange ?? throw new ArgumentNullException(nameof(auditExchange));
        _queueConfiguration = queueConfiguration ?? throw new ArgumentNullException(nameof(queueConfiguration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task PublishAuditIfEnabledAsync(IChannel channel, BasicDeliverEventArgs args, Dictionary<string, object> headers)
    {
        if (!_queueConfiguration.AuditingEnabled)
            return;

        string? messageType = null;
        if (headers.TryGetValue(HeaderKeys.MessageType, out var raw))
            messageType = HeaderDecoder.Decode(raw);

        if (messageType == HeaderKeys.ByteStream)
            return;

        var props = new BasicProperties(args.BasicProperties) { Headers = HeaderHelpers.ToNullableHeaders(headers) };
        await channel.BasicPublishAsync(_auditExchange, string.Empty, mandatory: false, props, args.Body).ConfigureAwait(false);
    }
}
```

- [ ] **Step 4.3: Create the file.**

### Step 4.4: Wire `Client` to use the publisher

Modify `src/ServiceConnect.Client.RabbitMQ/Client.cs`:

1. **Add a field** below `_retryHandler`:
   ```csharp
   private readonly MessageAuditPublisher _auditPublisher;
   ```
2. **Initialize in ctor:**
   ```csharp
   _auditPublisher = new MessageAuditPublisher(queueConfiguration.AuditQueueName, queueConfiguration, logger);
   ```
3. **Replace the success-path branch inside `ProcessMessage`** — currently (lines ~186-199):
   ```csharp
        else if (!_errorsDisabled)
        {
            string? messageType = null;
            if (headers.TryGetValue(HeaderKeys.MessageType, out var mtRaw))
            {
                messageType = HeaderDecoder.Decode(mtRaw);
            }

            if (_queueConfiguration.AuditingEnabled && messageType != HeaderKeys.ByteStream)
            {
                var auditProps = new BasicProperties(args.BasicProperties) { Headers = ToNullableHeaders(headers) };
                await _model!.BasicPublishAsync(_auditExchange, string.Empty, mandatory: false, auditProps, args.Body).ConfigureAwait(false);
            }
        }
   ```
   Replace with:
   ```csharp
        else if (!_errorsDisabled)
        {
            await _auditPublisher.PublishAuditIfEnabledAsync(_model!, args, headers).ConfigureAwait(false);
        }
   ```
4. **Remove the now-unused `_auditExchange` field** and the line `_auditExchange = _queueConfiguration.AuditQueueName;` inside `StartConsumingAsync` — the publisher owns the exchange name now.

- [ ] **Step 4.4.a: Add field, init in ctor.**
- [ ] **Step 4.4.b: Replace the audit branch with the one-line delegate.**
- [ ] **Step 4.4.c: Delete `_auditExchange` field + its assignment in `StartConsumingAsync`.**

### Step 4.5: Run tests

- [ ] **Step 4.5.a:** `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~MessageAuditPublisherTests" -v normal` → 3/3 PASS.
- [ ] **Step 4.5.b:** `dotnet test src/ServiceConnect.UnitTests -v quiet` → all PASS.
- [ ] **Step 4.5.c:** `sg docker -c "dotnet test src/ServiceConnect.EndToEndTests -v quiet"` → all PASS. `AuditingTests` is the relevant E2E coverage.

### Step 4.6: Commit

```bash
git add src/ServiceConnect.Client.RabbitMQ/MessageAuditPublisher.cs \
        src/ServiceConnect.Client.RabbitMQ/Client.cs \
        src/ServiceConnect.UnitTests/MessageAuditPublisherTests.cs
git commit -m "$(cat <<'EOF'
refactor: extract MessageAuditPublisher from Client (R-021)

Success-path audit publish moves into its own class; Client delegates
the audit branch to it in a single call. Behaviour unchanged: auditing
still respects queueConfiguration.AuditingEnabled and skips byte-stream
messages.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 4.6: Commit.**

---

## Task 5: Extract `RabbitMqConsumerHost` and slim `Client` to a facade

Moves the remaining lifecycle/ack-nack logic out of `Client` into `RabbitMqConsumerHost`. Client keeps its public surface (same ctor signature, same methods) but forwards to the host.

**Files:**
- Create: `src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs`
- Create: `src/ServiceConnect.UnitTests/RabbitMqConsumerHostTests.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/Client.cs` (shrinks to ~40 lines).

### Step 5.1: Write failing `RabbitMqConsumerHost` tests

Create `src/ServiceConnect.UnitTests/RabbitMqConsumerHostTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

public class RabbitMqConsumerHostTests
{
    private static (Mock<IServiceConnectConnection>, Mock<IChannel>) MockConnection()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.IsOpen).Returns(true);
        channel.Setup(c => c.BasicQosAsync(It.IsAny<uint>(), It.IsAny<ushort>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // Production code uses the short overload: BasicConsumeAsync(string, bool, IAsyncBasicConsumer, ...).
        // If that signature has evolved or Moq complains, widen or narrow these parameter types.
        channel.Setup(c => c.BasicConsumeAsync(
            It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<IAsyncBasicConsumer>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("tag");
        channel.Setup(c => c.QueueBindAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IDictionary<string, object?>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        channel.Setup(c => c.QueueDeleteAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0u);
        channel.Setup(c => c.CloseAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var conn = new Mock<IServiceConnectConnection>();
        conn.Setup(c => c.CreateChannelAsync()).ReturnsAsync(channel.Object);
        return (conn, channel);
    }

    private static Mock<ITransportConfiguration> MakeTransportCfg(ushort prefetch = 10, bool autoDelete = false, bool disablePrefetch = false)
    {
        var cfg = new Mock<ITransportConfiguration>();
        cfg.SetupGet(c => c.MaxRetries).Returns(3);
        cfg.SetupGet(c => c.PrefetchCount).Returns(prefetch);
        var settings = new Dictionary<string, object>();
        if (autoDelete) settings[RabbitMQSettingKeys.AutoDelete] = true;
        if (disablePrefetch) settings[RabbitMQSettingKeys.DisablePrefetch] = true;
        cfg.SetupGet(c => c.ClientSettings).Returns(settings);
        return cfg;
    }

    private static Mock<IQueueConfiguration> MakeQueueCfg()
    {
        var cfg = new Mock<IQueueConfiguration>();
        cfg.SetupGet(c => c.QueueName).Returns("q");
        cfg.SetupGet(c => c.ErrorQueueName).Returns("err");
        cfg.SetupGet(c => c.AuditQueueName).Returns("audit");
        cfg.SetupGet(c => c.DisableErrors).Returns(false);
        cfg.SetupGet(c => c.AuditingEnabled).Returns(false);
        return cfg;
    }

    [Fact]
    public async Task StartConsumingAsync_SetsBasicQos_WhenPrefetchEnabled()
    {
        var (conn, channel) = MockConnection();
        var tcfg = MakeTransportCfg(prefetch: 7);
        var qcfg = MakeQueueCfg();
        var retry = new MessageRetryHandler(3, "err", NullLogger.Instance);
        var audit = new MessageAuditPublisher("audit", qcfg.Object, NullLogger.Instance);

        var host = new RabbitMqConsumerHost(conn.Object, tcfg.Object, qcfg.Object, retry, audit, NullLogger.Instance);

        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");

        channel.Verify(c => c.BasicQosAsync(0, 7, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartConsumingAsync_SkipsBasicQos_WhenPrefetchDisabled()
    {
        var (conn, channel) = MockConnection();
        var tcfg = MakeTransportCfg(disablePrefetch: true);
        var qcfg = MakeQueueCfg();
        var retry = new MessageRetryHandler(3, "err", NullLogger.Instance);
        var audit = new MessageAuditPublisher("audit", qcfg.Object, NullLogger.Instance);

        var host = new RabbitMqConsumerHost(conn.Object, tcfg.Object, qcfg.Object, retry, audit, NullLogger.Instance);

        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");

        channel.Verify(c => c.BasicQosAsync(
            It.IsAny<uint>(), It.IsAny<ushort>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ConsumeMessageTypeAsync_BindsQueueToExchange()
    {
        var (conn, channel) = MockConnection();
        var tcfg = MakeTransportCfg();
        var qcfg = MakeQueueCfg();
        var retry = new MessageRetryHandler(3, "err", NullLogger.Instance);
        var audit = new MessageAuditPublisher("audit", qcfg.Object, NullLogger.Instance);

        var host = new RabbitMqConsumerHost(conn.Object, tcfg.Object, qcfg.Object, retry, audit, NullLogger.Instance);

        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");
        await host.ConsumeMessageTypeAsync("SomeMsg");

        channel.Verify(c => c.QueueBindAsync("q", "SomeMsg", string.Empty,
            It.IsAny<IDictionary<string, object?>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_DeletesRetryQueue_WhenAutoDelete()
    {
        var (conn, channel) = MockConnection();
        var tcfg = MakeTransportCfg(autoDelete: true);
        var qcfg = MakeQueueCfg();
        var retry = new MessageRetryHandler(3, "err", NullLogger.Instance);
        var audit = new MessageAuditPublisher("audit", qcfg.Object, NullLogger.Instance);

        var host = new RabbitMqConsumerHost(conn.Object, tcfg.Object, qcfg.Object, retry, audit, NullLogger.Instance);
        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");

        await host.DisposeAsync();

        channel.Verify(c => c.QueueDeleteAsync("q.Retries", false, false, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_SkipsRetryQueueDelete_WhenAutoDeleteFalse()
    {
        var (conn, channel) = MockConnection();
        var tcfg = MakeTransportCfg(autoDelete: false);
        var qcfg = MakeQueueCfg();
        var retry = new MessageRetryHandler(3, "err", NullLogger.Instance);
        var audit = new MessageAuditPublisher("audit", qcfg.Object, NullLogger.Instance);

        var host = new RabbitMqConsumerHost(conn.Object, tcfg.Object, qcfg.Object, retry, audit, NullLogger.Instance);
        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");

        await host.DisposeAsync();

        channel.Verify(c => c.QueueDeleteAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DisposeAsync_SwallowsObjectDisposedException_OnQueueDelete()
    {
        var (conn, channel) = MockConnection();
        channel.Setup(c => c.QueueDeleteAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ObjectDisposedException("channel"));
        var tcfg = MakeTransportCfg(autoDelete: true);
        var qcfg = MakeQueueCfg();
        var retry = new MessageRetryHandler(3, "err", NullLogger.Instance);
        var audit = new MessageAuditPublisher("audit", qcfg.Object, NullLogger.Instance);

        var host = new RabbitMqConsumerHost(conn.Object, tcfg.Object, qcfg.Object, retry, audit, NullLogger.Instance);
        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");

        var thrown = await Record.ExceptionAsync(() => host.DisposeAsync().AsTask());
        Assert.Null(thrown);
    }
}
```

- [ ] **Step 5.1: Create the failing test file.**

> **Note:** The `BasicConsumeAsync` overload used here matches the RabbitMQ.Client 7.x signature. If Moq complains about ambiguous overloads, narrow the mock setup by specifying the exact parameter types — RabbitMQ.Client exposes multiple overloads.
>
> The `DisposeAsync_DrainsInflightMessages_BeforeClose` test from the spec is intentionally omitted: exercising the drain loop requires deterministic control over the private `_messagesBeingProcessed` counter, which would force either an `InternalsVisibleTo` seam or test-only hooks. The drain behaviour is covered by the E2E suite (`BusLifecycleTests`, `DisposeAsync` scenarios). Add an `InternalsVisibleTo` seam in a later task if the E2E coverage proves insufficient.

- [ ] **Step 5.2: Build** — expected to FAIL because `RabbitMqConsumerHost` does not exist.

Run: `dotnet build src/ServiceConnect.UnitTests`
Expected: FAIL with CS0246 `RabbitMqConsumerHost`.

### Step 5.3: Create `RabbitMqConsumerHost`

Create `src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs`:

```csharp
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Owns the RabbitMQ channel, consumer, and ack/nack lifecycle for a single queue.
/// Delegates failure-path policy to <see cref="MessageRetryHandler"/> and success-path
/// audit publish to <see cref="MessageAuditPublisher"/>.
/// </summary>
internal sealed class RabbitMqConsumerHost : IAsyncDisposable
{
    private readonly IServiceConnectConnection _connection;
    private readonly IQueueConfiguration _queueConfiguration;
    private readonly MessageRetryHandler _retryHandler;
    private readonly MessageAuditPublisher _auditPublisher;
    private readonly ILogger _logger;

    private readonly bool _errorsDisabled;
    private readonly ushort _prefetchCount;
    private readonly bool _disablePrefetch;
    private readonly IDictionary<string, object?> _queueArguments;

    private IChannel? _model;
    private ConsumerEventHandler? _consumerEventHandler;
    private AsyncEventingBasicConsumer? _consumer;
    private CancellationToken _consumingCt;
    private bool _autoDelete;
    private string _queueName = "";
    private string _retryQueueName = "";
    private int _messagesBeingProcessed;

    public RabbitMqConsumerHost(
        IServiceConnectConnection connection,
        ITransportConfiguration transportConfiguration,
        IQueueConfiguration queueConfiguration,
        MessageRetryHandler retryHandler,
        MessageAuditPublisher auditPublisher,
        ILogger logger)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _queueConfiguration = queueConfiguration ?? throw new ArgumentNullException(nameof(queueConfiguration));
        _retryHandler = retryHandler ?? throw new ArgumentNullException(nameof(retryHandler));
        _auditPublisher = auditPublisher ?? throw new ArgumentNullException(nameof(auditPublisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var settings = transportConfiguration.ClientSettings;
        _errorsDisabled = queueConfiguration.DisableErrors;
        _autoDelete = settings.TryGetValue(RabbitMQSettingKeys.AutoDelete, out var autoDeleteVal) && (bool)autoDeleteVal;
        _prefetchCount = settings.TryGetValue(RabbitMQSettingKeys.PrefetchCount, out var prefetchVal)
            ? Convert.ToUInt16((int)prefetchVal)
            : transportConfiguration.PrefetchCount;
        _disablePrefetch = settings.TryGetValue(RabbitMQSettingKeys.DisablePrefetch, out var disablePrefetchVal) && (bool)disablePrefetchVal;
        _queueArguments = settings.TryGetValue(RabbitMQSettingKeys.Arguments, out var argsVal)
            ? (IDictionary<string, object?>)argsVal
            : new Dictionary<string, object?>();
    }

    public async Task StartConsumingAsync(
        ConsumerEventHandler messageReceived, string queueName,
        bool? exclusive = null, bool? autoDelete = null, CancellationToken cancellationToken = default)
    {
        _consumerEventHandler = messageReceived;
        _consumingCt = cancellationToken;
        _queueName = queueName;
        _retryQueueName = queueName + ".Retries";

        if (autoDelete.HasValue) _autoDelete = autoDelete.Value;

        _model = await _connection.CreateChannelAsync().ConfigureAwait(false);
        if (!_disablePrefetch)
            await _model.BasicQosAsync(0, _prefetchCount, false).ConfigureAwait(false);

        _consumer = new AsyncEventingBasicConsumer(_model);
        _consumer.ReceivedAsync += EventAsync;

        var consumerTag = await _model.BasicConsumeAsync(_queueName, false, _consumer).ConfigureAwait(false);
        _logger.LogDebug("Started consuming on {QueueName}, tag={ConsumerTag}", _queueName, consumerTag);
    }

    public async Task ConsumeMessageTypeAsync(string messageTypeName)
    {
        await _model!.QueueBindAsync(_queueName, messageTypeName, string.Empty, _queueArguments).ConfigureAwait(false);
    }

    public async Task EventAsync(object consumer, BasicDeliverEventArgs args)
    {
        bool processed = false;
        try
        {
            Interlocked.Increment(ref _messagesBeingProcessed);

            if (args.BasicProperties.Headers == null ||
                (!args.BasicProperties.Headers.ContainsKey(HeaderKeys.TypeName) &&
                 !args.BasicProperties.Headers.ContainsKey(HeaderKeys.FullTypeName)))
            {
                _logger.LogError("Error processing message, Message headers must contain type name.");
                processed = true; // no retry possible for malformed messages, ack to discard
                return;
            }

            await ProcessMessageAsync(args).ConfigureAwait(false);
            processed = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message");
        }
        finally
        {
            try
            {
                if (processed)
                    await _model!.BasicAckAsync(args.DeliveryTag, false).ConfigureAwait(false);
                else
                    await _model!.BasicNackAsync(args.DeliveryTag, false, true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error acking/nacking the message");
            }

            Interlocked.Decrement(ref _messagesBeingProcessed);
        }
    }

    private async Task ProcessMessageAsync(BasicDeliverEventArgs args)
    {
        ConsumeEventResult result;
        var headers = new Dictionary<string, object>();
        if (args.BasicProperties.Headers != null)
        {
            foreach (var kvp in args.BasicProperties.Headers)
            {
                if (kvp.Value is not null) headers[kvp.Key] = kvp.Value;
            }
        }

        if (args.Redelivered)
            HeaderHelpers.SetHeader(headers, HeaderKeys.Redelivered, true);

        try
        {
            HeaderHelpers.SetHeader(headers, HeaderKeys.TimeReceived, DateTime.UtcNow.ToString("O"));
            HeaderHelpers.SetHeader(headers, HeaderKeys.DestinationMachine, Environment.MachineName);
            HeaderHelpers.SetHeader(headers, HeaderKeys.DestinationAddress, _queueConfiguration.QueueName);

            var typeNameRaw = headers.ContainsKey(HeaderKeys.FullTypeName) ? headers[HeaderKeys.FullTypeName] : headers[HeaderKeys.TypeName];
            string typeName = HeaderDecoder.Decode(typeNameRaw) ?? "";

            if (_consumerEventHandler == null)
            {
                _logger.LogError("Consumer event handler not set — message will be nacked for redelivery. Queue: {Queue}", _queueConfiguration.QueueName);
                result = new ConsumeEventResult { Success = false };
            }
            else
            {
                result = await _consumerEventHandler(args.Body.ToArray(), typeName, headers, _consumingCt).ConfigureAwait(false);
            }

            HeaderHelpers.SetHeader(headers, HeaderKeys.TimeProcessed, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            result = new ConsumeEventResult { Exception = ex, Success = false };
        }

        if (!result.Success)
        {
            await _retryHandler.HandleFailureAsync(_model!, _retryQueueName, args, headers, result.Exception).ConfigureAwait(false);
        }
        else if (!_errorsDisabled)
        {
            await _auditPublisher.PublishAuditIfEnabledAsync(_model!, args, headers).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        var deadline = Environment.TickCount64 + 5000;
        while (Volatile.Read(ref _messagesBeingProcessed) > 0 && Environment.TickCount64 < deadline)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        if (_autoDelete && _model != null)
        {
            try
            {
                _logger.LogDebug("Deleting retry queue");
                await _model.QueueDeleteAsync(_queueName + ".Retries").ConfigureAwait(false);
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error deleting retry queue");
            }
        }

        await CloseChannelAsync().ConfigureAwait(false);
    }

    private async Task CloseChannelAsync()
    {
        if (_model == null) return;
        try
        {
            if (_model.IsOpen) await _model.CloseAsync().ConfigureAwait(false);
            _model.Dispose();
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error closing channel during dispose");
        }
        _model = null;
    }
}
```

- [ ] **Step 5.3: Create the host file.**

### Step 5.4: Shrink `Client` to a facade

Replace the entire contents of `src/ServiceConnect.Client.RabbitMQ/Client.cs` with:

```csharp
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Facade over <see cref="RabbitMqConsumerHost"/>. Public shape retained so existing
/// transport-factory code keeps compiling without changes.
/// </summary>
public sealed class Client : IAsyncDisposable
{
    private readonly RabbitMqConsumerHost _host;

    public Client(
        IServiceConnectConnection connection,
        ITransportConfiguration transportConfiguration,
        IQueueConfiguration queueConfiguration,
        ILogger logger)
    {
        var retryHandler = new MessageRetryHandler(
            transportConfiguration.MaxRetries, queueConfiguration.ErrorQueueName, logger);
        var auditPublisher = new MessageAuditPublisher(
            queueConfiguration.AuditQueueName, queueConfiguration, logger);
        _host = new RabbitMqConsumerHost(
            connection, transportConfiguration, queueConfiguration,
            retryHandler, auditPublisher, logger);
    }

    public Task StartConsumingAsync(
        ConsumerEventHandler messageReceived, string queueName,
        bool? exclusive = null, bool? autoDelete = null, CancellationToken cancellationToken = default)
        => _host.StartConsumingAsync(messageReceived, queueName, exclusive, autoDelete, cancellationToken);

    public Task ConsumeMessageTypeAsync(string messageTypeName) => _host.ConsumeMessageTypeAsync(messageTypeName);

    public ValueTask DisposeAsync() => _host.DisposeAsync();
}
```

- [ ] **Step 5.4: Overwrite `Client.cs` with the facade.**

### Step 5.5: Verify

- [ ] **Step 5.5.a: Build** — `dotnet build src/ServiceConnect.sln`. Expected: PASS.

- [ ] **Step 5.5.b: Run host tests** — `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~RabbitMqConsumerHostTests" -v normal`. Expected: 6/6 PASS.

- [ ] **Step 5.5.c: Run the full unit suite** — `dotnet test src/ServiceConnect.UnitTests -v quiet`. Expected: all PASS.

- [ ] **Step 5.5.d: Run E2E** — `sg docker -c "dotnet test src/ServiceConnect.EndToEndTests -v quiet"`. Expected: all PASS. This is the critical integration check for the full Client split.

### Step 5.6: Commit

```bash
git add src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs \
        src/ServiceConnect.Client.RabbitMQ/Client.cs \
        src/ServiceConnect.UnitTests/RabbitMqConsumerHostTests.cs
git commit -m "$(cat <<'EOF'
refactor: extract RabbitMqConsumerHost and reduce Client to a facade (R-021)

RabbitMQ channel lifecycle, consumer event dispatch, and ack/nack
orchestration move to RabbitMqConsumerHost. Client retains its public
ctor + method signatures and forwards every call to the host. The three
collaborators (host, retry handler, audit publisher) are now the unit of
behaviour; Client becomes wiring.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 5.6: Commit.**

---

## Task 6: Update `docs/remaining-issues.md`

Final bookkeeping — mark R-020/R-021 as done and update the header to record the completed group.

**Files:**
- Modify: `docs/remaining-issues.md`

### Step 6.1: Update the doc

Edit `docs/remaining-issues.md`:

1. **Header (line 3)** — append to the existing sentence about completed groups (currently notes Group C-2 completion). Add:
   > R-020/R-021 (Client + ProcessManagerProcessor SRP refactor) completed in Group C-3 on 2026-04-13.

2. **R-020/R-021 row** — change the `Scope` cell from `Large — internal structure refactor` to:
   > **Done** (Group C-3) — Client split into `RabbitMqConsumerHost` + `MessageRetryHandler` + `MessageAuditPublisher`; `ProcessManagerProcessor` thinned via `ProcessManagerHandlerRegistry` with compiled-expression delegates.

- [ ] **Step 6.1: Make both edits.**

### Step 6.2: Commit

```bash
git add docs/remaining-issues.md
git commit -m "$(cat <<'EOF'
docs: mark R-020/R-021 done (Client + ProcessManagerProcessor SRP refactor)

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 6.2: Commit.**

### Step 6.3: Final verification

- [ ] **Step 6.3.a: `git log --oneline | head -10`** — confirm 6 new commits.

- [ ] **Step 6.3.b: `dotnet test src/ServiceConnect.UnitTests -v quiet`** — everything green.

- [ ] **Step 6.3.c: `sg docker -c "dotnet test src/ServiceConnect.EndToEndTests -v quiet"`** — everything green.

---

## Notes & Caveats

- **RabbitMQ.Client 7.x overload surface:** When mocking `IChannel`, method overloads with `CreateChannelOptions` or `IAsyncBasicConsumer` may force ambiguity errors with Moq. If this bites, narrow the setups to the specific overload by declaring the parameter types explicitly. The test skeletons above target the common overloads used by the production code.
- **`BasicProperties` immutability:** The production code constructs `new BasicProperties(args.BasicProperties)` to get a writable clone — this pattern moves intact into the new collaborators.
- **`IProcessHandler<,>.ConfigureMapper` as a default interface method:** `Expression.Call` on a default interface method has worked since .NET 7. If a runtime throws because the interface type is not virtually dispatchable, fall back to `MethodInfo.CreateDelegate` on the closed method info. Verify before switching — the existing reflection-based code already invokes this method successfully via `MethodInfo.Invoke`, and `Expression.Call` on the same `MethodInfo` behaves identically at runtime.
- **Binary compat:** `Client` remains a `public sealed class` with the same ctor signature + the same public methods. No consumers need rebuilding.
- **Warm-up in `Bus`:** Because singletons are constructed on first resolution, registering `ProcessManagerHandlerRegistry` as a `Bus` ctor argument is enough — the `_ = _processManagerRegistry;` line in `StartConsumingAsync` is belt-and-braces documentation + a trimmer safeguard.
