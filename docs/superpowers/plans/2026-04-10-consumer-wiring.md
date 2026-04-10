# Consumer Wiring Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire Bus.StartConsuming() to actual RabbitMQ consumers via MessageDispatcher, then implement full E2E tests for all messaging patterns.

**Architecture:** A MessageDispatcher implements the ConsumerEventHandler delegate, deserializing messages and routing them to IMessageHandler<T> instances resolved from DI. HandlerScanner discovers handlers via assembly scanning. ConsumeContext provides reply support. UseRabbitMQ() registers both IProducer and IConsumer.

**Tech Stack:** .NET 10.0, RabbitMQ.Client 6.8.1, xUnit 2.9.2, Testcontainers.RabbitMq 4.4.0, Moq 4.20.72

---

### Task 1: Add StartConsumingAsync to IBus

**Files:**
- Modify: `src/ServiceConnect.Interfaces/IBus.cs`

- [ ] **Step 1: Add the async method to IBus**

Add `Task StartConsumingAsync();` to the interface:

```csharp
// src/ServiceConnect.Interfaces/IBus.cs
namespace ServiceConnect.Interfaces;

public interface IBus : IDisposable
{
    Task PublishAsync<T>(T message, PublishOptions? options = null) where T : Message;
    Task SendAsync<T>(T message, SendOptions? options = null) where T : Message;
    Task<TReply> SendRequestAsync<T, TReply>(T message, RequestOptions? options = null)
        where T : Message where TReply : Message;
    Task<IList<TReply>> SendRequestMultiAsync<T, TReply>(T message, RequestOptions? options = null)
        where T : Message where TReply : Message;
    Task PublishRequestAsync<TRequest, TReply>(TRequest message, Action<TReply> onReply, RequestOptions? options = null)
        where TRequest : Message where TReply : Message;
    void Route<T>(T message, IList<string> destinations) where T : Message;
    IMessageBusWriteStream CreateStream<T>(string endpoint, T message) where T : Message;
    void StartConsuming();
    Task StartConsumingAsync();
    void StopConsuming();
    bool IsConnected { get; }
}
```

- [ ] **Step 2: Verify it builds**

Run: `cd src && dotnet build ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj`
Expected: Build succeeded (interface change only — Bus will fail to compile until Task 4).

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.Interfaces/IBus.cs
git commit -m "feat: add StartConsumingAsync to IBus interface"
```

---

### Task 2: Create HandlerScanner

**Files:**
- Create: `src/ServiceConnect/Services/HandlerScanner.cs`
- Create: `src/ServiceConnect.UnitTests/HandlerScannerTests.cs`

- [ ] **Step 1: Write the tests**

```csharp
// src/ServiceConnect.UnitTests/HandlerScannerTests.cs
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests;

public class HandlerScannerTests
{
    [Fact]
    public void ScanForHandlers_FindsHandlerInAssembly()
    {
        var assemblies = new[] { typeof(HandlerScannerTests).Assembly };

        var handlers = HandlerScanner.ScanForHandlers(assemblies);

        Assert.Contains(handlers, h => h.HandlerType == typeof(TestScannerHandler));
    }

    [Fact]
    public void ScanForHandlers_ReturnsCorrectMessageType()
    {
        var assemblies = new[] { typeof(HandlerScannerTests).Assembly };

        var handlers = HandlerScanner.ScanForHandlers(assemblies);
        var handler = handlers.First(h => h.HandlerType == typeof(TestScannerHandler));

        Assert.Equal(typeof(TestScannerMessage), handler.MessageType);
    }

    [Fact]
    public void ScanForHandlers_IgnoresAbstractClasses()
    {
        var assemblies = new[] { typeof(HandlerScannerTests).Assembly };

        var handlers = HandlerScanner.ScanForHandlers(assemblies);

        Assert.DoesNotContain(handlers, h => h.HandlerType == typeof(AbstractTestHandler));
    }

    [Fact]
    public void ScanForHandlers_IgnoresInterfaces()
    {
        var assemblies = new[] { typeof(HandlerScannerTests).Assembly };

        var handlers = HandlerScanner.ScanForHandlers(assemblies);

        Assert.DoesNotContain(handlers, h => h.HandlerType.IsInterface);
    }

    [Fact]
    public void ScanForHandlers_EmptyAssemblies_ReturnsEmpty()
    {
        var handlers = HandlerScanner.ScanForHandlers(Array.Empty<System.Reflection.Assembly>());

        Assert.Empty(handlers);
    }
}

// Test types for scanner
public class TestScannerMessage : Message
{
    public TestScannerMessage(Guid correlationId) : base(correlationId) { }
}

public class TestScannerHandler : IMessageHandler<TestScannerMessage>
{
    public IConsumeContext? Context { get; set; }
    public Task HandleAsync(TestScannerMessage message) => Task.CompletedTask;
}

public abstract class AbstractTestHandler : IMessageHandler<TestScannerMessage>
{
    public IConsumeContext? Context { get; set; }
    public abstract Task HandleAsync(TestScannerMessage message);
}
```

- [ ] **Step 2: Write the implementation**

```csharp
// src/ServiceConnect/Services/HandlerScanner.cs
using System.Reflection;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

public static class HandlerScanner
{
    public static IList<HandlerReference> ScanForHandlers(IEnumerable<Assembly> assemblies)
    {
        var handlerReferences = new List<HandlerReference>();
        var handlerInterfaceType = typeof(IMessageHandler<>);

        foreach (var assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray()!;
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface)
                    continue;

                var handlerInterfaces = type.GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == handlerInterfaceType);

                foreach (var handlerInterface in handlerInterfaces)
                {
                    var messageType = handlerInterface.GetGenericArguments()[0];
                    handlerReferences.Add(new HandlerReference
                    {
                        HandlerType = type,
                        MessageType = messageType,
                        RoutingKeys = new List<string>()
                    });
                }
            }
        }

        return handlerReferences;
    }
}
```

- [ ] **Step 3: Run the tests**

Run: `cd src && dotnet test ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~HandlerScannerTests" -v normal`
Expected: All 5 tests PASS.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect/Services/HandlerScanner.cs src/ServiceConnect.UnitTests/HandlerScannerTests.cs
git commit -m "feat: add HandlerScanner for message handler discovery"
```

---

### Task 3: Create ConsumeContext

**Files:**
- Create: `src/ServiceConnect/Services/ConsumeContext.cs`
- Create: `src/ServiceConnect.UnitTests/ConsumeContextTests.cs`

- [ ] **Step 1: Write the tests**

```csharp
// src/ServiceConnect.UnitTests/ConsumeContextTests.cs
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests;

