using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Verifies that all public publish/send entry points reject null type arguments
/// with <see cref="ArgumentNullException"/> before touching the message body.
/// Body parameters are <see cref="ReadOnlyMemory{T}"/> (a value type), so null is
/// not representable; null-body tests are not applicable.
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
            producer.PublishAsync(null!, new byte[] { 1, 2, 3 }));
        Assert.Equal("type", ex.ParamName);
    }

    [Fact]
    public async Task SendAsyncByType_NullType_Throws()
    {
        var producer = CreateProducer();
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            producer.SendAsync(null!, new byte[] { 1, 2, 3 }));
        Assert.Equal("type", ex.ParamName);
    }

    [Fact]
    public async Task SendAsyncToEndpoint_NullType_Throws()
    {
        var producer = CreateProducer();
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            producer.SendAsync("ep", null!, new byte[] { 1, 2, 3 }));
        Assert.Equal("type", ex.ParamName);
    }

    [Fact]
    public async Task SendBytesAsync_NullType_Throws()
    {
        var producer = CreateProducer();
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            producer.SendBytesAsync("ep", null!, new byte[] { 1, 2, 3 }));
        Assert.Equal("type", ex.ParamName);
    }
}
