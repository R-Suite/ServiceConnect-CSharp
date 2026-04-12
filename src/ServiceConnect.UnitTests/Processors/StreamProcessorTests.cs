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
        var processor = new StreamProcessor(provider, new Mock<ILogger<StreamProcessor>>().Object, new MessageTypeRegistry());

        var headers = new Dictionary<string, object> { [HeaderKeys.MessageType] = "Send" };
        var envelope = new Envelope { Headers = headers, Body = Array.Empty<byte>() };

        var result = await processor.ProcessAsync(Array.Empty<byte>(), typeof(object), null, headers, envelope);

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_NoMessageTypeHeader_ReturnsNotHandled()
    {
        var services = new ServiceCollection();
        var provider = services.BuildServiceProvider();
        var processor = new StreamProcessor(provider, new Mock<ILogger<StreamProcessor>>().Object, new MessageTypeRegistry());

        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = Array.Empty<byte>() };

        var result = await processor.ProcessAsync(Array.Empty<byte>(), typeof(object), null, headers, envelope);

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_ByteStreamPacket_ReturnsHandled()
    {
        var services = new ServiceCollection();
        var provider = services.BuildServiceProvider();
        var processor = new StreamProcessor(provider, new Mock<ILogger<StreamProcessor>>().Object, new MessageTypeRegistry());

        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
            [HeaderKeys.SequenceId] = "seq-1",
            [HeaderKeys.PacketNumber] = "0"
        };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(object), null, headers, envelope);

        Assert.Equal(ProcessResult.Handled, result);
    }
}