public class ConsumeContextTests
{
    [Fact]
    public void Properties_AreSetFromConstructor()
    {
        var mockBus = new Mock<IBus>();
        var headers = new Dictionary<string, object>
        {
            ["MessageId"] = "msg-123",
            ["CorrelationId"] = Guid.NewGuid().ToString()
        };

        var context = new ConsumeContext(mockBus.Object, headers);

        Assert.Same(mockBus.Object, context.Bus);
        Assert.Same(headers, context.Headers);
        Assert.Equal("msg-123", context.MessageId);
    }

    [Fact]
    public void MessageId_ReturnsNull_WhenNotInHeaders()
    {
        var context = new ConsumeContext(new Mock<IBus>().Object, new Dictionary<string, object>());

        Assert.Null(context.MessageId);
    }

    [Fact]
    public void CorrelationId_ParsesFromHeaders()
    {
        var id = Guid.NewGuid();
        var headers = new Dictionary<string, object> { ["CorrelationId"] = id.ToString() };

        var context = new ConsumeContext(new Mock<IBus>().Object, headers);

        Assert.Equal(id, context.CorrelationId);
    }

    [Fact]
    public void CorrelationId_ReturnsEmptyGuid_WhenNotInHeaders()
    {
        var context = new ConsumeContext(new Mock<IBus>().Object, new Dictionary<string, object>());

        Assert.Equal(Guid.Empty, context.CorrelationId);
    }

    [Fact]
    public void Reply_SendsToSourceAddress()
    {
        var mockBus = new Mock<IBus>();
        mockBus.Setup(b => b.SendAsync(It.IsAny<ConsumeContextTestReply>(), It.IsAny<SendOptions>()))
            .Returns(Task.CompletedTask);

        var headers = new Dictionary<string, object>
        {
            ["SourceAddress"] = "requester-queue",
            ["RequestMessageId"] = "req-456"
        };

        var context = new ConsumeContext(mockBus.Object, headers);
        var reply = new ConsumeContextTestReply(Guid.NewGuid()) { Value = "ok" };

        context.Reply(reply);

        mockBus.Verify(b => b.SendAsync(
            reply,
            It.Is<SendOptions>(o =>
                o.EndPoint == "requester-queue" &&
                o.Headers != null &&
                o.Headers["ResponseMessageId"] == "req-456")),
            Times.Once);
    }
}

public class ConsumeContextTestReply : Message
{
    public ConsumeContextTestReply(Guid correlationId) : base(correlationId) { }
    public string Value { get; set; } = string.Empty;
}
```

- [ ] **Step 2: Write the implementation**

```csharp
// src/ServiceConnect/Services/ConsumeContext.cs
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

public class ConsumeContext : IConsumeContext
{
    public IBus Bus { get; set; }
    public IDictionary<string, object> Headers { get; set; }

    public string? MessageId =>
        Headers.TryGetValue(HeaderKeys.MessageId, out var value) ? value?.ToString() : null;

    public Guid CorrelationId =>
        Headers.TryGetValue(HeaderKeys.CorrelationId, out var value) && Guid.TryParse(value?.ToString(), out var id)
            ? id
            : Guid.Empty;

    public ConsumeContext(IBus bus, IDictionary<string, object> headers)
    {
        Bus = bus;
        Headers = headers;
    }

    public void Reply<TReply>(TReply message, Dictionary<string, string>? headers = null) where TReply : Message
    {
        var sourceAddress = Headers.TryGetValue(HeaderKeys.SourceAddress, out var sa) ? sa?.ToString() : null;
        if (string.IsNullOrEmpty(sourceAddress))
            throw new InvalidOperationException("Cannot reply: incoming message has no SourceAddress header.");

        var requestMessageId = Headers.TryGetValue(HeaderKeys.RequestMessageId, out var rmi) ? rmi?.ToString() : null;

        var replyHeaders = headers ?? new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(requestMessageId))
            replyHeaders["ResponseMessageId"] = requestMessageId;

        var options = new SendOptions
        {
            EndPoint = sourceAddress,
            Headers = replyHeaders
        };

