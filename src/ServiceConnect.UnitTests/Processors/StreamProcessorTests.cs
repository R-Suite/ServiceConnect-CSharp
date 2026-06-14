using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using ServiceConnect.UnitTests;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

[Collection(SerialConcurrencyCollection.Name)]
public class StreamProcessorTests
{
    private static (ConsumeScopeAccessor accessor, IDisposable scope, IServiceScopeFactory factory) BuildScopeContext(IServiceProvider provider)
    {
        var accessor = new ConsumeScopeAccessor();
        var scope = accessor.Push(provider);
        var factory = provider.GetRequiredService<IServiceScopeFactory>();
        return (accessor, scope, factory);
    }

    private static StreamProcessor BuildProcessor()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var accessor = new ConsumeScopeAccessor();
        // Push a never-popped scope: BuildProcessor is called from sync test bodies
        // that don't await across the call, so the AsyncLocal value stays in scope
        // for any subsequent ProcessAsync invocations on the returned processor.
        accessor.Push(provider);
        return new StreamProcessor(
            accessor,
            NullLogger<StreamProcessor>.Instance,
            new MessageTypeRegistry(),
            new StreamHandlerRegistry([], NullLogger<StreamHandlerRegistry>.Instance),
            Mock.Of<IMessageSerializer>(),
            TimeProvider.System,
            new BusConfiguration());
    }

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

    // Repeated cap-violating packets must not accumulate state. Validation runs before
    // Stream.Write commits bytes, and rejection actively removes the entry plus decrements
    // the count so the slot reclaims immediately rather than waiting on the eviction sweep.
    [Fact]
    public async Task ProcessAsync_RepeatedLastPacketNumberCapViolations_DoNotLeakActiveStreams()
    {
        var processor = BuildProcessor();

        for (int i = 0; i < 50; i++)
        {
            var sequenceId = Guid.NewGuid().ToString();
            var headers = new Dictionary<string, object>
            {
                [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
                [HeaderKeys.SequenceId] = sequenceId,
                [HeaderKeys.PacketNumber] = "0",
                [HeaderKeys.LastPacketNumber] = "100001",
            };
            var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

            var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(object), null, headers, envelope);
            Assert.Equal(ProcessResult.Handled, result);
        }

        // Each rejected packet's stream slot must reclaim immediately. A leak would leave
        // 50 entries resident, each holding up to 100 MB of writeable state until the
        // 5-minute eviction sweep — the DoS vector this fix closes.
        Assert.Equal(0, processor.ActiveStreamCount);
    }

    // Garbage in the LastPacketNumber header must not commit bytes either.
    [Fact]
    public async Task ProcessAsync_UnparseableLastPacketNumber_DiscardsAndReclaimsSlot()
    {
        var processor = BuildProcessor();
        var sequenceId = Guid.NewGuid().ToString();
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
            [HeaderKeys.SequenceId] = sequenceId,
            [HeaderKeys.PacketNumber] = "0",
            [HeaderKeys.LastPacketNumber] = "not-a-number",
        };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(object), null, headers, envelope);

        Assert.Equal(ProcessResult.Handled, result);
        Assert.Equal(0, processor.ActiveStreamCount);
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
            .Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), msgType))
            .Returns(msg);

        var (accessor, scopeStream1, _) = BuildScopeContext(provider);
        using var _scopeStream1 = scopeStream1;
        var processor = new StreamProcessor(
            accessor,
            capturingLogger,
            typeRegistry,
            streamHandlerRegistry,
            serializerMock.Object,
            TimeProvider.System,
            new BusConfiguration());

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

    // An OCE thrown by the handler with an unrelated CT (e.g. the handler timed out
    // an internal HTTP call via its own CancellationTokenSource) must NOT escape with
    // that unrelated token attached. The downstream metrics pipeline gates
    // error.type=cancelled on cancellationToken.IsCancellationRequested; an unrelated
    // OCE carrying a handler-owned token would produce a false graceful-shutdown signal.
    [Fact]
    public async Task InvokeHandlerAsync_HandlerThrowsUnrelatedOce_DoesNotPretendCallerCancelled()
    {
        using var unrelatedCts = new CancellationTokenSource();
        unrelatedCts.Cancel();

        var sequenceId = Guid.NewGuid().ToString();
        var msgType = typeof(SptMsg);

        var typeRegistry = new MessageTypeRegistry();
        typeRegistry.Register(msgType);

        var handlerRefs = new List<HandlerReference>
        {
            new() { MessageType = msgType, HandlerType = typeof(SptUnrelatedOceHandler) }
        };
        var streamHandlerRegistry = new StreamHandlerRegistry(handlerRefs, NullLogger<StreamHandlerRegistry>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton<IStreamHandler<SptMsg>>(new SptUnrelatedOceHandler(unrelatedCts.Token));
        var provider = services.BuildServiceProvider();

        var msg = new SptMsg(Guid.NewGuid());
        var serializerMock = new Mock<IMessageSerializer>();
        serializerMock
            .Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), msgType))
            .Returns(msg);

        var (accessor, scopeOce, _) = BuildScopeContext(provider);
        using var _scopeOce = scopeOce;
        var processor = new StreamProcessor(
            accessor,
            NullLogger<StreamProcessor>.Instance,
            typeRegistry,
            streamHandlerRegistry,
            serializerMock.Object,
            TimeProvider.System,
            new BusConfiguration());

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

        using var callerCts = new CancellationTokenSource();
        // Caller CT is NOT cancelled.

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(
            () => processor.ProcessAsync(payload, msgType, null, headers, envelope, callerCts.Token));

        // The escaping OCE must carry the caller's token (not the handler-owned one).
        // The inner exception preserves the original handler-thrown OCE with the unrelated token.
        Assert.Equal(callerCts.Token, ex.CancellationToken);
        Assert.False(ex.CancellationToken.IsCancellationRequested);
        Assert.NotNull(ex.InnerException);
        Assert.IsType<OperationCanceledException>(ex.InnerException);
        Assert.Equal(unrelatedCts.Token, ((OperationCanceledException)ex.InnerException).CancellationToken);
    }

    // When the CALLER cancels and the handler also throws OCE with an unrelated token, the
    // first catch branch takes priority: the escaping OCE must carry the caller's token,
    // confirming the cancellation was a genuine caller-initiated shutdown.
    [Fact]
    public async Task InvokeHandlerAsync_CallerCancels_OceCarriesCallerToken()
    {
        using var unrelatedCts = new CancellationTokenSource();
        unrelatedCts.Cancel();

        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel(); // caller IS cancelled this time

        var sequenceId = Guid.NewGuid().ToString();
        var msgType = typeof(SptMsg);

        var typeRegistry = new MessageTypeRegistry();
        typeRegistry.Register(msgType);

        var handlerRefs = new List<HandlerReference>
        {
            new() { MessageType = msgType, HandlerType = typeof(SptUnrelatedOceHandler) }
        };
        var streamHandlerRegistry = new StreamHandlerRegistry(handlerRefs, NullLogger<StreamHandlerRegistry>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton<IStreamHandler<SptMsg>>(new SptUnrelatedOceHandler(unrelatedCts.Token));
        var provider = services.BuildServiceProvider();

        var msg = new SptMsg(Guid.NewGuid());
        var serializerMock = new Mock<IMessageSerializer>();
        serializerMock
            .Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), msgType))
            .Returns(msg);

        var (accessor, scopeCallerCancels, _) = BuildScopeContext(provider);
        using var _scopeCallerCancels = scopeCallerCancels;
        var processor = new StreamProcessor(
            accessor,
            NullLogger<StreamProcessor>.Instance,
            typeRegistry,
            streamHandlerRegistry,
            serializerMock.Object,
            TimeProvider.System,
            new BusConfiguration());

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

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(
            () => processor.ProcessAsync(payload, msgType, null, headers, envelope, callerCts.Token));

        // Caller-cancelled path: OCE must carry the caller's token, which IS cancelled.
        Assert.Equal(callerCts.Token, ex.CancellationToken);
        Assert.True(ex.CancellationToken.IsCancellationRequested);
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
        var poisonEnv = new Envelope { Headers = poisonHeaders, Body = "\t"u8.ToArray() };

        var result = await processor.ProcessAsync("\t"u8.ToArray(), typeof(object), null, poisonHeaders, poisonEnv);

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

    // The dispatch path is idempotent-ack: multiple concurrent deliveries of the same final
    // packet all observe IsComplete() == true, but only the thread whose TryRemove returns
    // true is allowed to invoke the handler. Use a Barrier to converge N threads at the
    // dispatch boundary so the race is forced rather than rare.
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
            .Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), msgType))
            .Returns(msg);

        var (accessor, scopeStream2, _) = BuildScopeContext(provider);
        using var _scopeStream2 = scopeStream2;
        // FakeTimeProvider with AutoAdvanceAmount = 1 tick ensures every GetUtcNow()
        // call returns a strictly increasing value. Under TimeProvider.System the high
        // concurrency made multiple ActiveStreamState records structurally equal (identical
        // LastSeenUtc ticks), allowing more than one TryRemove(KVP) to succeed in the
        // completion-dispatch path and causing the handler to fire more than once.
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
        {
            AutoAdvanceAmount = TimeSpan.FromTicks(1),
        };
        var processor = new StreamProcessor(
            accessor,
            NullLogger<StreamProcessor>.Instance,
            typeRegistry,
            streamHandlerRegistry,
            serializerMock.Object,
            clock,
            new BusConfiguration());

        var payload = new byte[] { 0x01 };

        // Pre-prime the stream so all concurrent tasks find it in the dictionary via
        // the existing-stream path (TryGetValue → hit). Without pre-priming, a thread
        // delayed by the scheduler can arrive at the admission block after TryRemove
        // has already evicted the completed entry, causing re-admission of the same
        // sequenceId as a brand-new stream and a second handler dispatch.
        // Send packet 0 WITHOUT LastPacketNumber so the stream is admitted but stays open.
        var primeHeaders = new Dictionary<string, object>
        {
            [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
            [HeaderKeys.SequenceId] = sequenceId,
            [HeaderKeys.PacketNumber] = "0",
        };
        await processor.ProcessAsync(payload, msgType, null, primeHeaders, new Envelope { Headers = primeHeaders, Body = payload });

        const int concurrent = 8;
        using var barrier = new System.Threading.Barrier(concurrent);
        var tasks = Enumerable.Range(0, concurrent).Select(_ => Task.Run(async () =>
        {
            // Each task gets its own headers dict so the dispatch path doesn't race on
            // a shared dictionary; values are identical. Packet 0 is already in the
            // stream (from pre-prime); Write silently no-ops on duplicate packet numbers,
            // SetLastPacketNumber(0) completes the stream, and all 8 tasks race TryRemove.
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

        var (accessor, scopeStream3, _) = BuildScopeContext(new ServiceCollection().BuildServiceProvider());
        using var _scopeStream3 = scopeStream3;
        var processor = new StreamProcessor(
            accessor,
            NullLogger<StreamProcessor>.Instance,
            new MessageTypeRegistry(),
            new StreamHandlerRegistry([], NullLogger<StreamHandlerRegistry>.Instance),
            Mock.Of<IMessageSerializer>(),
            fakeTime,
            new BusConfiguration());

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

    // Concurrent opens that race across the admission threshold must not push
    // _activeStreams past MaxActiveStreams. Each opener uses a fresh SequenceId so none
    // of the GetOrAdd calls collide on an existing key; the race is purely on the count check.
    [Fact]
    public async Task ProcessAsync_ConcurrentExclusiveOpensAtCap_DoNotExceedCap()
    {
        const int cap = 1000;
        const int parallelism = 64;
        const int extra = 32;

        var processor = BuildProcessor();

        // Warm to cap-1 sequentially so the race fires right at the boundary.
        for (int i = 0; i < cap - 1; i++)
        {
            var warmHeaders = new Dictionary<string, object>
            {
                [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
                [HeaderKeys.SequenceId] = Guid.NewGuid().ToString(),
                [HeaderKeys.PacketNumber] = "0"
            };
            await processor.ProcessAsync(new byte[] { 1 }, typeof(object), null, warmHeaders,
                new Envelope { Headers = warmHeaders, Body = new byte[] { 1 } });
        }

        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, parallelism + extra).Select(_ => Task.Run(async () =>
        {
            var seqId = Guid.NewGuid().ToString();
            var headers = new Dictionary<string, object>
            {
                [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
                [HeaderKeys.SequenceId] = seqId,
                [HeaderKeys.PacketNumber] = "0"
            };
            gate.Wait();
            await processor.ProcessAsync(new byte[] { 1 }, typeof(object), null, headers,
                new Envelope { Headers = headers, Body = new byte[] { 1 } });
        })).ToList();

        gate.Set();
        await Task.WhenAll(tasks);

        Assert.True(processor.ActiveStreamCount <= cap,
            $"Expected ActiveStreamCount <= {cap} but was {processor.ActiveStreamCount}");
    }

    // The processor must resolve the IStreamHandler from the consume scope that is currently
    // active when ProcessAsync runs. The dispatcher pushes a per-message scope; without
    // scope-aware resolution, scoped handler dependencies (DbContext, unit-of-work, tenant
    // context) leak across messages. To verify the lookup honours Current (not anything
    // captured at ctor time), this test sets up two providers each carrying a different
    // IStreamHandler instance and pushes the "scoped" one before invoking ProcessAsync.
    [Fact]
    public async Task ProcessAsync_ResolvesHandlerFromCurrentConsumeScope_NotRoot()
    {
        var rootHandlerProbe = new ScopeProbeStreamHandler("root");
        var scopedHandlerProbe = new ScopeProbeStreamHandler("scoped");

        var rootServices = new ServiceCollection();
        rootServices.AddSingleton<IStreamHandler<ScopeProbeMessage>>(rootHandlerProbe);
        var rootProvider = rootServices.BuildServiceProvider();

        var scopedServices = new ServiceCollection();
        scopedServices.AddSingleton<IStreamHandler<ScopeProbeMessage>>(scopedHandlerProbe);
        var scopedProvider = scopedServices.BuildServiceProvider();

        var typeRegistry = new MessageTypeRegistry();
        typeRegistry.Register(typeof(ScopeProbeMessage));

        var handlerRefs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ScopeProbeMessage), HandlerType = typeof(ScopeProbeStreamHandler) }
        };
        var streamRegistry = new StreamHandlerRegistry(handlerRefs, NullLogger<StreamHandlerRegistry>.Instance);

        var scopeAccessor = new ConsumeScopeAccessor();
        var serializer = new SystemTextJsonMessageSerializer();

        var processor = new StreamProcessor(
            scopeAccessor,
            NullLogger<StreamProcessor>.Instance,
            typeRegistry,
            streamRegistry,
            serializer,
            TimeProvider.System,
            new BusConfiguration());

        // Push the root provider as an outer (mismatched) scope first. If the processor
        // were ever to fall back to a captured-at-ctor reference, rootHandlerProbe would
        // be the resolved instance. The inner push of scopedProvider must override.
        using var rootPush = scopeAccessor.Push(rootProvider);
        using (scopeAccessor.Push(scopedProvider))
        {
            var sequenceId = Guid.NewGuid().ToString();
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new ScopeProbeMessage(Guid.NewGuid()));
            var headers = new Dictionary<string, object>
            {
                [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
                [HeaderKeys.SequenceId] = sequenceId,
                [HeaderKeys.PacketNumber] = "0",
                [HeaderKeys.LastPacketNumber] = "0",
                [HeaderKeys.FullTypeName] = typeof(ScopeProbeMessage).AssemblyQualifiedName!
            };
            var envelope = new Envelope { Headers = headers, Body = bytes };

            await processor.ProcessAsync(bytes, typeof(ScopeProbeMessage), null, headers, envelope, CancellationToken.None);
        }

        Assert.Equal(0, rootHandlerProbe.InvocationCount);
        Assert.Equal(1, scopedHandlerProbe.InvocationCount);
    }
}

file sealed class ScopeProbeMessage(Guid correlationId) : Message(correlationId)
{
}

file sealed class ScopeProbeStreamHandler(string label) : IStreamHandler<ScopeProbeMessage>
{
    private int _count;

    public string Label { get; } = label; public int InvocationCount => Volatile.Read(ref _count);
    public Task ExecuteAsync(ScopeProbeMessage message, IMessageBusReadStream stream, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _count);
        return Task.CompletedTask;
    }
}

file class SptMsg(Guid c) : Message(c) {
}

file class SptThrowingHandler : IStreamHandler<SptMsg>
{
    public const string ErrorMessage = "handler-boom";
    public Task ExecuteAsync(SptMsg message, IMessageBusReadStream stream, CancellationToken cancellationToken = default)
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

file sealed class SptCountingHandler(SptCounter counter) : IStreamHandler<SptMsg>
{
    private readonly SptCounter _counter = counter;

    public Task ExecuteAsync(SptMsg message, IMessageBusReadStream stream, CancellationToken cancellationToken = default)
    {
        _counter.Increment();
        return Task.CompletedTask;
    }
}

// Simulates a handler that times out an internal sub-call via its own CancellationTokenSource.
// The OCE it throws carries that handler-owned token, not the caller's CT.
file sealed class SptUnrelatedOceHandler(CancellationToken unrelatedToken) : IStreamHandler<SptMsg>
{
    public Task ExecuteAsync(SptMsg message, IMessageBusReadStream stream, CancellationToken cancellationToken = default)
        => throw new OperationCanceledException("handler's own linked CTS", unrelatedToken);
}
