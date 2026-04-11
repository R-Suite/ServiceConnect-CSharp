# Dispatcher Features Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement chain-of-responsibility dispatcher with Process Manager, Aggregator, and Streaming support — including E2E tests with both InMemory and MongoDB persistence.

**Architecture:** Refactor `MessageDispatcher` to delegate to ordered `IMessageProcessor` chain: ReplyProcessor → StreamProcessor → ProcessManagerProcessor → AggregatorProcessor → HandlerProcessor. Each processor is self-contained and independently testable. New `IProcessHandler<TData, TMessage>` interface for process managers, existing `Aggregator<T>` for aggregation, and `IStreamHandler<T>` + write/read stream implementations for streaming.

**Tech Stack:** .NET 10, xUnit 2.9.2, Moq 4.20.72, Testcontainers (RabbitMQ + MongoDB), ServiceConnect.Client.RabbitMQ

**Spec:** `docs/superpowers/specs/2026-04-11-dispatcher-features-design.md`

---

## File Structure

| File | Responsibility |
|------|---------------|
| `src/ServiceConnect.Interfaces/IMessageProcessor.cs` | Interface + ProcessResult enum |
| `src/ServiceConnect.Interfaces/IProcessHandler.cs` | Process handler interface with ConfigureMapper |
| `src/ServiceConnect/Services/Processors/ReplyProcessor.cs` | Route replies to RequestReplyManager |
| `src/ServiceConnect/Services/Processors/HandlerProcessor.cs` | Existing IMessageHandler<T> dispatch (extracted) |
| `src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs` | Correlation-based stateful workflow dispatch |
| `src/ServiceConnect/Services/Processors/AggregatorProcessor.cs` | Batch message collection with timer |
| `src/ServiceConnect/Services/Processors/StreamProcessor.cs` | Chunked byte stream reassembly |
| `src/ServiceConnect/Services/MessageBusWriteStream.cs` | Write-side stream chunking |
| `src/ServiceConnect/Services/MessageBusReadStream.cs` | Read-side packet reassembly |
| `src/ServiceConnect/Services/MessageDispatcher.cs` | Refactored to use processor chain |
| `src/ServiceConnect/ServiceCollectionExtensions.cs` | Register processors, extend scanner |
| `src/ServiceConnect/Services/HandlerScanner.cs` | Scan for IProcessHandler, Aggregator, IStreamHandler |
| `src/ServiceConnect/Bus.cs` | Implement CreateStream<T> |
| `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceExtensions.cs` | UseInMemoryPersistence builder method |
| `src/ServiceConnect.EndToEndTests/ProcessManagerTests.cs` | InMemory E2E |
| `src/ServiceConnect.EndToEndTests/ProcessManagerMongoDbTests.cs` | MongoDB E2E |
| `src/ServiceConnect.EndToEndTests/AggregatorTests.cs` | InMemory E2E (batch + timeout) |
| `src/ServiceConnect.EndToEndTests/AggregatorMongoDbTests.cs` | MongoDB E2E |
| `src/ServiceConnect.EndToEndTests/StreamingTests.cs` | Streaming E2E |
| `src/ServiceConnect.UnitTests/Processors/` | Unit tests per processor |

---

### Task 1: IMessageProcessor Interface + ProcessResult Enum

**Files:**
- Create: `src/ServiceConnect.Interfaces/IMessageProcessor.cs`

- [ ] **Step 1: Create the interface file**

```csharp
namespace ServiceConnect.Interfaces;

public enum ProcessResult { Handled, NotHandled }

public interface IMessageProcessor
{
    Task<ProcessResult> ProcessAsync(
        byte[] messageBytes,
        Type messageType,
        object? message,
        IDictionary<string, object> headers,
        Envelope envelope);
}
```

Note: `message` is nullable because `ReplyProcessor` operates before deserialization (passes null).

- [ ] **Step 2: Verify build**

Run: `dotnet build src/ServiceConnect.Interfaces -v q`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.Interfaces/IMessageProcessor.cs
git commit -m "feat: add IMessageProcessor interface and ProcessResult enum"
```

---

### Task 2: IProcessHandler Interface

**Files:**
- Create: `src/ServiceConnect.Interfaces/IProcessHandler.cs`

- [ ] **Step 1: Create the interface file**

```csharp
using System.Linq.Expressions;

namespace ServiceConnect.Interfaces;