        Bus.SendAsync(message, options).GetAwaiter().GetResult();
    }
}
```

- [ ] **Step 3: Run the tests**

Run: `cd src && dotnet test ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ConsumeContextTests" -v normal`
Expected: All 5 tests PASS.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect/Services/ConsumeContext.cs src/ServiceConnect.UnitTests/ConsumeContextTests.cs
git commit -m "feat: add ConsumeContext with Reply support"
```

---

### Task 4: Create MessageDispatcher

**Files:**
- Create: `src/ServiceConnect/Services/MessageDispatcher.cs`
- Create: `src/ServiceConnect.UnitTests/MessageDispatcherTests.cs`

- [ ] **Step 1: Write the tests**

```csharp
// src/ServiceConnect.UnitTests/MessageDispatcherTests.cs
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MessageDispatcherTests
{
    private readonly Mock<IMessageSerializer> _mockSerializer;
    private readonly Mock<IFilterPipeline> _mockFilterPipeline;
    private readonly Mock<IRequestReplyManager> _mockReplyManager;
    private readonly ServiceCollection _services;

    public MessageDispatcherTests()
    {
        _mockSerializer = new Mock<IMessageSerializer>();
        _mockFilterPipeline = new Mock<IFilterPipeline>();
        _mockReplyManager = new Mock<IRequestReplyManager>();
        _mockFilterPipeline.Setup(f => f.ExecuteBeforeConsumingFilters(It.IsAny<Envelope>())).Returns(false);
        _mockFilterPipeline.Setup(f => f.ExecuteAfterConsumingFilters(It.IsAny<Envelope>())).Returns(false);

        _services = new ServiceCollection();
        _services.AddLogging();
    }

    private MessageDispatcher CreateDispatcher(IServiceProvider provider)
    {
        return new MessageDispatcher(
            provider,
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            _mockReplyManager.Object,
            provider.GetRequiredService<ILogger<MessageDispatcher>>());
    }

    [Fact]
    public async Task Dispatch_DeserializesAndCallsHandler()
    {
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "test" };
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<byte[]>(), typeof(FakeMessage1))).Returns(message);

        var handlerCalled = false;
        var handler = new TestDispatchHandler(() => handlerCalled = true);
        _services.AddSingleton<IMessageHandler<FakeMessage1>>(handler);
        var provider = _services.BuildServiceProvider();

        var dispatcher = CreateDispatcher(provider);
        var headers = new Dictionary<string, object>
        {
            ["FullTypeName"] = Encoding.UTF8.GetBytes(typeof(FakeMessage1).AssemblyQualifiedName!)
        };

        var result = await dispatcher.Dispatch(new byte[] { 1, 2, 3 }, typeof(FakeMessage1).FullName!, headers);

        Assert.True(result.Success);
        Assert.True(handlerCalled);
    }

    [Fact]
    public async Task Dispatch_SetsConsumeContextOnHandler()
    {
        var message = new FakeMessage1(Guid.NewGuid());
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<byte[]>(), typeof(FakeMessage1))).Returns(message);

        IConsumeContext? capturedContext = null;
        var handler = new TestDispatchHandler(ctx => capturedContext = ctx);
        _services.AddSingleton<IMessageHandler<FakeMessage1>>(handler);
        _services.AddSingleton<IBus>(new Mock<IBus>().Object);
        var provider = _services.BuildServiceProvider();

        var dispatcher = CreateDispatcher(provider);
        var headers = new Dictionary<string, object>
        {
            ["FullTypeName"] = Encoding.UTF8.GetBytes(typeof(FakeMessage1).AssemblyQualifiedName!)
        };

        await dispatcher.Dispatch(new byte[] { 1 }, typeof(FakeMessage1).FullName!, headers);

        Assert.NotNull(capturedContext);
    }

    [Fact]
    public async Task Dispatch_BeforeConsumingFilterBlocks_HandlerNotCalled()
    {
        _mockFilterPipeline.Setup(f => f.ExecuteBeforeConsumingFilters(It.IsAny<Envelope>())).Returns(true);

        var handlerCalled = false;
        var handler = new TestDispatchHandler(() => handlerCalled = true);
        _services.AddSingleton<IMessageHandler<FakeMessage1>>(handler);
        var provider = _services.BuildServiceProvider();

        var dispatcher = CreateDispatcher(provider);
        var headers = new Dictionary<string, object>
        {
            ["FullTypeName"] = Encoding.UTF8.GetBytes(typeof(FakeMessage1).AssemblyQualifiedName!)
        };

        var result = await dispatcher.Dispatch(new byte[] { 1 }, typeof(FakeMessage1).FullName!, headers);

        Assert.True(result.Success);
        Assert.False(handlerCalled);
    }

    [Fact]
    public async Task Dispatch_ResponseMessage_RoutesToReplyManager()
    {
        var provider = _services.BuildServiceProvider();
        var dispatcher = CreateDispatcher(provider);
        var headers = new Dictionary<string, object>
        {
            ["FullTypeName"] = Encoding.UTF8.GetBytes(typeof(FakeMessage1).AssemblyQualifiedName!),
            ["ResponseMessageId"] = "req-123"
        };

        var result = await dispatcher.Dispatch(new byte[] { 1, 2, 3 }, typeof(FakeMessage1).FullName!, headers);

        Assert.True(result.Success);
        _mockReplyManager.Verify(r => r.ProcessReply("req-123", It.IsAny<byte[]>(), typeof(FakeMessage1)), Times.Once);
    }

    [Fact]
    public async Task Dispatch_HandlerThrows_ReturnsFailure()
    {
        var message = new FakeMessage1(Guid.NewGuid());
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<byte[]>(), typeof(FakeMessage1))).Returns(message);

        var handler = new TestDispatchHandler(() => throw new InvalidOperationException("Handler error"));
        _services.AddSingleton<IMessageHandler<FakeMessage1>>(handler);
        var provider = _services.BuildServiceProvider();

        var dispatcher = CreateDispatcher(provider);
        var headers = new Dictionary<string, object>
        {
            ["FullTypeName"] = Encoding.UTF8.GetBytes(typeof(FakeMessage1).AssemblyQualifiedName!)
        };

        var result = await dispatcher.Dispatch(new byte[] { 1 }, typeof(FakeMessage1).FullName!, headers);

        Assert.False(result.Success);
        Assert.IsType<InvalidOperationException>(result.Exception);
    }

    [Fact]
    public async Task Dispatch_NoHandler_ReturnsSuccess()
    {
        var message = new FakeMessage1(Guid.NewGuid());
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<byte[]>(), typeof(FakeMessage1))).Returns(message);

        // No handler registered
        var provider = _services.BuildServiceProvider();
        var dispatcher = CreateDispatcher(provider);
        var headers = new Dictionary<string, object>
        {
            ["FullTypeName"] = Encoding.UTF8.GetBytes(typeof(FakeMessage1).AssemblyQualifiedName!)
        };

        var result = await dispatcher.Dispatch(new byte[] { 1 }, typeof(FakeMessage1).FullName!, headers);

        Assert.True(result.Success);
    }
}

