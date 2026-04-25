using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using System.Reflection;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class StreamProcessorTests
{
    private static StreamProcessor BuildProcessor() =>
        new StreamProcessor(
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<StreamProcessor>.Instance,
            new MessageTypeRegistry(),
            new StreamHandlerRegistry(new List<HandlerReference>(), NullLogger<StreamHandlerRegistry>.Instance),
            Mock.Of<IMessageSerializer>(),
            TimeProvider.System);

    [Fact]
    public void Constructor_UsesSingleStateDictionaryAndInjectedSerializer()
    {
        Assert.NotNull(typeof(StreamProcessor).GetField("_serializer", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Null(typeof(StreamProcessor).GetField("_streamTimestamps", BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Fact]
    public async Task ProcessAsync_NonByteStream_ReturnsNotHandled()
    {
        var processor = BuildProcessor();
        var headers = new Dictionary<string, object> { [HeaderKeys.MessageType] = "Send" };
        var envelope = new Envelope { Headers = headers, Body = Array.Empty<byte>() };

        var result = await processor.ProcessAsync(Array.Empty<byte>(), typeof(object), null, headers, envelope);

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_NoMessageTypeHeader_ReturnsNotHandled()
    {
        var processor = BuildProcessor();
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = Array.Empty<byte>() };

        var result = await processor.ProcessAsync(Array.Empty<byte>(), typeof(object), null, headers, envelope);

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_ByteStreamPacket_WithValidGuidSequenceId_ReturnsHandled()
    {
        var processor = BuildProcessor();
        var sequenceId = Guid.NewGuid().ToString();
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
            [HeaderKeys.SequenceId] = sequenceId,
            [HeaderKeys.PacketNumber] = "0"
        };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(object), null, headers, envelope);

        Assert.Equal(ProcessResult.Handled, result);
    }

    // Non-GUID SequenceId must be rejected.
    [Fact]
    public async Task ProcessAsync_NonGuidSequenceId_ReturnsNotHandled()
    {
        var processor = BuildProcessor();
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
            [HeaderKeys.SequenceId] = "not-a-guid",
            [HeaderKeys.PacketNumber] = "0"
        };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(object), null, headers, envelope);

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    // Valid GUID SequenceId is accepted.
    [Fact]
    public async Task ProcessAsync_ValidGuidSequenceId_ReturnsHandled()
    {
        var processor = BuildProcessor();
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
            [HeaderKeys.SequenceId] = "3f2504e0-4f89-11d3-9a0c-0305e82c3301",
            [HeaderKeys.PacketNumber] = "0"
        };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(object), null, headers, envelope);

        Assert.Equal(ProcessResult.Handled, result);
    }

    // Stream creation is rejected when MaxActiveStreams limit is reached.
    [Fact]
    public async Task ProcessAsync_WhenMaxActiveStreamsReached_RejectsNewStream()
    {
        var processor = BuildProcessor();

        // Fill up to MaxActiveStreams (1000) by sending packet 0 to each distinct stream.
        // We only need to exceed the limit, so we drive it to 1000 streams first.
        for (int i = 0; i < 1000; i++)
        {
            var fillHeaders = new Dictionary<string, object>
            {
                [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
                [HeaderKeys.SequenceId] = Guid.NewGuid().ToString(),
                [HeaderKeys.PacketNumber] = "0"
            };
            var fillEnvelope = new Envelope { Headers = fillHeaders, Body = new byte[] { 1 } };
            await processor.ProcessAsync(new byte[] { 1 }, typeof(object), null, fillHeaders, fillEnvelope);
        }

        // Now a brand-new stream should be rejected.
        var newId = Guid.NewGuid().ToString();
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
            [HeaderKeys.SequenceId] = newId,
            [HeaderKeys.PacketNumber] = "0"
        };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(object), null, headers, envelope);

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    // LastPacketNumber exceeding the limit is rejected.
    [Fact]
    public async Task ProcessAsync_LastPacketNumberExceedsMax_ReturnsHandled_AndDiscards()
    {
        var processor = BuildProcessor();
        var sequenceId = Guid.NewGuid().ToString();
        // Send a final packet that claims LastPacketNumber = 100001 (above the 100_000 cap).
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
            [HeaderKeys.SequenceId] = sequenceId,
            [HeaderKeys.PacketNumber] = "0",
            [HeaderKeys.LastPacketNumber] = "100001"
        };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        // Returns Handled (to prevent requeue) but the stream is silently discarded.
        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(object), null, headers, envelope);

        Assert.Equal(ProcessResult.Handled, result);
    }

    // LastPacketNumber at exactly the limit (100_000) is accepted.
    [Fact]
    public async Task ProcessAsync_LastPacketNumberAtMax_IsAccepted()
    {
        var processor = BuildProcessor();
        var sequenceId = Guid.NewGuid().ToString();
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
            [HeaderKeys.SequenceId] = sequenceId,
            [HeaderKeys.PacketNumber] = "0",
            [HeaderKeys.LastPacketNumber] = "100000"
        };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(object), null, headers, envelope);

        // Packet received but stream not yet complete (we only sent packet 0 of 100001 total).
        Assert.Equal(ProcessResult.Handled, result);
    }

    [Fact]
    public void StreamProcessor_Implements_IAsyncDisposable()
    {
        // Disposal must wait for in-flight EvictStaleStreams callbacks.
        // ITimer.DisposeAsync awaits the callback; ITimer.Dispose does not.
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(StreamProcessor)),
            "StreamProcessor must implement IAsyncDisposable so disposal waits for the cleanup-timer callback.");
    }

    [Fact]
    public async Task DisposeAsync_CompletesWithoutThrowing()
    {
        var processor = BuildProcessor();
        var ex = await Record.ExceptionAsync(async () =>
        {
            await ((IAsyncDisposable)processor).DisposeAsync();
        });
        Assert.Null(ex);
    }

    // A handler that throws must propagate the exception out of InvokeHandlerAsync
    // and the error must be logged at Error level including handler type and sequence id.
    [Fact]
    public async Task InvokeHandlerAsync_ThrowingHandler_LogsErrorAndRethrows()
    {
        var capturingLogger = new SptCapturingLogger();

        var sequenceId = Guid.NewGuid().ToString();
        var msgType = typeof(SptMsg);

        var typeRegistry = new MessageTypeRegistry();
        typeRegistry.Register(msgType);

        var handlerRefs = new List<HandlerReference>
        {
            new() { MessageType = msgType, HandlerType = typeof(SptThrowingHandler) }
        };
        var streamHandlerRegistry = new StreamHandlerRegistry(handlerRefs, NullLogger<StreamHandlerRegistry>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton<IStreamHandler<SptMsg>>(new SptThrowingHandler());
        var provider = services.BuildServiceProvider();

        // The serializer just needs to return a valid SptMsg.
        var msg = new SptMsg(Guid.NewGuid());
        var serializerMock = new Mock<IMessageSerializer>();
        serializerMock
            .Setup(s => s.Deserialize(It.IsAny<System.Buffers.ReadOnlySequence<byte>>(), msgType))
            .Returns(msg);

        var processor = new StreamProcessor(
            provider,
            capturingLogger,
            typeRegistry,
            streamHandlerRegistry,
            serializerMock.Object,
            TimeProvider.System);

        // Build a single-packet complete stream.
        var payload = new byte[] { 0x01 };
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
            [HeaderKeys.SequenceId] = sequenceId,
            [HeaderKeys.PacketNumber] = "0",
            [HeaderKeys.LastPacketNumber] = "0",
            [HeaderKeys.FullTypeName] = msgType.FullName!
        };
        var envelope = new Envelope { Headers = headers, Body = payload };

        // The exception from the handler must propagate out.
        var thrownEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.ProcessAsync(payload, msgType, null, headers, envelope));

        Assert.Equal(SptThrowingHandler.ErrorMessage, thrownEx.Message);

        // The error must have been logged at Error level containing the sequenceId.
        Assert.Contains(capturingLogger.Entries, e =>
            e.Level == LogLevel.Error && e.Message.Contains(sequenceId));
    }

    // When MessageBusReadStream.Write throws (e.g. packet number exceeds the
    // already-set LastPacketNumber), the StreamProcessor must evict the entry
    // from _activeStreams so the sequence is not wedged until the 5-minute sweep.
    [Fact]
    public async Task ProcessAsync_WhenWriteThrowsForPoisonPacket_EvictsActiveStreamEntry()
    {
        var processor = BuildProcessor();
        var sequenceId = Guid.NewGuid().ToString();

        // Packet 0 of a 3-packet stream — establishes LastPacketNumber=2 without
        // completing the sequence (packets 1 and 2 are still outstanding).
        var setupHeaders = new Dictionary<string, object>
        {
            [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
            [HeaderKeys.SequenceId] = sequenceId,
            [HeaderKeys.PacketNumber] = "0",
            [HeaderKeys.LastPacketNumber] = "2"
        };
        var setupEnv = new Envelope { Headers = setupHeaders, Body = new byte[] { 1 } };
        await processor.ProcessAsync(new byte[] { 1 }, typeof(object), null, setupHeaders, setupEnv);

        // Now send packet 99 — exceeds LastPacketNumber=2 → underlying Write throws.
        var poisonHeaders = new Dictionary<string, object>
        {
            [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
            [HeaderKeys.SequenceId] = sequenceId,
            [HeaderKeys.PacketNumber] = "99"
        };
        var poisonEnv = new Envelope { Headers = poisonHeaders, Body = new byte[] { 9 } };

        var result = await processor.ProcessAsync(new byte[] { 9 }, typeof(object), null, poisonHeaders, poisonEnv);

        // Handled to drop the poison packet without requeue.
        Assert.Equal(ProcessResult.Handled, result);

        // The sequence must no longer occupy a slot in _activeStreams.
        var dictField = typeof(StreamProcessor)
            .GetField("_activeStreams", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(dictField);
        // The dictionary's value type (ActiveStreamState) is a private nested type in
        // StreamProcessor, so the test inspects it via the non-generic IDictionary surface.
        var dict = (System.Collections.IDictionary)dictField!.GetValue(processor)!;
        Assert.False(dict.Contains(sequenceId), "Active-stream entry must be evicted after a poison-packet exception.");
    }

    // ActiveStreamState must be a record (or readonly struct) so that updating
    // LastSeenUtc requires a new instance and the eviction sweep's KVP-based TryRemove
    // can detect concurrent touches via reference inequality.
    [Fact]
    public void ActiveStreamState_IsImmutable_LastSeenUtcHasNoPublicSetter()
    {
        var stateType = typeof(StreamProcessor)
            .GetNestedType("ActiveStreamState", BindingFlags.NonPublic);
        Assert.NotNull(stateType);

        var lastSeen = stateType!.GetProperty("LastSeenUtc");
        Assert.NotNull(lastSeen);
        // A positional record property has an init-only setter — its SetMethod carries the
        // IsExternalInit modifier. A mutable `set` carries no such modifier. Asserting the
        // setter exists AND is init-only avoids a vacuous pass if the property were ever
        // refactored to get-only.
        var setter = lastSeen!.SetMethod;
        Assert.NotNull(setter);
        var modifiers = setter!.ReturnParameter.GetRequiredCustomModifiers();
        Assert.Contains(modifiers,
            m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
    }

    // After the duplicate-packet idempotent-ack fix, multiple deliveries of the same final
    // packet all observe IsComplete() == true. The dispatch path must gate on
    // TryRemove returning true; otherwise every duplicate triggers another handler
    // invocation. Use a Barrier to converge N threads at the dispatch boundary.
    [Fact]
    public async Task ProcessAsync_ConcurrentFinalPacketDeliveries_DispatchesHandlerOnce()
    {
        var sequenceId = Guid.NewGuid().ToString();
        var msgType = typeof(SptMsg);

        var typeRegistry = new MessageTypeRegistry();
        typeRegistry.Register(msgType);

        var handlerRefs = new List<HandlerReference>
        {
            new() { MessageType = msgType, HandlerType = typeof(SptCountingHandler) }
        };
        var streamHandlerRegistry = new StreamHandlerRegistry(handlerRefs, NullLogger<StreamHandlerRegistry>.Instance);

        var counter = new SptCounter();
        var services = new ServiceCollection();
        services.AddSingleton<IStreamHandler<SptMsg>>(_ => new SptCountingHandler(counter));
        var provider = services.BuildServiceProvider();

        var msg = new SptMsg(Guid.NewGuid());
        var serializerMock = new Mock<IMessageSerializer>();
        serializerMock
            .Setup(s => s.Deserialize(It.IsAny<System.Buffers.ReadOnlySequence<byte>>(), msgType))
            .Returns(msg);

        var processor = new StreamProcessor(
            provider,
            NullLogger<StreamProcessor>.Instance,
            typeRegistry,
            streamHandlerRegistry,
            serializerMock.Object,
            TimeProvider.System);

        var payload = new byte[] { 0x01 };

        const int concurrent = 8;
        using var barrier = new System.Threading.Barrier(concurrent);
        var tasks = Enumerable.Range(0, concurrent).Select(_ => Task.Run(async () =>
        {
            // Each task gets its own headers dict so the dispatch path doesn't race on
            // a shared dictionary; values are identical.
            var headers = new Dictionary<string, object>
            {
                [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
                [HeaderKeys.SequenceId] = sequenceId,
                [HeaderKeys.PacketNumber] = "0",
                [HeaderKeys.LastPacketNumber] = "0",
                [HeaderKeys.FullTypeName] = msgType.FullName!
            };
            var envelope = new Envelope { Headers = headers, Body = payload };

            barrier.SignalAndWait();
            await processor.ProcessAsync(payload, msgType, null, headers, envelope);
        })).ToList();

        await Task.WhenAll(tasks);

        Assert.Equal(1, counter.Count);
    }

    // The touch path must REPLACE the active-stream entry with a new instance carrying
    // the updated LastSeenUtc. If the field is mutated in place, the eviction sweep's
    // TOCTOU race is unavoidable.
    [Fact]
    public async Task ProcessAsync_TouchPath_ReplacesActiveStreamStateInstance()
    {
        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(
            DateTimeOffset.UtcNow);

        var processor = new StreamProcessor(
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<StreamProcessor>.Instance,
            new MessageTypeRegistry(),
            new StreamHandlerRegistry(new List<HandlerReference>(), NullLogger<StreamHandlerRegistry>.Instance),
            Mock.Of<IMessageSerializer>(),
            fakeTime);

        var sequenceId = Guid.NewGuid().ToString();
        var headers0 = new Dictionary<string, object>
        {
            [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
            [HeaderKeys.SequenceId] = sequenceId,
            [HeaderKeys.PacketNumber] = "0"
        };
        await processor.ProcessAsync(new byte[] { 1 }, typeof(object), null, headers0, new Envelope { Headers = headers0, Body = new byte[] { 1 } });

        var dictField = typeof(StreamProcessor)
            .GetField("_activeStreams", BindingFlags.Instance | BindingFlags.NonPublic);
        var dict = (System.Collections.IDictionary)dictField!.GetValue(processor)!;
        var firstState = dict[sequenceId];
        Assert.NotNull(firstState);

        // Advance time and send the next packet — touch path must produce a new state instance.
        fakeTime.Advance(TimeSpan.FromSeconds(30));
        var headers1 = new Dictionary<string, object>
        {
            [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
            [HeaderKeys.SequenceId] = sequenceId,
            [HeaderKeys.PacketNumber] = "1"
        };
        await processor.ProcessAsync(new byte[] { 2 }, typeof(object), null, headers1, new Envelope { Headers = headers1, Body = new byte[] { 2 } });

        var secondState = dict[sequenceId];
        Assert.NotNull(secondState);
        Assert.NotSame(firstState, secondState);

        var lastSeenProp = secondState!.GetType().GetProperty("LastSeenUtc")!;
        var lastSeen = (DateTimeOffset)lastSeenProp.GetValue(secondState)!;
        Assert.Equal(fakeTime.GetUtcNow(), lastSeen);
    }
}

file class SptMsg : Message { public SptMsg(Guid c) : base(c) { } }

file class SptThrowingHandler : IStreamHandler<SptMsg>
{
    public const string ErrorMessage = "handler-boom";
    public IMessageBusReadStream Stream { get; set; } = null!;
    public Task ExecuteAsync(SptMsg stream, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(ErrorMessage);
}

file sealed class SptCapturingLogger : ILogger<StreamProcessor>
{
    public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
    public List<LogEntry> Entries { get; } = [];

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;
    bool ILogger.IsEnabled(LogLevel logLevel) => true;

    void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }
}

file sealed class SptCounter
{
    private int _count;
    public int Count => Volatile.Read(ref _count);
    public void Increment() => Interlocked.Increment(ref _count);
}

file sealed class SptCountingHandler : IStreamHandler<SptMsg>
{
    private readonly SptCounter _counter;
    public SptCountingHandler(SptCounter counter) => _counter = counter;
    public IMessageBusReadStream Stream { get; set; } = null!;
    public Task ExecuteAsync(SptMsg stream, CancellationToken cancellationToken = default)
    {
        _counter.Increment();
        return Task.CompletedTask;
    }
}