public interface IProcessHandler<TData, TMessage>
    where TData : class, IProcessManagerData, new()
    where TMessage : Message
{
    IConsumeContext? Context { get; set; }
    Task HandleAsync(TMessage message, TData data);

    void ConfigureMapper(IProcessManagerPropertyMapper mapper)
    {
        mapper.ConfigureMapping<TData, TMessage>(d => d.CorrelationId, m => m.CorrelationId);
    }
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build src/ServiceConnect.Interfaces -v q`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.Interfaces/IProcessHandler.cs
git commit -m "feat: add IProcessHandler interface for process manager dispatch"
```

---

### Task 3: ReplyProcessor (extract from MessageDispatcher)

**Files:**
- Create: `src/ServiceConnect/Services/Processors/ReplyProcessor.cs`
- Test: `src/ServiceConnect.UnitTests/Processors/ReplyProcessorTests.cs`

- [ ] **Step 1: Write the unit test**

```csharp
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class ReplyProcessorTests
{
    private readonly Mock<IRequestReplyManager> _mockReplyManager = new();
    private readonly Mock<IMessageSerializer> _mockSerializer = new();

    [Fact]
    public async Task ProcessAsync_WithResponseMessageId_RoutesToReplyManager()
    {
        var processor = new ReplyProcessor(_mockReplyManager.Object, _mockSerializer.Object);
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.ResponseMessageId] = "reply-123",
            [HeaderKeys.FullTypeName] = typeof(TestMsg).AssemblyQualifiedName!
        };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(TestMsg), null, headers, envelope);

        Assert.Equal(ProcessResult.Handled, result);
        _mockReplyManager.Verify(r => r.ProcessReply("reply-123", It.IsAny<byte[]>(), typeof(TestMsg)), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_WithoutResponseMessageId_ReturnsNotHandled()
    {
        var processor = new ReplyProcessor(_mockReplyManager.Object, _mockSerializer.Object);
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(TestMsg), null, headers, envelope);

        Assert.Equal(ProcessResult.NotHandled, result);
    }
}

file class TestMsg : Message
{
    public TestMsg(Guid correlationId) : base(correlationId) { }
}
```

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~ReplyProcessorTests" -v n`
Expected: FAIL (type not found)

- [ ] **Step 3: Create ReplyProcessor**

```csharp
using System.Text;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

public class ReplyProcessor : IMessageProcessor
{
    private readonly IRequestReplyManager _replyManager;
    private readonly IMessageSerializer _serializer;

    public ReplyProcessor(IRequestReplyManager replyManager, IMessageSerializer serializer)
    {
        _replyManager = replyManager;
        _serializer = serializer;
    }

    public Task<ProcessResult> ProcessAsync(
        byte[] messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope)
    {
        if (!headers.TryGetValue(HeaderKeys.ResponseMessageId, out var responseMessageIdRaw))
            return Task.FromResult(ProcessResult.NotHandled);

        var responseMessageId = responseMessageIdRaw is byte[] bytes
            ? Encoding.UTF8.GetString(bytes)
            : responseMessageIdRaw?.ToString();

        if (string.IsNullOrEmpty(responseMessageId))
            return Task.FromResult(ProcessResult.NotHandled);

        _replyManager.ProcessReply(responseMessageId, messageBytes, messageType);
        return Task.FromResult(ProcessResult.Handled);
    }
}
```

- [ ] **Step 4: Run tests, verify they pass**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~ReplyProcessorTests" -v n`
Expected: 2 tests PASS

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/Services/Processors/ReplyProcessor.cs src/ServiceConnect.UnitTests/Processors/ReplyProcessorTests.cs
git commit -m "feat: extract ReplyProcessor from MessageDispatcher"
```

---

### Task 4: HandlerProcessor (extract from MessageDispatcher)

**Files:**
- Create: `src/ServiceConnect/Services/Processors/HandlerProcessor.cs`
- Test: `src/ServiceConnect.UnitTests/Processors/HandlerProcessorTests.cs`

- [ ] **Step 1: Write the unit test**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class HandlerProcessorTests
{
    [Fact]
    public async Task ProcessAsync_WithRegisteredHandler_InvokesHandler()
    {
        var handler = new TestHandler();
        var mockBus = new Mock<IBus>();
        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<TestHandlerMsg>>(handler);
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        var processor = new HandlerProcessor(provider, new Mock<ILogger<HandlerProcessor>>().Object);
        var msg = new TestHandlerMsg(Guid.NewGuid());
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(TestHandlerMsg), msg, headers, envelope);

        Assert.Equal(ProcessResult.Handled, result);
        Assert.True(handler.Invoked);
    }

    [Fact]
    public async Task ProcessAsync_NoHandlers_ReturnsNotHandled()
    {
        var mockBus = new Mock<IBus>();
        var services = new ServiceCollection();
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        var processor = new HandlerProcessor(provider, new Mock<ILogger<HandlerProcessor>>().Object);
        var msg = new TestHandlerMsg(Guid.NewGuid());
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(TestHandlerMsg), msg, headers, envelope);

        Assert.Equal(ProcessResult.NotHandled, result);
    }
}

file class TestHandlerMsg : Message
{
    public TestHandlerMsg(Guid correlationId) : base(correlationId) { }
}

file class TestHandler : IMessageHandler<TestHandlerMsg>
{
    public bool Invoked { get; private set; }
    public IConsumeContext? Context { get; set; }
    public Task HandleAsync(TestHandlerMsg message) { Invoked = true; return Task.CompletedTask; }
}
```

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~HandlerProcessorTests" -v n`
Expected: FAIL

- [ ] **Step 3: Create HandlerProcessor**

This extracts the handler resolution + type hierarchy walking + dispatch logic from `MessageDispatcher`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

public class HandlerProcessor : IMessageProcessor
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<HandlerProcessor> _logger;

    public HandlerProcessor(IServiceProvider serviceProvider, ILogger<HandlerProcessor> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<ProcessResult> ProcessAsync(
        byte[] messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope)
    {
        if (message == null) return ProcessResult.NotHandled;

        // Resolve handlers — walk up base types, stop before Message and object
        var allHandlers = new List<(object Handler, Type InterfaceType)>();
        var checkedType = messageType;
        while (checkedType != null && checkedType != typeof(Message) && checkedType != typeof(object))
        {
            var handlerInterfaceType = typeof(IMessageHandler<>).MakeGenericType(checkedType);
            var handlers = _serviceProvider.GetServices(handlerInterfaceType);
            foreach (var h in handlers)
            {
                if (h != null)
                    allHandlers.Add((h, handlerInterfaceType));
            }
            checkedType = checkedType.BaseType;
        }

        if (allHandlers.Count == 0)
            return ProcessResult.NotHandled;

        var bus = _serviceProvider.GetRequiredService<IBus>();
        var context = new ConsumeContext(bus, headers);

        foreach (var (handler, resolvedInterface) in allHandlers)
        {
            var contextProperty = resolvedInterface.GetProperty("Context");
            var handleAsyncMethod = resolvedInterface.GetMethod("HandleAsync");

            contextProperty?.SetValue(handler, context);

            var task = (Task?)handleAsyncMethod?.Invoke(handler, new[] { message });
            if (task != null)
                await task;
        }

        return ProcessResult.Handled;
    }
}
```

- [ ] **Step 4: Run tests, verify they pass**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~HandlerProcessorTests" -v n`
Expected: 2 tests PASS

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/Services/Processors/HandlerProcessor.cs src/ServiceConnect.UnitTests/Processors/HandlerProcessorTests.cs
git commit -m "feat: extract HandlerProcessor from MessageDispatcher"
```

---

### Task 5: StreamProcessor + MessageBusWriteStream + MessageBusReadStream

**Files:**
- Create: `src/ServiceConnect/Services/MessageBusReadStream.cs`
- Create: `src/ServiceConnect/Services/MessageBusWriteStream.cs`
- Create: `src/ServiceConnect/Services/Processors/StreamProcessor.cs`
- Test: `src/ServiceConnect.UnitTests/Processors/StreamProcessorTests.cs`
- Test: `src/ServiceConnect.UnitTests/MessageBusReadStreamTests.cs`

- [ ] **Step 1: Create MessageBusReadStream**

```csharp
using System.Collections.Concurrent;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

public class MessageBusReadStream : IMessageBusReadStream
{
    private readonly ConcurrentDictionary<long, byte[]> _packets = new();

    public string SequenceId { get; set; } = string.Empty;
    public long LastPacketNumber { get; set; } = -1;
    public MessageBusStreamComplete CompleteEventHandler { get; set; } = null!;
    public int HandlerCount { get; set; }

    public void Write(byte[] data, long packetNumber)
    {
        _packets[packetNumber] = data;
    }

    public byte[] Read()
    {
        if (!IsComplete())
            throw new InvalidOperationException("Stream is not yet complete.");

        using var ms = new MemoryStream();
        for (long i = 0; i <= LastPacketNumber; i++)
        {
            if (_packets.TryGetValue(i, out var packet))
                ms.Write(packet, 0, packet.Length);
        }
        return ms.ToArray();
    }

    public bool IsComplete()
    {
        if (LastPacketNumber < 0) return false;
        for (long i = 0; i <= LastPacketNumber; i++)
        {
            if (!_packets.ContainsKey(i)) return false;
        }
        return true;
    }
}
```

- [ ] **Step 2: Write ReadStream unit tests**

```csharp
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MessageBusReadStreamTests
{
    [Fact]
    public void Write_And_Read_ReassemblesPacketsInOrder()
    {
        var stream = new MessageBusReadStream { LastPacketNumber = 2 };
        stream.Write(new byte[] { 1, 2 }, 0);
        stream.Write(new byte[] { 5, 6 }, 2);
        stream.Write(new byte[] { 3, 4 }, 1);

        Assert.True(stream.IsComplete());
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, stream.Read());
    }

    [Fact]
    public void IsComplete_MissingPacket_ReturnsFalse()
    {
        var stream = new MessageBusReadStream { LastPacketNumber = 2 };
        stream.Write(new byte[] { 1 }, 0);
        stream.Write(new byte[] { 3 }, 2);

        Assert.False(stream.IsComplete());
    }

    [Fact]
    public void IsComplete_NoLastPacketNumber_ReturnsFalse()
    {
        var stream = new MessageBusReadStream();
        stream.Write(new byte[] { 1 }, 0);

        Assert.False(stream.IsComplete());
    }
}
```

- [ ] **Step 3: Create MessageBusWriteStream**

```csharp
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

public class MessageBusWriteStream : IMessageBusWriteStream
{
    private readonly IProducer _producer;
    private readonly string _endpoint;
    private readonly string _sequenceId;
    private readonly Dictionary<string, string> _baseHeaders;
    private long _packetNumber;
    private bool _closed;

    public MessageBusWriteStream(IProducer producer, string endpoint, Type messageType)
    {
        _producer = producer;
        _endpoint = endpoint;
        _sequenceId = Guid.NewGuid().ToString();
        _baseHeaders = new Dictionary<string, string>
        {
            [HeaderKeys.SequenceId] = _sequenceId,
            [HeaderKeys.FullTypeName] = messageType.AssemblyQualifiedName!,
            [HeaderKeys.TypeName] = messageType.FullName!,
            [HeaderKeys.MessageType] = "ByteStream"
        };
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        if (_closed) throw new ObjectDisposedException(nameof(MessageBusWriteStream));

        var packet = new byte[count];
        Array.Copy(buffer, offset, packet, 0, count);

        var headers = new Dictionary<string, string>(_baseHeaders)
        {
            [HeaderKeys.PacketNumber] = _packetNumber.ToString()
        };

        _producer.SendBytesAsync(_endpoint, packet, headers).GetAwaiter().GetResult();
        _packetNumber++;
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;

        var headers = new Dictionary<string, string>(_baseHeaders)
        {
            [HeaderKeys.PacketNumber] = _packetNumber.ToString(),
            [HeaderKeys.LastPacketNumber] = _packetNumber.ToString()
        };

        _producer.SendBytesAsync(_endpoint, Array.Empty<byte>(), headers).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Close();
    }
}
```

- [ ] **Step 4: Create StreamProcessor**

```csharp
using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