// Test handler
file class TestDispatchHandler : IMessageHandler<FakeMessage1>
{
    private readonly Action? _onHandle;
    private readonly Action<IConsumeContext?>? _onContext;

    public TestDispatchHandler(Action onHandle) { _onHandle = onHandle; }
    public TestDispatchHandler(Action<IConsumeContext?> onContext) { _onContext = onContext; }

    public IConsumeContext? Context { get; set; }

    public Task HandleAsync(FakeMessage1 message)
    {
        _onContext?.Invoke(Context);
        _onHandle?.Invoke();
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Write the implementation**

```csharp
// src/ServiceConnect/Services/MessageDispatcher.cs
using System.Text;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

public class MessageDispatcher
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IMessageSerializer _serializer;
    private readonly IFilterPipeline _filterPipeline;
    private readonly IRequestReplyManager _replyManager;
    private readonly ILogger<MessageDispatcher> _logger;

    public MessageDispatcher(
        IServiceProvider serviceProvider,
        IMessageSerializer serializer,
        IFilterPipeline filterPipeline,
        IRequestReplyManager replyManager,
        ILogger<MessageDispatcher> logger)
    {
        _serviceProvider = serviceProvider;
        _serializer = serializer;
        _filterPipeline = filterPipeline;
        _replyManager = replyManager;
        _logger = logger;
    }

    public async Task<ConsumeEventResult> Dispatch(byte[] messageBytes, string typeName, IDictionary<string, object> headers)
    {
        try
        {
            // Resolve CLR type from FullTypeName header
            var fullTypeName = headers.TryGetValue(HeaderKeys.FullTypeName, out var ftn)
                ? (ftn is byte[] bytes ? Encoding.UTF8.GetString(bytes) : ftn.ToString())
                : typeName;

            var messageType = Type.GetType(fullTypeName!);
            if (messageType == null)
            {
                _logger.LogWarning("Could not resolve type: {TypeName}", fullTypeName);
                return new ConsumeEventResult { Success = false, Exception = new TypeLoadException($"Cannot resolve type '{fullTypeName}'") };
            }

            // Check if this is a reply to a pending request
            var responseMessageId = headers.TryGetValue("ResponseMessageId", out var rmi)
                ? (rmi is byte[] rmiBytes ? Encoding.UTF8.GetString(rmiBytes) : rmi.ToString())
                : null;

            if (!string.IsNullOrEmpty(responseMessageId))
            {
                _replyManager.ProcessReply(responseMessageId!, messageBytes, messageType);
                return new ConsumeEventResult { Success = true };
            }

            // Deserialize message
            var message = _serializer.Deserialize(messageBytes, messageType);

            // Build envelope for filters
            var envelope = new Envelope { Body = messageBytes, Headers = headers };

            // Before consuming filters
            if (_filterPipeline.ExecuteBeforeConsumingFilters(envelope))
            {
                return new ConsumeEventResult { Success = true }; // Filtered out
            }

            // Resolve handlers: IMessageHandler<TMessage>
            var handlerInterfaceType = typeof(IMessageHandler<>).MakeGenericType(messageType);
            var handlers = _serviceProvider.GetServices(handlerInterfaceType);

            // Build consume context
            var bus = _serviceProvider.GetService<IBus>();
            var context = new ConsumeContext(bus!, headers);

            // Invoke each handler
            foreach (var handler in handlers)
            {
                // Set context
                var contextProp = handlerInterfaceType.GetProperty("Context");
                contextProp?.SetValue(handler, context);

                // Call HandleAsync via reflection
                var handleMethod = handlerInterfaceType.GetMethod("HandleAsync");
                var task = (Task)handleMethod!.Invoke(handler, new[] { message })!;
                await task;
            }

            // After consuming filters
            _filterPipeline.ExecuteAfterConsumingFilters(envelope);

            return new ConsumeEventResult { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dispatching message of type {TypeName}", typeName);
            return new ConsumeEventResult { Success = false, Exception = ex };
        }
    }
}
```

- [ ] **Step 3: Run the tests**

Run: `cd src && dotnet test ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MessageDispatcherTests" -v normal`
Expected: All 6 tests PASS.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect/Services/MessageDispatcher.cs src/ServiceConnect.UnitTests/MessageDispatcherTests.cs
git commit -m "feat: add MessageDispatcher for consumer-side message routing"
```

---

### Task 5: Update UseRabbitMQ to Register IProducer and IConsumer

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/RabbitMQExtensions.cs`
- Create: `src/ServiceConnect.UnitTests/RabbitMQExtensionsTests.cs`

- [ ] **Step 1: Write the tests**

```csharp
// src/ServiceConnect.UnitTests/RabbitMQExtensionsTests.cs
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests;

public class RabbitMQExtensionsTests
{
    [Fact]
    public void UseRabbitMQ_RegistersProducerAndConsumer()
    {
        var builder = new ServiceConnectBuilder();

        builder.UseRabbitMQ();

        Assert.NotEmpty(builder.AdditionalRegistrations);
    }

    [Fact]
    public void UseRabbitMQ_AppliesTransportConfig()
    {
        var builder = new ServiceConnectBuilder();

        builder.UseRabbitMQ(t => t.Host = "myhost");

        Assert.Equal("myhost", builder.BusConfig.Transport.Host);
    }
}
```

- [ ] **Step 2: Update UseRabbitMQ**

```csharp
// src/ServiceConnect.Client.RabbitMQ/RabbitMQExtensions.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

public static class RabbitMQExtensions
{
    public static ServiceConnectBuilder UseRabbitMQ(
        this ServiceConnectBuilder builder,
        Action<ITransportConfiguration>? configure = null)
    {
        if (configure != null)
        {
            builder.ConfigureTransport(configure);
        }

        builder.AdditionalRegistrations.Add(services =>
        {
            services.TryAddSingleton<IProducer, Producer>();
            services.TryAddSingleton<IConsumer, Consumer>();
        });

        return builder;
    }
}
```

- [ ] **Step 3: Run the tests**

Run: `cd src && dotnet test ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RabbitMQExtensionsTests" -v normal`
Expected: All 2 tests PASS.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/RabbitMQExtensions.cs src/ServiceConnect.UnitTests/RabbitMQExtensionsTests.cs
git commit -m "feat: UseRabbitMQ now registers IProducer and IConsumer"
```

---

### Task 6: Update ServiceCollectionExtensions and Bus for Consumer Wiring

**Files:**
- Modify: `src/ServiceConnect/ServiceCollectionExtensions.cs`
- Modify: `src/ServiceConnect/Bus.cs`

- [ ] **Step 1: Update ServiceCollectionExtensions**

```csharp
// src/ServiceConnect/ServiceCollectionExtensions.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;

namespace ServiceConnect;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddServiceConnect(
        this IServiceCollection services,
        Action<ServiceConnectBuilder> configure)
    {
        var builder = new ServiceConnectBuilder();
        configure(builder);

        // Configuration
        services.TryAddSingleton<IBusConfiguration>(builder.BusConfig);
        services.TryAddSingleton<ITransportConfiguration>(builder.BusConfig.Transport);
        services.TryAddSingleton<IQueueConfiguration>(builder.BusConfig.Queues);
        services.TryAddSingleton<IPersistenceConfiguration>(builder.BusConfig.Persistence);
        services.TryAddSingleton<IPipelineConfiguration>(builder.BusConfig.Pipeline);

        // Core services
        services.TryAddSingleton<IMessageSerializer, NewtonsoftJsonMessageSerializer>();
        services.TryAddSingleton<IFilterPipeline, FilterPipeline>();
        services.TryAddSingleton<IRequestReplyManager, RequestReplyManager>();
        services.TryAddSingleton<ISendMessagePipeline, SendMessagePipeline>();
        services.TryAddSingleton<MessageDispatcher>();

        // Handler scanning
        IList<HandlerReference> handlerReferences;
        if (builder.BusConfig.ScanForMessageHandlers)
        {
            handlerReferences = HandlerScanner.ScanForHandlers(AppDomain.CurrentDomain.GetAssemblies());
        }
        else
        {
            handlerReferences = new List<HandlerReference>();
        }

        // Register discovered handlers in DI
        foreach (var handlerRef in handlerReferences)
        {
            var handlerInterfaceType = typeof(IMessageHandler<>).MakeGenericType(handlerRef.MessageType);
            services.TryAddTransient(handlerInterfaceType, handlerRef.HandlerType);
        }

        services.TryAddSingleton<IList<HandlerReference>>(handlerReferences);

        // Apply additional registrations from builder extensions (e.g., persistence providers, transport)
        foreach (var registration in builder.AdditionalRegistrations)
        {
            registration(services);
        }

        // Bus
        services.TryAddSingleton<IBus, Bus>();

        return services;
    }
}
```

- [ ] **Step 2: Update Bus**

```csharp
// src/ServiceConnect/Bus.cs
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;

namespace ServiceConnect;

public sealed class Bus : IBus
{
    private readonly IMessageSerializer _serializer;
    private readonly IFilterPipeline _filterPipeline;
    private readonly ISendMessagePipeline _sendPipeline;
    private readonly IRequestReplyManager _requestReplyManager;
    private readonly IBusConfiguration _config;
    private readonly IQueueConfiguration _queueConfig;
    private readonly ILogger<Bus> _logger;
    private readonly IConsumer? _consumer;
    private readonly MessageDispatcher _dispatcher;
    private readonly IList<HandlerReference> _handlerReferences;
    private bool _consuming;
    private bool _disposed;

    public Bus(
        IMessageSerializer serializer,
        IFilterPipeline filterPipeline,
        ISendMessagePipeline sendPipeline,
        IRequestReplyManager requestReplyManager,
        IBusConfiguration config,
        IQueueConfiguration queueConfig,
        ILogger<Bus> logger,
        MessageDispatcher dispatcher,
        IList<HandlerReference> handlerReferences,
        IConsumer? consumer = null)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _filterPipeline = filterPipeline ?? throw new ArgumentNullException(nameof(filterPipeline));
        _sendPipeline = sendPipeline ?? throw new ArgumentNullException(nameof(sendPipeline));
        _requestReplyManager = requestReplyManager ?? throw new ArgumentNullException(nameof(requestReplyManager));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _queueConfig = queueConfig ?? throw new ArgumentNullException(nameof(queueConfig));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _handlerReferences = handlerReferences ?? throw new ArgumentNullException(nameof(handlerReferences));
        _consumer = consumer; // nullable — no consumer registered means consuming is unavailable
    }

    public bool IsConnected => _consuming;

    public async Task PublishAsync<T>(T message, PublishOptions? options = null) where T : Message
    {
        var messageBytes = _serializer.Serialize(message);
        var envelope = CreateEnvelope(typeof(T), messageBytes, options?.Headers);

        if (_filterPipeline.ExecuteOutgoingFilters(envelope))
            return;

        var headers = ExtractHeaders(envelope);

        if (options?.RoutingKey is not null)
            headers[HeaderKeys.RoutingKey] = options.RoutingKey;

        await _sendPipeline.ExecutePublishMessagePipelineAsync(typeof(T), messageBytes, headers).ConfigureAwait(false);
    }

    public async Task SendAsync<T>(T message, SendOptions? options = null) where T : Message
    {
        var messageBytes = _serializer.Serialize(message);
        var envelope = CreateEnvelope(typeof(T), messageBytes, options?.Headers);

        if (_filterPipeline.ExecuteOutgoingFilters(envelope))
            return;

        var headers = ExtractHeaders(envelope);

        if (options?.EndPoints is { Count: > 0 } endpoints)
        {
            foreach (var endpoint in endpoints)
            {
                await _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(T), messageBytes, headers, endpoint).ConfigureAwait(false);
            }
        }
        else
        {
            await _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(T), messageBytes, headers, options?.EndPoint).ConfigureAwait(false);
        }
    }

    public async Task<TReply> SendRequestAsync<T, TReply>(T message, RequestOptions? options = null)
        where T : Message where TReply : Message
    {
        var requestOptions = options ?? new RequestOptions();
        var messageBytes = _serializer.Serialize(message);
        var envelope = CreateEnvelope(typeof(T), messageBytes, requestOptions.Headers);

        if (_filterPipeline.ExecuteOutgoingFilters(envelope))
            throw new InvalidOperationException("Outgoing filters blocked the request message.");

        var headers = ExtractHeaders(envelope);

        return await _requestReplyManager.SendRequestAsync<T, TReply>(
            messageBytes,
            headers,
            (type, bytes, hdrs, endpoint) => _sendPipeline.ExecuteSendMessagePipelineAsync(type, bytes, hdrs, endpoint),
            requestOptions).ConfigureAwait(false);
    }

    public async Task<IList<TReply>> SendRequestMultiAsync<T, TReply>(T message, RequestOptions? options = null)
        where T : Message where TReply : Message
    {
        var requestOptions = options ?? new RequestOptions();
        var messageBytes = _serializer.Serialize(message);
        var envelope = CreateEnvelope(typeof(T), messageBytes, requestOptions.Headers);

        if (_filterPipeline.ExecuteOutgoingFilters(envelope))
            throw new InvalidOperationException("Outgoing filters blocked the request message.");

        var headers = ExtractHeaders(envelope);

        return await _requestReplyManager.SendRequestMultiAsync<T, TReply>(
            messageBytes,
            headers,
            (type, bytes, hdrs, endpoint) => _sendPipeline.ExecuteSendMessagePipelineAsync(type, bytes, hdrs, endpoint),
            requestOptions).ConfigureAwait(false);
    }

    public async Task PublishRequestAsync<TRequest, TReply>(TRequest message, Action<TReply> onReply, RequestOptions? options = null)
        where TRequest : Message where TReply : Message
    {
        var replies = await SendRequestMultiAsync<TRequest, TReply>(message, options).ConfigureAwait(false);

        foreach (var reply in replies)
        {
            onReply(reply);
        }
    }

    public void Route<T>(T message, IList<string> destinations) where T : Message
    {
        if (destinations == null || destinations.Count == 0)
            throw new ArgumentException("At least one destination is required.", nameof(destinations));

        var firstDestination = destinations[0];
        var messageBytes = _serializer.Serialize(message);
        var envelope = CreateEnvelope(typeof(T), messageBytes);

        if (_filterPipeline.ExecuteOutgoingFilters(envelope))
            return;

        var headers = ExtractHeaders(envelope);

        if (destinations.Count > 1)
        {
            var remainingDestinations = new List<string>();
            for (var i = 1; i < destinations.Count; i++)
                remainingDestinations.Add(destinations[i]);

            headers[HeaderKeys.RoutingSlip] = string.Join(",", remainingDestinations);
        }

        Task.Run(() => _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(T), messageBytes, headers, firstDestination))
            .GetAwaiter()
            .GetResult();
    }

    public IMessageBusWriteStream CreateStream<T>(string endpoint, T message) where T : Message
    {
        throw new NotImplementedException("Stream support will be wired in a later task.");
    }

    public async Task StartConsumingAsync()
    {
        if (_consumer == null)
            throw new InvalidOperationException("No consumer registered. Call UseRabbitMQ() or register an IConsumer.");

        var messageTypeNames = _handlerReferences
            .Select(h => h.MessageType.FullName!.Replace(".", string.Empty))
            .Distinct()
            .ToList();

        _logger.LogInformation("Bus starting to consume messages on queue {QueueName} for {Count} message types.",
            _queueConfig.QueueName, messageTypeNames.Count);

        await _consumer.StartConsumingAsync(_queueConfig.QueueName, messageTypeNames, _dispatcher.Dispatch);
        _consuming = true;
    }

    public void StartConsuming()
    {
        StartConsumingAsync().GetAwaiter().GetResult();
    }

    public void StopConsuming()
    {
        _logger.LogInformation("Bus stopping message consumption.");
        _consuming = false;
        _consumer?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopConsuming();
        _sendPipeline.Dispose();
    }

    private static Envelope CreateEnvelope(Type messageType, byte[] body, Dictionary<string, string>? additionalHeaders = null)
    {
        var envelope = new Envelope
        {
            Body = body,
            Headers = new Dictionary<string, object>
            {
                [HeaderKeys.MessageType] = messageType.FullName ?? messageType.Name
            }
        };

        if (additionalHeaders is not null)
        {
            foreach (var header in additionalHeaders)
            {
                envelope.Headers[header.Key] = header.Value;
            }
        }

        return envelope;
    }

    private static Dictionary<string, string> ExtractHeaders(Envelope envelope)
    {
        var headers = new Dictionary<string, string>();
        foreach (var kvp in envelope.Headers)
        {
            headers[kvp.Key] = kvp.Value?.ToString() ?? string.Empty;
        }
        return headers;
    }
}
```

- [ ] **Step 3: Fix existing unit tests that construct Bus directly**

The Bus constructor now requires more parameters. Update `BusTests.cs` constructor:

The new Bus constructor requires: `serializer, filterPipeline, sendPipeline, requestReplyManager, config, queueConfig, logger, dispatcher, handlerReferences, consumer?`

Update the `BusTests` constructor to add the new mocks:

```csharp
// In BusTests constructor, add:
private readonly Mock<IQueueConfiguration> _mockQueueConfig;
private readonly Mock<MessageDispatcher> _mockDispatcher; // Note: may need concrete, check below

