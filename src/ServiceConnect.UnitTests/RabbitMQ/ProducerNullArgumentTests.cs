using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Verifies that all public publish/send entry points reject null type or message
/// arguments with <see cref="ArgumentNullException"/> before touching the message
/// body — preventing NullReferenceException from leaking through to callers.
/// </summary>
public sealed class ProducerNullArgumentTests
{
    private static Producer CreateProducer()
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("test-queue");

        var busConfig = new Mock<IBusConfiguration>();

        return new Producer(transport.Object, queueConfig.Object, busConfig.Object, NullLogger<Producer>.Instance);
    }

    [Fact]
    public async Task PublishAsync_NullType_Throws()
    {
        var producer = CreateProducer();
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            producer.PublishAsync(null!, [1, 2, 3]));
        Assert.Equal("type", ex.ParamName);
    }

    [Fact]
    public async Task PublishAsync_NullMessage_Throws()
    {
        var producer = CreateProducer();
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            producer.PublishAsync(typeof(string), null!));
        Assert.Equal("message", ex.ParamName);
    }

    [Fact]
    public async Task SendAsyncByType_NullType_Throws()
    {
        var producer = CreateProducer();
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            producer.SendAsync(null!, [1, 2, 3]));
        Assert.Equal("type", ex.ParamName);
    }

    [Fact]
    public async Task SendAsyncByType_NullMessage_Throws()
    {
        var producer = CreateProducer();
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            producer.SendAsync(typeof(string), null!));
        Assert.Equal("message", ex.ParamName);
    }

    [Fact]
    public async Task SendAsyncToEndpoint_NullType_Throws()
    {
        var producer = CreateProducer();
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            producer.SendAsync("ep", null!, [1, 2, 3]));
        Assert.Equal("type", ex.ParamName);
    }

    [Fact]
    public async Task SendAsyncToEndpoint_NullMessage_Throws()
    {
        var producer = CreateProducer();
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            producer.SendAsync("ep", typeof(string), null!));
        Assert.Equal("message", ex.ParamName);
    }

    [Fact]
    public async Task SendBytesAsync_NullType_Throws()
    {
        var producer = CreateProducer();
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            producer.SendBytesAsync("ep", null!, [1, 2, 3]));
        Assert.Equal("type", ex.ParamName);
    }

    [Fact]
    public async Task SendBytesAsync_NullPacket_Throws()
    {
        var producer = CreateProducer();
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            producer.SendBytesAsync("ep", typeof(string), null!));
        Assert.Equal("packet", ex.ParamName);
    }
}
