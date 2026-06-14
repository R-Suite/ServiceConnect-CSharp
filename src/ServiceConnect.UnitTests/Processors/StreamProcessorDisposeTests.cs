using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class StreamProcessorDisposeTests
{
    private static StreamProcessor BuildProcessor(TimeProvider? timeProvider = null)
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var accessor = new ConsumeScopeAccessor();
        accessor.Push(provider);
        return new StreamProcessor(
            accessor,
            NullLogger<StreamProcessor>.Instance,
            new MessageTypeRegistry(),
            new StreamHandlerRegistry([], NullLogger<StreamHandlerRegistry>.Instance),
            Mock.Of<IMessageSerializer>(),
            timeProvider ?? TimeProvider.System,
            new BusConfiguration());
    }

    [Fact]
    public async Task ProcessAsync_AfterDispose_ReturnsNotHandled()
    {
        var processor = BuildProcessor();
        await processor.DisposeAsync();

        var sequenceId = Guid.NewGuid().ToString();
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
            [HeaderKeys.SequenceId] = sequenceId,
            [HeaderKeys.PacketNumber] = "0",
        };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 0xAA } };

        var result = await processor.ProcessAsync(
            messageBytes: new byte[] { 0xAA },
            messageType: typeof(byte[]),
            message: null,
            headers: headers,
            envelope: envelope,
            cancellationToken: CancellationToken.None);

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_AfterDispose_DoesNotPopulateActiveStreams()
    {
        var processor = BuildProcessor();
        await processor.DisposeAsync();

        var sequenceId = Guid.NewGuid().ToString();
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
            [HeaderKeys.SequenceId] = sequenceId,
            [HeaderKeys.PacketNumber] = "0",
        };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 0xAA } };

        await processor.ProcessAsync(
            messageBytes: new byte[] { 0xAA },
            messageType: typeof(byte[]),
            message: null,
            headers: headers,
            envelope: envelope,
            cancellationToken: CancellationToken.None);

        // The dictionary must remain empty — once disposed, ProcessAsync must short-circuit
        // before calling GetOrAdd, otherwise stream entries leak past disposal.
        Assert.Equal(0, processor.ActiveStreamCount);
    }

    [Fact]
    public async Task DisposeAsync_DrainsPreviouslyAdmittedStreams()
    {
        var processor = BuildProcessor();

        // Admit a stream before disposing.
        var sequenceId = Guid.NewGuid().ToString();
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
            [HeaderKeys.SequenceId] = sequenceId,
            [HeaderKeys.PacketNumber] = "0",
        };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 0x01 } };
        await processor.ProcessAsync(
            messageBytes: new byte[] { 0x01 },
            messageType: typeof(byte[]),
            message: null,
            headers: headers,
            envelope: envelope,
            cancellationToken: CancellationToken.None);

        Assert.Equal(1, processor.ActiveStreamCount);

        await processor.DisposeAsync();

        // Dispose must drain the dictionary so retained memory is released promptly.
        Assert.Equal(0, processor.ActiveStreamCount);
    }

    [Fact]
    public async Task DisposeAsync_CalledMultipleTimes_IsIdempotent()
    {
        var processor = BuildProcessor();

        // Calling DisposeAsync twice must not throw.
        await processor.DisposeAsync();
        await processor.DisposeAsync();
    }
}