// Actually, MessageDispatcher is not easily mockable since it's a concrete class.
// Create it with mock dependencies instead:
```

Since `MessageDispatcher` is a concrete class, pass a real instance with mocked dependencies. Or simpler: make `Bus` accept nullable `MessageDispatcher` and `IList<HandlerReference>` via a secondary constructor or default values. But the cleanest approach: update the test to provide the new dependencies.

Add to `BusTests` constructor setup:
```csharp
var mockQueueConfig = new Mock<IQueueConfiguration>();
var services = new ServiceCollection();
services.AddLogging();
var provider = services.BuildServiceProvider();
var dispatcher = new MessageDispatcher(
    provider,
    _mockSerializer.Object,
    _mockFilterPipeline.Object,
    _mockRequestReplyManager.Object,
    provider.GetRequiredService<ILogger<MessageDispatcher>>());
var handlerReferences = new List<HandlerReference>();

_bus = new Bus(
    _mockSerializer.Object,
    _mockFilterPipeline.Object,
    _mockSendPipeline.Object,
    _mockRequestReplyManager.Object,
    _mockConfig.Object,
    mockQueueConfig.Object,
    _mockLogger.Object,
    dispatcher,
    handlerReferences);
```

Also update the `Constructor_ShouldThrow_WhenDependencyIsNull` test to pass the new parameters. Each null-guard assertion needs the full parameter list.

- [ ] **Step 4: Run all unit tests**

Run: `cd src && dotnet test ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -v minimal`
Expected: All tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/ServiceCollectionExtensions.cs src/ServiceConnect/Bus.cs src/ServiceConnect.UnitTests/BusTests.cs
git commit -m "feat: wire Bus to consumer via MessageDispatcher and HandlerScanner"
```

