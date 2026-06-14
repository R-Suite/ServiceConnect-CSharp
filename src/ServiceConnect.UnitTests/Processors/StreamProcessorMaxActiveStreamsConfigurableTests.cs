using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

/// <summary>
/// Verifies that the active-stream admission cap previously hard-coded as
/// <c>StreamProcessor.MaxActiveStreams = 1000</c> is now sourced from
/// <see cref="IBusConfiguration.MaxActiveStreams"/> (default 1,000) and snapshotted into
/// each <see cref="StreamProcessor"/> at construction. The admission check is
/// <c>newCount &gt; _maxActiveStreams</c>, so a configured value of <c>2</c> admits
/// exactly two distinct sequence ids and rejects the third.
/// </summary>
public class StreamProcessorMaxActiveStreamsConfigurableTests
{
    [Fact]
    public void BusConfiguration_MaxActiveStreams_DefaultsTo1000()
    {
        var config = new BusConfiguration();

        Assert.Equal(1000, config.MaxActiveStreams);
    }

    [Fact]
    public async Task StreamProcessor_HonoursConfiguredCap()
    {
        // Configured cap of 2: the first two distinct sequence ids must be admitted,
        // the third must be rejected at the admission gate. Each admission is the first
        // packet of its stream; nothing completes, nothing evicts, so ActiveStreamCount
        // settles at exactly the configured cap.
        var config = new BusConfiguration { MaxActiveStreams = 2 };
        var processor = BuildProcessor(config);

        await SendFirstPacketAsync(processor, Guid.NewGuid().ToString());
        await SendFirstPacketAsync(processor, Guid.NewGuid().ToString());
        await SendFirstPacketAsync(processor, Guid.NewGuid().ToString());

        Assert.Equal(2, processor.ActiveStreamCount);
    }

    private static StreamProcessor BuildProcessor(IBusConfiguration busConfig)
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
            TimeProvider.System,
            busConfig);
    }

    private static Task SendFirstPacketAsync(StreamProcessor processor, string sequenceId)
    {
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
            [HeaderKeys.SequenceId] = sequenceId,
            [HeaderKeys.PacketNumber] = "0",
        };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 0xCC } };
        return processor.ProcessAsync(
            messageBytes: new byte[] { 0xCC },
            messageType: typeof(byte[]),
            message: null,
            headers: headers,
            envelope: envelope,
            cancellationToken: CancellationToken.None);
    }
}