public class StreamProcessor : IMessageProcessor
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StreamProcessor> _logger;
    private readonly ConcurrentDictionary<string, MessageBusReadStream> _activeStreams = new();

    public StreamProcessor(IServiceProvider serviceProvider, ILogger<StreamProcessor> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<ProcessResult> ProcessAsync(
        byte[] messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope)
    {
        // Check if this is a ByteStream message
        if (!headers.TryGetValue(HeaderKeys.MessageType, out var msgTypeRaw))
            return ProcessResult.NotHandled;

        var msgType = msgTypeRaw is byte[] mtBytes ? Encoding.UTF8.GetString(mtBytes) : msgTypeRaw?.ToString();
        if (msgType != "ByteStream")
            return ProcessResult.NotHandled;

        // Extract required headers
        if (!headers.TryGetValue(HeaderKeys.SequenceId, out var seqIdRaw))
            return ProcessResult.NotHandled;
        var sequenceId = seqIdRaw is byte[] siBytes ? Encoding.UTF8.GetString(siBytes) : seqIdRaw?.ToString()!;

        if (!headers.TryGetValue(HeaderKeys.PacketNumber, out var pnRaw))
            return ProcessResult.NotHandled;
        var packetNumber = long.Parse(pnRaw is byte[] pnBytes ? Encoding.UTF8.GetString(pnBytes) : pnRaw?.ToString()!);

        // Get or create the read stream
        var stream = _activeStreams.GetOrAdd(sequenceId, _ => new MessageBusReadStream { SequenceId = sequenceId });

        // Write the packet
        stream.Write(messageBytes, packetNumber);

        // Check for LastPacketNumber header (final packet marker)
        if (headers.TryGetValue(HeaderKeys.LastPacketNumber, out var lpnRaw))
        {
            var lastPacketNumber = long.Parse(lpnRaw is byte[] lpnBytes ? Encoding.UTF8.GetString(lpnBytes) : lpnRaw?.ToString()!);
            stream.LastPacketNumber = lastPacketNumber;
        }

        // If stream is complete, dispatch to handler
        if (stream.IsComplete())
        {
            _activeStreams.TryRemove(sequenceId, out _);

            // Resolve the actual message type from FullTypeName
            if (!headers.TryGetValue(HeaderKeys.FullTypeName, out var ftnRaw))
            {
                _logger.LogWarning("Completed stream {SequenceId} missing FullTypeName header", sequenceId);
                return ProcessResult.Handled;
            }

            var fullTypeName = ftnRaw is byte[] ftnBytes ? Encoding.UTF8.GetString(ftnBytes) : ftnRaw?.ToString();
            var resolvedType = Type.GetType(fullTypeName!);
            if (resolvedType == null)
            {
                _logger.LogWarning("Cannot resolve type {TypeName} for completed stream", fullTypeName);
                return ProcessResult.Handled;
            }

            var handlerType = typeof(IStreamHandler<>).MakeGenericType(resolvedType);
            var handler = _serviceProvider.GetService(handlerType);
            if (handler == null)
            {
                _logger.LogWarning("No IStreamHandler registered for {MessageType}", resolvedType.FullName);
                return ProcessResult.Handled;
            }

            // Set Stream property and call Execute
            var streamProp = handlerType.GetProperty("Stream");
            streamProp?.SetValue(handler, stream);

            // Deserialize the original message from the assembled bytes for Execute
            var serializer = _serviceProvider.GetRequiredService<IMessageSerializer>();
            var assembledBytes = stream.Read();
            var originalMessage = serializer.Deserialize(assembledBytes, resolvedType);

            var executeMethod = handlerType.GetMethod("Execute");
            executeMethod?.Invoke(handler, new[] { originalMessage });
        }

        return ProcessResult.Handled;
    }
}
```

- [ ] **Step 5: Write StreamProcessor unit test**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class StreamProcessorTests
{
    [Fact]
    public async Task ProcessAsync_NonByteStream_ReturnsNotHandled()
    {
        var services = new ServiceCollection();
        var provider = services.BuildServiceProvider();
        var processor = new StreamProcessor(provider, new Mock<ILogger<StreamProcessor>>().Object);

        var headers = new Dictionary<string, object> { [HeaderKeys.MessageType] = "Send" };
        var envelope = new Envelope { Headers = headers, Body = Array.Empty<byte>() };

        var result = await processor.ProcessAsync(Array.Empty<byte>(), typeof(object), null, headers, envelope);

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_ByteStreamPacket_ReturnsHandled()
    {
        var services = new ServiceCollection();
        var provider = services.BuildServiceProvider();
        var processor = new StreamProcessor(provider, new Mock<ILogger<StreamProcessor>>().Object);

        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.MessageType] = "ByteStream",
            [HeaderKeys.SequenceId] = "seq-1",
            [HeaderKeys.PacketNumber] = "0"
        };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(object), null, headers, envelope);

        Assert.Equal(ProcessResult.Handled, result);
    }
}
```

- [ ] **Step 6: Run all new tests**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~StreamProcessor|FullyQualifiedName~MessageBusReadStream" -v n`
Expected: All tests PASS

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect/Services/MessageBusReadStream.cs src/ServiceConnect/Services/MessageBusWriteStream.cs src/ServiceConnect/Services/Processors/StreamProcessor.cs src/ServiceConnect.UnitTests/Processors/StreamProcessorTests.cs src/ServiceConnect.UnitTests/MessageBusReadStreamTests.cs
git commit -m "feat: add streaming support - WriteStream, ReadStream, StreamProcessor"
```

---

### Task 6: ProcessManagerProcessor

**Files:**
- Create: `src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs`
- Test: `src/ServiceConnect.UnitTests/Processors/ProcessManagerProcessorTests.cs`

- [ ] **Step 1: Write the unit test**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class ProcessManagerProcessorTests
{
    [Fact]
    public async Task ProcessAsync_NoProcessHandler_ReturnsNotHandled()
    {
        var services = new ServiceCollection();
        var provider = services.BuildServiceProvider();
        var processor = new ProcessManagerProcessor(provider, new Mock<ILogger<ProcessManagerProcessor>>().Object);

        var msg = new PmTestMessage(Guid.NewGuid());
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), msg, headers, envelope);

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_WithProcessHandler_NewState_InsertsData()
    {
        var mockFinder = new Mock<IProcessManagerFinder>();
        mockFinder.Setup(f => f.FindData<PmTestData>(It.IsAny<IProcessManagerPropertyMapper>(), It.IsAny<Message>()))
            .Returns((IPersistenceData<PmTestData>?)null);

        var handler = new PmTestHandler();
        var mockBus = new Mock<IBus>();

        var services = new ServiceCollection();
        services.AddSingleton<IProcessHandler<PmTestData, PmTestMessage>>(handler);
        services.AddSingleton<IProcessManagerFinder>(mockFinder.Object);
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        var processor = new ProcessManagerProcessor(provider, new Mock<ILogger<ProcessManagerProcessor>>().Object);
        var correlationId = Guid.NewGuid();
        var msg = new PmTestMessage(correlationId) { Content = "test" };
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), msg, headers, envelope);

        Assert.Equal(ProcessResult.Handled, result);
        Assert.True(handler.Invoked);
        mockFinder.Verify(f => f.InsertData(It.Is<IProcessManagerData>(d => d.CorrelationId == correlationId)), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_WithProcessHandler_ExistingState_UpdatesData()
    {
        var existingData = new PmTestData { CorrelationId = Guid.NewGuid(), Counter = 5 };
        var persistenceData = new Mock<IPersistenceData<PmTestData>>();
        persistenceData.Setup(p => p.Data).Returns(existingData);

        var mockFinder = new Mock<IProcessManagerFinder>();
        mockFinder.Setup(f => f.FindData<PmTestData>(It.IsAny<IProcessManagerPropertyMapper>(), It.IsAny<Message>()))
            .Returns(persistenceData.Object);

        var handler = new PmTestHandler();
        var mockBus = new Mock<IBus>();

        var services = new ServiceCollection();
        services.AddSingleton<IProcessHandler<PmTestData, PmTestMessage>>(handler);
        services.AddSingleton<IProcessManagerFinder>(mockFinder.Object);
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        var processor = new ProcessManagerProcessor(provider, new Mock<ILogger<ProcessManagerProcessor>>().Object);
        var msg = new PmTestMessage(existingData.CorrelationId) { Content = "update" };
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), msg, headers, envelope);

        Assert.Equal(ProcessResult.Handled, result);
        Assert.True(handler.Invoked);
        mockFinder.Verify(f => f.UpdateData(persistenceData.Object), Times.Once);
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

file class PmTestHandler : IProcessHandler<PmTestData, PmTestMessage>
{
    public bool Invoked { get; private set; }
    public IConsumeContext? Context { get; set; }

    public Task HandleAsync(PmTestMessage message, PmTestData data)
    {
        Invoked = true;
        data.Counter++;
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~ProcessManagerProcessorTests" -v n`
Expected: FAIL