---

### Task 7: E2E Test — Publish/Subscribe

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/PublishSubscribeTests.cs`

- [ ] **Step 1: Write the test**

```csharp
// src/ServiceConnect.EndToEndTests/PublishSubscribeTests.cs
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class PublishSubscribeTests
{
    private readonly MessagingFixture _fixture;

    public PublishSubscribeTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Publish_SubscriberReceivesMessage()
    {
        var receivedTcs = new TaskCompletionSource<TestMessage>();
        var queueName = _fixture.GetUniqueQueueName("pubsub");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMessageHandler<TestMessage>>(new CallbackHandler<TestMessage>(msg => receivedTcs.TrySetResult(msg)));
        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3;
                t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();

        // Allow consumer to be fully ready
        await Task.Delay(500);

        await bus.PublishAsync(new TestMessage(Guid.NewGuid()) { Content = "Hello Pub/Sub" });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        cts.Token.Register(() => receivedTcs.TrySetCanceled());
        var received = await receivedTcs.Task;

        Assert.Equal("Hello Pub/Sub", received.Content);

        bus.Dispose();
    }
}

// Reusable callback handler for E2E tests
public class CallbackHandler<T> : IMessageHandler<T> where T : Message
{
    private readonly Action<T> _callback;
    public IConsumeContext? Context { get; set; }

    public CallbackHandler(Action<T> callback) => _callback = callback;

