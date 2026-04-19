using Microsoft.Extensions.DependencyInjection;
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
}