- [ ] **Step 3: Create ProcessManagerProcessor**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

public class ProcessManagerProcessor : IMessageProcessor
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ProcessManagerProcessor> _logger;

    public ProcessManagerProcessor(IServiceProvider serviceProvider, ILogger<ProcessManagerProcessor> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<ProcessResult> ProcessAsync(
        byte[] messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope)
    {
        if (message == null) return ProcessResult.NotHandled;

        // Look for IProcessHandler<TData, TMessage> registrations via reflection
        // We need to scan all registered services for IProcessHandler<,> matching messageType
        var processHandlerType = FindProcessHandlerType(messageType);
        if (processHandlerType == null)
            return ProcessResult.NotHandled;

        var finder = _serviceProvider.GetService<IProcessManagerFinder>();
        if (finder == null)
        {
            _logger.LogWarning("IProcessManagerFinder not registered. Cannot process process manager message {MessageType}", messageType.FullName);
            return ProcessResult.NotHandled;
        }

        // Get TData and TMessage from the generic interface
        var genericArgs = processHandlerType.GetGenericArguments();
        var dataType = genericArgs[0]; // TData
        // genericArgs[1] is TMessage

        // Resolve the handler
        var handler = _serviceProvider.GetService(processHandlerType);
        if (handler == null) return ProcessResult.NotHandled;

        // Configure mapper
        var mapper = new DefaultProcessManagerPropertyMapper();
        var configureMapperMethod = processHandlerType.GetMethod("ConfigureMapper");
        configureMapperMethod?.Invoke(handler, new object[] { mapper });

        // Find existing data
        var findDataMethod = typeof(IProcessManagerFinder).GetMethod("FindData")!.MakeGenericMethod(dataType);
        var persistenceData = findDataMethod.Invoke(finder, new object[] { mapper, (Message)message });

        bool isNew = persistenceData == null;
        object data;

        if (isNew)
        {
            data = Activator.CreateInstance(dataType)!;
            var correlationProp = dataType.GetProperty("CorrelationId");
            correlationProp?.SetValue(data, ((Message)message).CorrelationId);
        }
        else
        {
            var dataProp = persistenceData!.GetType().GetProperty("Data");
            data = dataProp!.GetValue(persistenceData)!;
        }

        // Set context
        var bus = _serviceProvider.GetRequiredService<IBus>();
        var context = new ConsumeContext(bus, headers);
        var contextProp = processHandlerType.GetProperty("Context");
        contextProp?.SetValue(handler, context);

        // Invoke HandleAsync
        var handleAsyncMethod = processHandlerType.GetMethod("HandleAsync");
        var task = (Task?)handleAsyncMethod?.Invoke(handler, new[] { message, data });
        if (task != null) await task;

        // Persist
        if (isNew)
        {
            finder.InsertData((IProcessManagerData)data);
        }
        else
        {
            var updateMethod = typeof(IProcessManagerFinder).GetMethod("UpdateData")!.MakeGenericMethod(dataType);
            updateMethod.Invoke(finder, new[] { persistenceData });
        }

        return ProcessResult.Handled;
    }

    private Type? FindProcessHandlerType(Type messageType)
    {
        // Check if IProcessHandler<TData, messageType> is registered for any TData
        var checkedType = messageType;
        while (checkedType != null && checkedType != typeof(Message) && checkedType != typeof(object))
        {
            // We can't enumerate all possible TData types, so we scan registered services
            // by checking all interfaces of all registered service types
            // Simpler approach: try to resolve IProcessHandler<,> where TMessage = checkedType
            // Since we don't know TData, iterate service descriptors
            var found = FindHandlerForMessageType(checkedType);
            if (found != null) return found;
            checkedType = checkedType.BaseType;
        }
        return null;
    }

    private Type? FindHandlerForMessageType(Type messageType)
    {
        // Scan service provider for any IProcessHandler<TData, TMessage> where TMessage matches
        // We use the service collection approach - try to get services for all known data types
        // Since we can't enumerate generics directly, we inspect the DI container
        if (_serviceProvider is IServiceProvider sp)
        {
            // Try to find via service descriptors if available, or use a different approach
            // For now, use the registered handler references to find process handler types
            try
            {
                var handlerRefs = sp.GetService<IList<HandlerReference>>();
                if (handlerRefs != null)
                {
                    foreach (var href in handlerRefs)
                    {
                        if (href.MessageType != messageType) continue;
                        var handlerInterfaces = href.HandlerType.GetInterfaces();
                        foreach (var iface in handlerInterfaces)
                        {
                            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IProcessHandler<,>))
                            {
                                var args = iface.GetGenericArguments();
                                if (args[1] == messageType)
                                    return iface;
                            }
                        }
                    }
                }
            }
            catch { /* ignore */ }
        }
        return null;
    }
}

/// <summary>
/// Default implementation of IProcessManagerPropertyMapper for use by ProcessManagerProcessor.
/// </summary>
file class DefaultProcessManagerPropertyMapper : IProcessManagerPropertyMapper
{
    public List<ProcessManagerToMessageMap> Mappings { get; set; } = new();

    public void ConfigureMapping<TProcessManagerData, TMessage>(
        System.Linq.Expressions.Expression<Func<TProcessManagerData, object>> processManagerProperty,
        System.Linq.Expressions.Expression<Func<TMessage, object>> messageExpression)
        where TProcessManagerData : IProcessManagerData
    {
        var map = new ProcessManagerToMessageMap
        {
            MessageType = typeof(TMessage),
            PropertiesHierarchy = new Dictionary<string, Type>(),
            MessageProp = BuildMessageFunc(messageExpression)
        };

        var body = processManagerProperty.Body;
        if (body is System.Linq.Expressions.UnaryExpression unary) body = unary.Operand;
        if (body is System.Linq.Expressions.MemberExpression member)
        {
            var propInfo = (System.Reflection.PropertyInfo)member.Member;
            map.PropertiesHierarchy[propInfo.Name] = propInfo.PropertyType;
        }

        Mappings.Add(map);
    }

    private static Func<object, object> BuildMessageFunc<TMessage>(
        System.Linq.Expressions.Expression<Func<TMessage, object>> messageExpression)
    {
        var compiled = messageExpression.Compile();
        return obj => compiled((TMessage)obj);
    }
}
```

- [ ] **Step 4: Run tests, verify they pass**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~ProcessManagerProcessorTests" -v n`
Expected: 3 tests PASS

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/Services/Processors/ProcessManagerProcessor.cs src/ServiceConnect.UnitTests/Processors/ProcessManagerProcessorTests.cs
git commit -m "feat: add ProcessManagerProcessor for stateful workflow dispatch"
```

---

### Task 7: AggregatorProcessor

**Files:**
- Create: `src/ServiceConnect/Services/Processors/AggregatorProcessor.cs`
- Test: `src/ServiceConnect.UnitTests/Processors/AggregatorProcessorTests.cs`

- [ ] **Step 1: Write the unit test**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class AggregatorProcessorTests
{
    [Fact]
    public async Task ProcessAsync_NoAggregator_ReturnsNotHandled()
    {
        var services = new ServiceCollection();
        var provider = services.BuildServiceProvider();
        var processor = new AggregatorProcessor(provider, new Mock<ILogger<AggregatorProcessor>>().Object);

        var msg = new AggTestMessage(Guid.NewGuid());
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(AggTestMessage), msg, headers, envelope);

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_BatchComplete_ExecutesAggregator()
    {
        var executed = new TaskCompletionSource<IList<AggTestMessage>>();
        var aggregator = new AggTestAggregator(executed);
        var mockPersistor = new Mock<IAggregatorPersistor>();

        // First two inserts: count returns 1, 2
        // Third insert: count returns 3 (== BatchSize)
        var insertCount = 0;
        mockPersistor.Setup(p => p.InsertData(It.IsAny<object>(), It.IsAny<string>()));
        mockPersistor.Setup(p => p.Count(It.IsAny<string>())).Returns(() => ++insertCount);

        var msg1 = new AggTestMessage(Guid.NewGuid()) { Value = "a" };
        var msg2 = new AggTestMessage(Guid.NewGuid()) { Value = "b" };
        var msg3 = new AggTestMessage(Guid.NewGuid()) { Value = "c" };
        mockPersistor.Setup(p => p.GetData(It.IsAny<string>()))
            .Returns(new List<object> { msg1, msg2, msg3 });

        var services = new ServiceCollection();
        services.AddSingleton<Aggregator<AggTestMessage>>(aggregator);
        services.AddSingleton<IAggregatorPersistor>(mockPersistor.Object);
        services.AddSingleton<IList<HandlerReference>>(new List<HandlerReference>
        {
            new() { HandlerType = typeof(AggTestAggregator), MessageType = typeof(AggTestMessage), RoutingKeys = new List<string>() }
        });
        var provider = services.BuildServiceProvider();

        var processor = new AggregatorProcessor(provider, new Mock<ILogger<AggregatorProcessor>>().Object);
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        // Send 3 messages
        await processor.ProcessAsync(new byte[] { 1 }, typeof(AggTestMessage), msg1, headers, envelope);
        await processor.ProcessAsync(new byte[] { 1 }, typeof(AggTestMessage), msg2, headers, envelope);
        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(AggTestMessage), msg3, headers, envelope);

        Assert.Equal(ProcessResult.Handled, result);

        // Wait for Execute to be called
        var messages = await executed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, messages.Count);

        processor.Dispose();
    }
}

file class AggTestMessage : Message
{
    public AggTestMessage(Guid correlationId) : base(correlationId) { }
    public string Value { get; set; } = string.Empty;
}

file class AggTestAggregator : Aggregator<AggTestMessage>
{
    private readonly TaskCompletionSource<IList<AggTestMessage>> _tcs;
    public AggTestAggregator(TaskCompletionSource<IList<AggTestMessage>> tcs) => _tcs = tcs;
    public override int BatchSize() => 3;
    public override TimeSpan Timeout() => TimeSpan.FromSeconds(30);
    public override void Execute(IList<AggTestMessage> messages) => _tcs.TrySetResult(messages);
}
```

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~AggregatorProcessorTests" -v n`
Expected: FAIL