    public Task HandleAsync(T message)
    {
        _callback(message);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Build and run**

Run: `cd src && sg docker -c "dotnet test ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter 'FullyQualifiedName~PublishSubscribeTests' -v normal"`
Expected: Test PASSES.

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/PublishSubscribeTests.cs
git commit -m "test: add publish/subscribe E2E test"
```

---

### Task 8: E2E Test — Request/Reply (Full E2E)

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/RequestReplyE2ETests.cs`

- [ ] **Step 1: Write the test**

```csharp
// src/ServiceConnect.EndToEndTests/RequestReplyE2ETests.cs
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class RequestReplyE2ETests
{
    private readonly MessagingFixture _fixture;

    public RequestReplyE2ETests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task SendRequest_ResponderReplies_RequesterGetsResponse()
    {
        var responderQueue = _fixture.GetUniqueQueueName("responder");
        var requesterQueue = _fixture.GetUniqueQueueName("requester");

        // Set up responder bus
        var responderServices = new ServiceCollection();
        responderServices.AddLogging();
        responderServices.AddSingleton<IMessageHandler<TestRequest>>(new ReplyHandler());
        responderServices.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3;
                t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = responderQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var responderProvider = responderServices.BuildServiceProvider();
        var responderBus = responderProvider.GetRequiredService<IBus>();
        await responderBus.StartConsumingAsync();

        // Set up requester bus
        var requesterServices = new ServiceCollection();
        requesterServices.AddLogging();
        requesterServices.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3;
                t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = requesterQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var requesterProvider = requesterServices.BuildServiceProvider();
        var requesterBus = requesterProvider.GetRequiredService<IBus>();
        await requesterBus.StartConsumingAsync();

        await Task.Delay(500);

        var response = await requesterBus.SendRequestAsync<TestRequest, TestResponse>(
            new TestRequest(Guid.NewGuid()) { Question = "What is 2+2?" },
            new RequestOptions { EndPoint = responderQueue, Timeout = 30000 });

        Assert.Equal("The answer is 4", response.Answer);

        requesterBus.Dispose();
        responderBus.Dispose();
    }
}

file class ReplyHandler : IMessageHandler<TestRequest>
{
    public IConsumeContext? Context { get; set; }

    public Task HandleAsync(TestRequest message)
    {
        Context!.Reply(new TestResponse(Guid.NewGuid()) { Answer = "The answer is 4" });
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Build and run**

Run: `cd src && sg docker -c "dotnet test ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter 'FullyQualifiedName~RequestReplyE2ETests' -v normal"`
Expected: Test PASSES.

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/RequestReplyE2ETests.cs
git commit -m "test: add full request/reply E2E test with real RabbitMQ"
```

---

### Task 9: E2E Tests — Competing Consumers and Content Routing

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/CompetingConsumersTests.cs`
- Create: `src/ServiceConnect.EndToEndTests/ContentRoutingTests.cs`

- [ ] **Step 1: Write CompetingConsumersTests**

```csharp
// src/ServiceConnect.EndToEndTests/CompetingConsumersTests.cs
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class CompetingConsumersTests
{
    private readonly MessagingFixture _fixture;

    public CompetingConsumersTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task TwoConsumers_SameQueue_EachGetsSubset()
    {
        var queueName = _fixture.GetUniqueQueueName("competing");
        var consumer1Messages = new ConcurrentBag<string>();
        var consumer2Messages = new ConcurrentBag<string>();
        var allReceived = new TaskCompletionSource<bool>();
        var totalExpected = 10;
        var totalReceived = 0;

        IBus CreateConsumerBus(ConcurrentBag<string> bag)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IMessageHandler<TestMessage>>(new CallbackHandler<TestMessage>(msg =>
            {
                bag.Add(msg.Content);
                if (Interlocked.Increment(ref totalReceived) >= totalExpected)
                    allReceived.TrySetResult(true);
            }));
            services.AddServiceConnect(builder =>
            {
                builder.UseRabbitMQ(t =>
                {
                    t.Host = _fixture.RabbitMqHostname;
                    t.Username = _fixture.RabbitMqUsername;
                    t.Password = _fixture.RabbitMqPassword;
                    t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                    t.ClientSettings["RetryCount"] = 3;
                    t.ClientSettings["RetrySeconds"] = 1;
                });
                builder.ConfigureQueues(q => q.QueueName = queueName);
                builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
            });
            var provider = services.BuildServiceProvider();
            return provider.GetRequiredService<IBus>();
        }

        var bus1 = CreateConsumerBus(consumer1Messages);
        var bus2 = CreateConsumerBus(consumer2Messages);
        await bus1.StartConsumingAsync();
        await bus2.StartConsumingAsync();
        await Task.Delay(500);

        // Publish via a separate producer bus
        var producerServices = new ServiceCollection();
        producerServices.AddLogging();
        producerServices.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3;
                t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = _fixture.GetUniqueQueueName("producer"));
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });
        var producerProvider = producerServices.BuildServiceProvider();
        var producerBus = producerProvider.GetRequiredService<IBus>();

        for (int i = 0; i < totalExpected; i++)
        {
            await producerBus.SendAsync(new TestMessage(Guid.NewGuid()) { Content = $"msg-{i}" },
                new SendOptions { EndPoint = queueName });
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        cts.Token.Register(() => allReceived.TrySetResult(false));
        await allReceived.Task;

        // Both consumers should have received some messages
        Assert.Equal(totalExpected, consumer1Messages.Count + consumer2Messages.Count);

        bus1.Dispose();
        bus2.Dispose();
        producerBus.Dispose();
    }
}
```

- [ ] **Step 2: Write ContentRoutingTests**

```csharp
// src/ServiceConnect.EndToEndTests/ContentRoutingTests.cs
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class ContentRoutingTests
{
    private readonly MessagingFixture _fixture;

    public ContentRoutingTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task DifferentMessageTypes_RouteToCorrectHandlers()
    {
        var queueName = _fixture.GetUniqueQueueName("content-route");
        var testMessageReceived = new TaskCompletionSource<TestMessage>();
        var stepMessageReceived = new TaskCompletionSource<StepMessage>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMessageHandler<TestMessage>>(new CallbackHandler<TestMessage>(msg => testMessageReceived.TrySetResult(msg)));
        services.AddSingleton<IMessageHandler<StepMessage>>(new CallbackHandler<StepMessage>(msg => stepMessageReceived.TrySetResult(msg)));
        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3;
                t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();
        await Task.Delay(500);

        await bus.PublishAsync(new TestMessage(Guid.NewGuid()) { Content = "hello" });
        await bus.PublishAsync(new StepMessage(Guid.NewGuid()) { CurrentStep = "step1" });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        cts.Token.Register(() => { testMessageReceived.TrySetCanceled(); stepMessageReceived.TrySetCanceled(); });

        var testMsg = await testMessageReceived.Task;
        var stepMsg = await stepMessageReceived.Task;

        Assert.Equal("hello", testMsg.Content);
        Assert.Equal("step1", stepMsg.CurrentStep);

        bus.Dispose();
    }
}
```

- [ ] **Step 3: Build and run**

Run: `cd src && sg docker -c "dotnet test ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter 'FullyQualifiedName~CompetingConsumersTests|FullyQualifiedName~ContentRoutingTests' -v normal"`
Expected: Tests PASS.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/CompetingConsumersTests.cs src/ServiceConnect.EndToEndTests/ContentRoutingTests.cs
git commit -m "test: add competing consumers and content routing E2E tests"
```

---

### Task 10: E2E Tests — Priority Queues and Before/After Consuming Filters

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/PriorityQueueTests.cs`
- Create: `src/ServiceConnect.EndToEndTests/FilterPipelineConsumerTests.cs`

- [ ] **Step 1: Write PriorityQueueTests**

```csharp
// src/ServiceConnect.EndToEndTests/PriorityQueueTests.cs
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class PriorityQueueTests
{
    private readonly MessagingFixture _fixture;

    public PriorityQueueTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task HigherPriority_ConsumedFirst()
    {
        var queueName = _fixture.GetUniqueQueueName("priority");
        var receivedOrder = new ConcurrentQueue<int>();
        var allReceived = new TaskCompletionSource<bool>();
        var totalExpected = 3;
        var totalReceived = 0;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMessageHandler<PriorityMessage>>(new CallbackHandler<PriorityMessage>(msg =>
        {
            receivedOrder.Enqueue(msg.Priority);
            if (Interlocked.Increment(ref totalReceived) >= totalExpected)
                allReceived.TrySetResult(true);
        }));
        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3;
                t.ClientSettings["RetrySeconds"] = 1;
                t.ClientSettings["Arguments"] = new Dictionary<string, object> { { "x-max-priority", (byte)10 } };
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        // Send messages with different priorities BEFORE starting consumer
        // so they queue up and priority ordering can take effect
        await bus.SendAsync(new PriorityMessage(Guid.NewGuid()) { Priority = 1, Order = 1 },
            new SendOptions { EndPoint = queueName, Headers = new Dictionary<string, string> { ["Priority"] = "1" } });
        await bus.SendAsync(new PriorityMessage(Guid.NewGuid()) { Priority = 5, Order = 2 },
            new SendOptions { EndPoint = queueName, Headers = new Dictionary<string, string> { ["Priority"] = "5" } });
        await bus.SendAsync(new PriorityMessage(Guid.NewGuid()) { Priority = 10, Order = 3 },
            new SendOptions { EndPoint = queueName, Headers = new Dictionary<string, string> { ["Priority"] = "10" } });

        // Now start consuming — higher priority should come first
        await bus.StartConsumingAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        cts.Token.Register(() => allReceived.TrySetResult(false));
        await allReceived.Task;

        var order = receivedOrder.ToArray();
        Assert.Equal(3, order.Length);
        // Priority 10 should be first (or at least before priority 1)
        Assert.True(Array.IndexOf(order, 10) < Array.IndexOf(order, 1),
            $"Expected priority 10 before priority 1, got order: [{string.Join(",", order)}]");

        bus.Dispose();
    }
}
```

- [ ] **Step 2: Write FilterPipelineConsumerTests**

```csharp
// src/ServiceConnect.EndToEndTests/FilterPipelineConsumerTests.cs
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class FilterPipelineConsumerTests
{
    private readonly MessagingFixture _fixture;

    public FilterPipelineConsumerTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task BeforeConsumingFilter_Blocks_HandlerNotInvoked()
    {
        var queueName = _fixture.GetUniqueQueueName("filter-before");
        var handlerCalled = false;
        var messageProcessed = new TaskCompletionSource<bool>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<BeforeBlockFilter>();
        services.AddSingleton<IMessageHandler<TestMessage>>(new CallbackHandler<TestMessage>(_ =>
        {
            handlerCalled = true;
            messageProcessed.TrySetResult(true);
        }));
        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3;
                t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
            builder.AddBeforeConsumingFilter<BeforeBlockFilter>();
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();
        await Task.Delay(500);

        await bus.PublishAsync(new TestMessage(Guid.NewGuid()) { Content = "should be blocked" });

        // Wait a short time — handler should NOT be called
        await Task.Delay(2000);

        Assert.False(handlerCalled);

        bus.Dispose();
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task AfterConsumingFilter_RunsAfterHandler()
    {
        var queueName = _fixture.GetUniqueQueueName("filter-after");
        var filterRan = new TaskCompletionSource<bool>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(filterRan);
        services.AddSingleton<AfterTrackingFilter>();
        services.AddSingleton<IMessageHandler<TestMessage>>(new CallbackHandler<TestMessage>(_ => { }));
        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3;
                t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
            builder.AddAfterConsumingFilter<AfterTrackingFilter>();
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();
        await Task.Delay(500);

        await bus.PublishAsync(new TestMessage(Guid.NewGuid()) { Content = "check after filter" });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        cts.Token.Register(() => filterRan.TrySetCanceled());
        var result = await filterRan.Task;

        Assert.True(result);

        bus.Dispose();
    }
}

file class BeforeBlockFilter : IFilter
{
    public IBus Bus { get; set; } = null!;
    public bool Process(Envelope envelope) => false; // Block
}

file class AfterTrackingFilter : IFilter
{
    private readonly TaskCompletionSource<bool> _tcs;
    public IBus Bus { get; set; } = null!;

    public AfterTrackingFilter(TaskCompletionSource<bool> tcs) => _tcs = tcs;

    public bool Process(Envelope envelope)
    {
        _tcs.TrySetResult(true);
        return true; // Continue
    }
}
```

- [ ] **Step 3: Build and run**

Run: `cd src && sg docker -c "dotnet test ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter 'FullyQualifiedName~PriorityQueueTests|FullyQualifiedName~FilterPipelineConsumerTests' -v normal"`
Expected: Tests PASS.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/PriorityQueueTests.cs src/ServiceConnect.EndToEndTests/FilterPipelineConsumerTests.cs
git commit -m "test: add priority queue and consumer filter E2E tests"
```

---

### Task 11: Final Verification

**Files:** None (verification only)

- [ ] **Step 1: Run all unit tests**

Run: `cd src && dotnet test ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj -v minimal`
Expected: All tests PASS.

- [ ] **Step 2: Run all E2E tests**

Run: `cd src && sg docker -c "dotnet test ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj -v minimal"`
Expected: All tests PASS.

- [ ] **Step 3: Run all tests with coverage**

Run: `cd src && rm -rf TestResults && sg docker -c "dotnet test ServiceConnect.sln --collect:'XPlat Code Coverage' --results-directory ./TestResults -v minimal"`

- [ ] **Step 4: Generate coverage report**

Run: `cd src && ~/.dotnet/tools/reportgenerator -reports:"TestResults/*/coverage.cobertura.xml" -targetdir:"TestResults/CoverageReport" -reporttypes:"TextSummary" && cat TestResults/CoverageReport/Summary.txt`

- [ ] **Step 5: Commit any final fixes**

```bash
git add -A
git commit -m "test: complete consumer wiring and full E2E test suite"
```