- [ ] **Step 3: Create AggregatorProcessor**

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

public class AggregatorProcessor : IMessageProcessor, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AggregatorProcessor> _logger;
    private readonly ConcurrentDictionary<string, Timer> _timers = new();
    private readonly object _flushLock = new();

    public AggregatorProcessor(IServiceProvider serviceProvider, ILogger<AggregatorProcessor> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<ProcessResult> ProcessAsync(
        byte[] messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope)
    {
        if (message == null) return ProcessResult.NotHandled;

        // Find Aggregator<T> for this message type
        var aggregatorType = FindAggregatorType(messageType);
        if (aggregatorType == null)
            return ProcessResult.NotHandled;

        var persistor = _serviceProvider.GetService<IAggregatorPersistor>();
        if (persistor == null)
        {
            _logger.LogWarning("IAggregatorPersistor not registered. Cannot aggregate {MessageType}", messageType.FullName);
            return ProcessResult.NotHandled;
        }

        // Resolve the aggregator to get BatchSize and Timeout
        var aggregator = _serviceProvider.GetService(aggregatorType);
        if (aggregator == null) return ProcessResult.NotHandled;

        var aggregatorName = aggregatorType.FullName!;
        var batchSize = (int)aggregatorType.GetMethod("BatchSize")!.Invoke(aggregator, null)!;
        var timeout = (TimeSpan)aggregatorType.GetMethod("Timeout")!.Invoke(aggregator, null)!;

        // Persist the message
        persistor.InsertData(message, aggregatorName);

        // Check if batch is complete
        var count = persistor.Count(aggregatorName);
        if (batchSize > 0 && count >= batchSize)
        {
            FlushAggregator(aggregatorName, messageType, aggregatorType);
        }
        else if (timeout > TimeSpan.Zero)
        {
            // Start or reset timer
            if (_timers.TryRemove(aggregatorName, out var existingTimer))
                existingTimer.Dispose();

            var timer = new Timer(_ => OnTimerFired(aggregatorName, messageType, aggregatorType),
                null, timeout, System.Threading.Timeout.InfiniteTimeSpan);
            _timers[aggregatorName] = timer;
        }

        return ProcessResult.Handled;
    }

    private void OnTimerFired(string aggregatorName, Type messageType, Type aggregatorType)
    {
        try
        {
            FlushAggregator(aggregatorName, messageType, aggregatorType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error flushing aggregator {AggregatorName} on timeout", aggregatorName);
        }
        finally
        {
            if (_timers.TryRemove(aggregatorName, out var timer))
                timer.Dispose();
        }
    }

    private void FlushAggregator(string aggregatorName, Type messageType, Type aggregatorType)
    {
        lock (_flushLock)
        {
            var persistor = _serviceProvider.GetService<IAggregatorPersistor>();
            if (persistor == null) return;

            var rawMessages = persistor.GetData(aggregatorName);
            if (rawMessages.Count == 0) return;

            // Cast to IList<T> for the aggregator's Execute method
            var listType = typeof(List<>).MakeGenericType(messageType);
            var typedList = (System.Collections.IList)Activator.CreateInstance(listType)!;
            foreach (var msg in rawMessages)
                typedList.Add(msg);

            // Resolve aggregator and call Execute
            var aggregator = _serviceProvider.GetService(aggregatorType);
            if (aggregator == null) return;

            var executeMethod = aggregatorType.GetMethod("Execute");
            executeMethod?.Invoke(aggregator, new object[] { typedList });

            // Remove all processed messages
            foreach (var msg in rawMessages)
            {
                if (msg is Message m)
                    persistor.RemoveData(aggregatorName, m.CorrelationId);
            }
        }
    }

    private Type? FindAggregatorType(Type messageType)
    {
        var handlerRefs = _serviceProvider.GetService<IList<HandlerReference>>();
        if (handlerRefs == null) return null;

        foreach (var href in handlerRefs)
        {
            if (href.MessageType != messageType) continue;
            if (href.HandlerType.BaseType is { IsGenericType: true } baseType &&
                baseType.GetGenericTypeDefinition() == typeof(Aggregator<>))
            {
                return baseType;
            }
        }
        return null;
    }

    public void Dispose()
    {
        foreach (var kvp in _timers)
        {
            kvp.Value.Dispose();
        }
        _timers.Clear();
    }
}
```

- [ ] **Step 4: Run tests, verify they pass**

Run: `dotnet test src/ServiceConnect.UnitTests --filter "FullyQualifiedName~AggregatorProcessorTests" -v n`
Expected: 2 tests PASS

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/Services/Processors/AggregatorProcessor.cs src/ServiceConnect.UnitTests/Processors/AggregatorProcessorTests.cs
git commit -m "feat: add AggregatorProcessor for batch message collection"
```

---

### Task 8: Refactor MessageDispatcher to Use Processor Chain

**Files:**
- Modify: `src/ServiceConnect/Services/MessageDispatcher.cs`
- Modify: `src/ServiceConnect.UnitTests/MessageDispatcherTests.cs`

- [ ] **Step 1: Rewrite MessageDispatcher**

Replace the entire content of `src/ServiceConnect/Services/MessageDispatcher.cs`:

```csharp
using System.Text;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

public class MessageDispatcher
{
    private readonly IMessageSerializer _serializer;
    private readonly IFilterPipeline _filterPipeline;
    private readonly IList<IMessageProcessor> _processors;
    private readonly ILogger<MessageDispatcher> _logger;

    public MessageDispatcher(
        IMessageSerializer serializer,
        IFilterPipeline filterPipeline,
        IList<IMessageProcessor> processors,
        ILogger<MessageDispatcher> logger)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _filterPipeline = filterPipeline ?? throw new ArgumentNullException(nameof(filterPipeline));
        _processors = processors ?? throw new ArgumentNullException(nameof(processors));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ConsumeEventResult> Dispatch(byte[] messageBytes, string messageType, IDictionary<string, object> headers)
    {
        try
        {
            // 1. Resolve CLR Type from FullTypeName header
            if (!headers.TryGetValue(HeaderKeys.FullTypeName, out var fullTypeNameRaw))
                throw new InvalidOperationException("Message is missing FullTypeName header.");

            var fullTypeName = fullTypeNameRaw is byte[] bytes
                ? Encoding.UTF8.GetString(bytes)
                : fullTypeNameRaw?.ToString() ?? throw new InvalidOperationException("FullTypeName header is null.");

            var type = Type.GetType(fullTypeName)
                ?? throw new InvalidOperationException($"Cannot resolve type '{fullTypeName}'.");

            // 2. Build envelope
            var envelope = new Envelope { Headers = headers, Body = messageBytes };

            // 3. Run ReplyProcessor first (before deserialization) — it's the first in the chain
            // ReplyProcessor checks for ResponseMessageId and short-circuits
            if (_processors.Count > 0)
            {
                var replyResult = await _processors[0].ProcessAsync(messageBytes, type, null, headers, envelope);
                if (replyResult == ProcessResult.Handled)
                    return new ConsumeEventResult { Success = true };
            }

            // 4. Deserialize the message
            var message = _serializer.Deserialize(messageBytes, type);

            // 5. Run BeforeConsumingFilters
            bool blocked = _filterPipeline.ExecuteBeforeConsumingFilters(envelope);
            if (blocked)
                return new ConsumeEventResult { Success = true };

            // 6. Iterate remaining processors
            for (int i = 1; i < _processors.Count; i++)
            {
                var result = await _processors[i].ProcessAsync(messageBytes, type, message, headers, envelope);
                if (result == ProcessResult.Handled)
                {
                    // 7. Run AfterConsumingFilters
                    _filterPipeline.ExecuteAfterConsumingFilters(envelope);
                    return new ConsumeEventResult { Success = true };
                }
            }

            // No processor handled the message
            _logger.LogWarning("No processor handled message of type {MessageType}", type.FullName);

            // Still run AfterConsumingFilters
            _filterPipeline.ExecuteAfterConsumingFilters(envelope);

            return new ConsumeEventResult { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dispatching message of type {MessageType}", messageType);
            return new ConsumeEventResult { Success = false, Exception = ex };
        }
    }
}
```

- [ ] **Step 2: Update existing unit tests**

The existing `MessageDispatcherTests.cs` uses the old constructor. Update the test setup to use the new constructor with `IList<IMessageProcessor>`. You'll need to read the existing test file and update each test to construct a `MessageDispatcher` with the processor chain.

Key changes:
- Replace `new MessageDispatcher(serviceProvider, serializer, filterPipeline, replyManager, logger)` with `new MessageDispatcher(serializer, filterPipeline, processors, logger)`
- For tests that test reply routing, include a `ReplyProcessor` in the chain
- For tests that test handler dispatch, include a `HandlerProcessor` in the chain
- For tests that test filter blocking, include any processor in the chain

Read the existing tests, understand each one, and update accordingly. Run ALL existing tests after updating.

- [ ] **Step 3: Run all unit tests**

Run: `dotnet test src/ServiceConnect.UnitTests -v n`
Expected: ALL tests PASS (167+ tests)

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect/Services/MessageDispatcher.cs src/ServiceConnect.UnitTests/MessageDispatcherTests.cs
git commit -m "refactor: MessageDispatcher to use IMessageProcessor chain"
```

---

### Task 9: Update ServiceCollectionExtensions + HandlerScanner + Bus

**Files:**
- Modify: `src/ServiceConnect/ServiceCollectionExtensions.cs`
- Modify: `src/ServiceConnect/Services/HandlerScanner.cs`
- Modify: `src/ServiceConnect/Bus.cs`
- Create: `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceExtensions.cs`

- [ ] **Step 1: Update HandlerScanner to scan for all handler types**

Replace `src/ServiceConnect/Services/HandlerScanner.cs`:

```csharp
using System.Reflection;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

public static class HandlerScanner
{
    private static readonly Type[] HandlerGenericTypes = new[]
    {
        typeof(IMessageHandler<>),
        typeof(IProcessHandler<,>),
        typeof(IStreamHandler<>)
    };

    public static IList<HandlerReference> ScanForHandlers(IEnumerable<Assembly> assemblies)
    {
        var handlerReferences = new List<HandlerReference>();

        foreach (var assembly in assemblies)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition) continue;

                // Scan for IMessageHandler<T>, IProcessHandler<TData,TMessage>, IStreamHandler<T>
                foreach (var iface in type.GetInterfaces())
                {
                    if (!iface.IsGenericType) continue;
                    var genericDef = iface.GetGenericTypeDefinition();

                    if (Array.IndexOf(HandlerGenericTypes, genericDef) < 0) continue;

                    // Get the message type (last generic argument for all handler types)
                    var genericArgs = iface.GetGenericArguments();
                    var messageType = genericArgs[^1]; // Last arg is always the message type
                    if (messageType.IsGenericParameter) continue;

                    handlerReferences.Add(new HandlerReference
                    {
                        HandlerType = type,
                        MessageType = messageType,
                        RoutingKeys = new List<string>()
                    });
                }

                // Scan for Aggregator<T> subclasses
                if (type.BaseType is { IsGenericType: true } baseType &&
                    baseType.GetGenericTypeDefinition() == typeof(Aggregator<>))
                {
                    var messageType = baseType.GetGenericArguments()[0];
                    if (messageType.IsGenericParameter) continue;

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

- [ ] **Step 2: Update ServiceCollectionExtensions to register processor chain**

Replace `src/ServiceConnect/ServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;

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

        // Message processors (chain of responsibility)
        services.TryAddSingleton<ReplyProcessor>();
        services.TryAddSingleton<StreamProcessor>();
        services.TryAddSingleton<ProcessManagerProcessor>();
        services.TryAddSingleton<AggregatorProcessor>();
        services.TryAddSingleton<HandlerProcessor>();

        services.TryAddSingleton<IList<IMessageProcessor>>(sp => new List<IMessageProcessor>
        {
            sp.GetRequiredService<ReplyProcessor>(),
            sp.GetRequiredService<StreamProcessor>(),
            sp.GetRequiredService<ProcessManagerProcessor>(),
            sp.GetRequiredService<AggregatorProcessor>(),
            sp.GetRequiredService<HandlerProcessor>()
        });

        // Message dispatcher
        services.TryAddSingleton<MessageDispatcher>();

        // Handler scanning
        IList<HandlerReference> handlerReferences;
        if (builder.BusConfig.ScanForMessageHandlers)
            handlerReferences = HandlerScanner.ScanForHandlers(AppDomain.CurrentDomain.GetAssemblies());
        else
            handlerReferences = new List<HandlerReference>();

        foreach (var handlerRef in handlerReferences)
        {
            // Register IMessageHandler<T>
            var msgHandlerInterface = handlerRef.HandlerType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IMessageHandler<>)
                    && i.GetGenericArguments()[0] == handlerRef.MessageType);
            if (msgHandlerInterface != null)
            {
                services.AddTransient(msgHandlerInterface, handlerRef.HandlerType);
                continue;
            }

            // Register IProcessHandler<TData, TMessage>
            var processHandlerInterface = handlerRef.HandlerType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IProcessHandler<,>)
                    && i.GetGenericArguments()[1] == handlerRef.MessageType);
            if (processHandlerInterface != null)
            {
                services.AddTransient(processHandlerInterface, handlerRef.HandlerType);
                continue;
            }

            // Register IStreamHandler<T>
            var streamHandlerInterface = handlerRef.HandlerType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IStreamHandler<>)
                    && i.GetGenericArguments()[0] == handlerRef.MessageType);
            if (streamHandlerInterface != null)
            {
                services.AddTransient(streamHandlerInterface, handlerRef.HandlerType);
                continue;
            }

            // Register Aggregator<T>
            if (handlerRef.HandlerType.BaseType is { IsGenericType: true } bt &&
                bt.GetGenericTypeDefinition() == typeof(Aggregator<>))
            {
                services.AddTransient(bt, handlerRef.HandlerType);
                continue;
            }
        }

        services.TryAddSingleton<IList<HandlerReference>>(handlerReferences);

        // Apply additional registrations from builder extensions
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

- [ ] **Step 3: Update Bus.CreateStream**

In `src/ServiceConnect/Bus.cs`, replace the `CreateStream` method:

```csharp
    public IMessageBusWriteStream CreateStream<T>(string endpoint, T message) where T : Message
    {
        var producer = _sendPipeline.GetProducer();
        return new MessageBusWriteStream(producer, endpoint, typeof(T));
    }
```

Note: `ISendMessagePipeline` needs a `GetProducer()` method exposed, OR we inject `IProducer` directly into Bus. Check `ISendMessagePipeline` — if it doesn't expose the producer, add `IProducer` as an optional constructor parameter to Bus (same pattern as `IConsumer`).

Actually, simpler: inject `IProducer` into Bus constructor as optional (like `IConsumer`):

In Bus.cs constructor, add `IProducer? producer = null` parameter and store it. Then:

```csharp
    public IMessageBusWriteStream CreateStream<T>(string endpoint, T message) where T : Message
    {
        if (_producer == null)
            throw new InvalidOperationException("No producer registered. Cannot create stream.");
        return new MessageBusWriteStream(_producer, endpoint, typeof(T));
    }
```

- [ ] **Step 4: Create InMemoryPersistenceExtensions**

Create `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistence.InMemory;

public static class InMemoryPersistenceExtensions
{
    public static ServiceConnectBuilder UseInMemoryPersistence(this ServiceConnectBuilder builder)
    {
        builder.AdditionalRegistrations.Add(services =>
        {
            services.TryAddSingleton<ICacheProvider, CacheProvider>();
            services.TryAddSingleton<IAggregatorPersistor>(_ => new InMemoryAggregatorPersistor("", "", ""));
            services.TryAddSingleton<IProcessManagerFinder>(sp =>
            {
                var cache = sp.GetRequiredService<ICacheProvider>();
                return new InMemoryProcessManagerFinder(cache);
            });
        });

        return builder;
    }
}
```

Note: The InMemory persistence project needs a `<ProjectReference>` to ServiceConnect for `ServiceConnectBuilder`. Check if it already has one; if not, add it to the csproj, or move the extension into the main ServiceConnect project. The simplest approach: add the extension method to the E2E test project directly if the persistence project can't reference ServiceConnect.

Actually, the MongoDB extensions are in the persistence project and reference `ServiceConnectBuilder` via `ServiceConnect` namespace. The persistence project references `ServiceConnect.Interfaces` but not `ServiceConnect`. The `MongoDbPersistenceExtensions` works because `ServiceConnectBuilder` is in the `ServiceConnect` namespace. Check the MongoDB persistence project's references — if it references the main `ServiceConnect` project, the InMemory one should too. If not, we need to add the reference.

- [ ] **Step 5: Run all tests**

Run: `dotnet test -v n`
Expected: ALL tests PASS

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect/Services/HandlerScanner.cs src/ServiceConnect/ServiceCollectionExtensions.cs src/ServiceConnect/Bus.cs src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceExtensions.cs
git commit -m "feat: wire processor chain, update scanner, implement CreateStream"
```

---

### Task 10: Process Manager E2E Tests (InMemory + MongoDB)

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/ProcessManagerTests.cs`
- Create: `src/ServiceConnect.EndToEndTests/ProcessManagerMongoDbTests.cs`
- Modify: `src/ServiceConnect.EndToEndTests/Messages/TestMessage.cs` (add TestProcessData)

- [ ] **Step 1: Add TestProcessData to messages file**

In `src/ServiceConnect.EndToEndTests/Messages/TestMessage.cs`, add:

```csharp
public class TestProcessData : IProcessManagerData
{
    public Guid CorrelationId { get; set; }
    public int Counter { get; set; }
    public string LastContent { get; set; } = string.Empty;
}
```

Note: Check if `TestData.cs` already has this — the existing `TestData` in `Messages/TestData.cs` may already implement `IProcessManagerData`. If so, reuse it. If not, add `TestProcessData` to `TestMessage.cs`.

- [ ] **Step 2: Create InMemory process manager E2E test**

Create `src/ServiceConnect.EndToEndTests/ProcessManagerTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class ProcessManagerTests
{
    private readonly MessagingFixture _fixture;

    public ProcessManagerTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task ProcessManager_TwoMessages_StateUpdatedCorrectly()
    {
        var secondHandled = new TaskCompletionSource<bool>();
        var queueName = _fixture.GetUniqueQueueName("pm");
        var correlationId = Guid.NewGuid();

        var handlerRefs = new List<HandlerReference>
        {
            new() { HandlerType = typeof(CounterProcessHandler), MessageType = typeof(TestMessage), RoutingKeys = new List<string>() }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddSingleton(secondHandled);
        services.AddTransient<IProcessHandler<TestProcessData, TestMessage>, CounterProcessHandler>();

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
            builder.UseInMemoryPersistence();
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();
        await Task.Delay(500);

        try
        {
            // Send first message
            await bus.SendAsync(new TestMessage(correlationId) { Content = "first" },
                new SendOptions { EndPoint = queueName });

            // Wait a bit for processing
            await Task.Delay(1000);

            // Send second message with same correlationId
            await bus.SendAsync(new TestMessage(correlationId) { Content = "second" },
                new SendOptions { EndPoint = queueName });

            // Wait for second handler to complete
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => secondHandled.TrySetCanceled());
            await secondHandled.Task;

            // Verify state
            var finder = provider.GetRequiredService<IProcessManagerFinder>();
            // Use a mapper to look up data
            var mapper = new Helpers.TestProcessManagerPropertyMapper();
            mapper.ConfigureMapping<TestProcessData, TestMessage>(d => d.CorrelationId, m => m.CorrelationId);
            var lookupMsg = new TestMessage(correlationId);
            var result = finder.FindData<TestProcessData>(mapper, lookupMsg);

            Assert.NotNull(result);
            Assert.Equal(2, result.Data.Counter);
            Assert.Equal("second", result.Data.LastContent);
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }
}

file class CounterProcessHandler : IProcessHandler<TestProcessData, TestMessage>
{
    private readonly TaskCompletionSource<bool> _secondHandled;

    public CounterProcessHandler(TaskCompletionSource<bool> secondHandled)
    {
        _secondHandled = secondHandled;
    }

    public IConsumeContext? Context { get; set; }

    public Task HandleAsync(TestMessage message, TestProcessData data)
    {
        data.Counter++;
        data.LastContent = message.Content;
        if (data.Counter >= 2)
            _secondHandled.TrySetResult(true);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 3: Create MongoDB process manager E2E test**

Create `src/ServiceConnect.EndToEndTests/ProcessManagerMongoDbTests.cs` — same test logic but using `[Collection(nameof(PersistenceCollection))]`, `PersistenceFixture`, and `builder.UseMongoDbPersistence(...)` instead of `UseInMemoryPersistence()`.

- [ ] **Step 4: Run tests to verify they compile**

Run: `dotnet build src/ServiceConnect.EndToEndTests -v q`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.EndToEndTests/ProcessManagerTests.cs src/ServiceConnect.EndToEndTests/ProcessManagerMongoDbTests.cs src/ServiceConnect.EndToEndTests/Messages/TestMessage.cs
git commit -m "test: add process manager E2E tests (InMemory + MongoDB)"
```

---

### Task 11: Aggregator E2E Tests (InMemory + MongoDB + Timeout)

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/AggregatorTests.cs`
- Create: `src/ServiceConnect.EndToEndTests/AggregatorMongoDbTests.cs`

- [ ] **Step 1: Create InMemory aggregator E2E test (batch + timeout)**

Create `src/ServiceConnect.EndToEndTests/AggregatorTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class AggregatorTests
{
    private readonly MessagingFixture _fixture;

    public AggregatorTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Aggregator_BatchComplete_ExecutesWithAllMessages()
    {
        var executed = new TaskCompletionSource<IList<TestMessage>>();
        var queueName = _fixture.GetUniqueQueueName("agg-batch");

        var handlerRefs = new List<HandlerReference>
        {
            new() { HandlerType = typeof(BatchAggregator), MessageType = typeof(TestMessage), RoutingKeys = new List<string>() }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddSingleton(executed);
        services.AddTransient<Aggregator<TestMessage>, BatchAggregator>();

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
            builder.UseInMemoryPersistence();
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();
        await Task.Delay(500);

        try
        {
            // Send 3 messages (batch size = 3)
            for (int i = 0; i < 3; i++)
            {
                await bus.SendAsync(
                    new TestMessage(Guid.NewGuid()) { Content = $"batch-{i}" },
                    new SendOptions { EndPoint = queueName });
            }

            // Wait for aggregator to execute
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => executed.TrySetCanceled());
            var messages = await executed.Task;

            Assert.Equal(3, messages.Count);
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Aggregator_Timeout_FlushesPartialBatch()
    {
        var executed = new TaskCompletionSource<IList<TestMessage>>();
        var queueName = _fixture.GetUniqueQueueName("agg-timeout");

        var handlerRefs = new List<HandlerReference>
        {
            new() { HandlerType = typeof(TimeoutAggregator), MessageType = typeof(TestMessage), RoutingKeys = new List<string>() }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddSingleton(executed);
        services.AddTransient<Aggregator<TestMessage>, TimeoutAggregator>();

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
            builder.UseInMemoryPersistence();
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();
        await Task.Delay(500);

        try
        {
            // Send only 2 messages (batch size = 10, timeout = 2s)
            for (int i = 0; i < 2; i++)
            {
                await bus.SendAsync(
                    new TestMessage(Guid.NewGuid()) { Content = $"timeout-{i}" },
                    new SendOptions { EndPoint = queueName });
            }

            // Wait for timeout to fire (2s timeout + buffer)
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            cts.Token.Register(() => executed.TrySetCanceled());
            var messages = await executed.Task;

            Assert.Equal(2, messages.Count);
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }
}

file class BatchAggregator : Aggregator<TestMessage>
{
    private readonly TaskCompletionSource<IList<TestMessage>> _tcs;
    public BatchAggregator(TaskCompletionSource<IList<TestMessage>> tcs) => _tcs = tcs;
    public override int BatchSize() => 3;
    public override TimeSpan Timeout() => TimeSpan.FromSeconds(30);
    public override void Execute(IList<TestMessage> messages) => _tcs.TrySetResult(messages);
}

file class TimeoutAggregator : Aggregator<TestMessage>
{
    private readonly TaskCompletionSource<IList<TestMessage>> _tcs;
    public TimeoutAggregator(TaskCompletionSource<IList<TestMessage>> tcs) => _tcs = tcs;
    public override int BatchSize() => 10;
    public override TimeSpan Timeout() => TimeSpan.FromSeconds(2);
    public override void Execute(IList<TestMessage> messages) => _tcs.TrySetResult(messages);
}
```

- [ ] **Step 2: Create MongoDB aggregator E2E test**

Create `src/ServiceConnect.EndToEndTests/AggregatorMongoDbTests.cs` — batch test only (not timeout), using `[Collection(nameof(PersistenceCollection))]`, `PersistenceFixture`, and `builder.UseMongoDbPersistence(...)`.

- [ ] **Step 3: Build and commit**

```bash
dotnet build src/ServiceConnect.EndToEndTests -v q
git add src/ServiceConnect.EndToEndTests/AggregatorTests.cs src/ServiceConnect.EndToEndTests/AggregatorMongoDbTests.cs
git commit -m "test: add aggregator E2E tests (InMemory + MongoDB + timeout)"
```

---

### Task 12: Streaming E2E Test

**Files:**
- Create: `src/ServiceConnect.EndToEndTests/StreamingTests.cs`

- [ ] **Step 1: Create streaming E2E test**

Create `src/ServiceConnect.EndToEndTests/StreamingTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class StreamingTests
{
    private readonly MessagingFixture _fixture;

    public StreamingTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task CreateStream_WritesChunks_HandlerReceivesCompleteData()
    {
        var completed = new TaskCompletionSource<byte[]>();
        var consumerQueue = _fixture.GetUniqueQueueName("stream-consumer");
        var producerQueue = _fixture.GetUniqueQueueName("stream-producer");

        // Consumer bus with IStreamHandler<TestMessage>
        var handlerRefs = new List<HandlerReference>
        {
            new() { HandlerType = typeof(TestStreamHandler), MessageType = typeof(TestMessage), RoutingKeys = new List<string>() }
        };

        var consumerServices = new ServiceCollection();
        consumerServices.AddLogging();
        consumerServices.AddSingleton<IList<HandlerReference>>(handlerRefs);
        consumerServices.AddSingleton(completed);
        consumerServices.AddTransient<IStreamHandler<TestMessage>, TestStreamHandler>();

        consumerServices.AddServiceConnect(builder =>
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
            builder.ConfigureQueues(q => q.QueueName = consumerQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var consumerProvider = consumerServices.BuildServiceProvider();
        var consumerBus = consumerProvider.GetRequiredService<IBus>();
        await consumerBus.StartConsumingAsync();
        await Task.Delay(500);

        // Producer bus
        var producerServices = new ServiceCollection();
        producerServices.AddLogging();
        producerServices.AddSingleton<IList<HandlerReference>>(new List<HandlerReference>());
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
            builder.ConfigureQueues(q => q.QueueName = producerQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var producerProvider = producerServices.BuildServiceProvider();
        var producerBus = producerProvider.GetRequiredService<IBus>();

        try
        {
            // Create stream and write 3 chunks
            var message = new TestMessage(Guid.NewGuid()) { Content = "streamed" };
            using var stream = producerBus.CreateStream(consumerQueue, message);

            var chunk1 = new byte[] { 1, 2, 3, 4 };
            var chunk2 = new byte[] { 5, 6, 7, 8 };
            var chunk3 = new byte[] { 9, 10 };

            stream.Write(chunk1, 0, chunk1.Length);
            stream.Write(chunk2, 0, chunk2.Length);
            stream.Write(chunk3, 0, chunk3.Length);
            stream.Close();

            // Wait for handler to receive complete stream
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => completed.TrySetCanceled());
            var receivedBytes = await completed.Task;

            // Verify reassembled bytes match original chunks
            var expected = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            Assert.Equal(expected, receivedBytes);
        }
        finally
        {
            consumerBus.Dispose();
            producerBus.Dispose();
            (consumerProvider as IDisposable)?.Dispose();
            (producerProvider as IDisposable)?.Dispose();
        }
    }
}

file class TestStreamHandler : IStreamHandler<TestMessage>
{
    private readonly TaskCompletionSource<byte[]> _tcs;

    public TestStreamHandler(TaskCompletionSource<byte[]> tcs) => _tcs = tcs;

    public IMessageBusReadStream Stream { get; set; } = null!;

    public void Execute(TestMessage message)
    {
        var data = Stream.Read();
        _tcs.TrySetResult(data);
    }
}
```

- [ ] **Step 2: Build and commit**

```bash
dotnet build src/ServiceConnect.EndToEndTests -v q
git add src/ServiceConnect.EndToEndTests/StreamingTests.cs
git commit -m "test: add streaming E2E test"
```

---

### Task 13: Run All Tests + Fix Issues

- [ ] **Step 1: Run all unit tests**

Run: `dotnet test src/ServiceConnect.UnitTests -v n`
Expected: ALL tests PASS

- [ ] **Step 2: Run non-Docker E2E tests**

Run: `dotnet test src/ServiceConnect.EndToEndTests --filter "Category!=Docker" -v n`
Expected: ALL tests PASS

- [ ] **Step 3: Fix any compilation or test failures**

If there are failures, investigate and fix. Common issues:
- Missing `using` statements in new files
- Constructor signature changes in `MessageDispatcher` breaking existing tests
- `InMemoryProcessManagerFinder` constructor expecting `ICacheProvider` (check actual constructor)
- `ServiceConnectBuilder` namespace not available in persistence projects (may need project reference)

- [ ] **Step 4: Update deferred notes doc**

Update `docs/superpowers/notes/deferred-e2e-tests.md` — mark Streaming, ProcessManager, and Aggregator as DONE.

- [ ] **Step 5: Final commit**

```bash
git add -A
git commit -m "fix: resolve integration issues, update deferred notes"
```

---

## Post-Implementation

After all tasks are complete:

1. Run full test suite: `dotnet test -v n`
2. Run Docker E2E tests: `dotnet test src/ServiceConnect.EndToEndTests --filter "Category=Docker" -v n`
3. Verify all tests pass
4. The deferred notes doc should have all items marked as DONE
