using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Verifies that Producer enforces MaximumMessageSize on all outbound publish/send methods
/// before attempting any network I/O.
/// </summary>
public class ProducerSizeLimitTests
{
    private const long SmallLimit = 10; // 10 bytes — easy to exceed in tests

    private static Producer MakeProducer(long maxSize = SmallLimit)
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        var settings = new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.MessageSize] = maxSize,
            [RabbitMQSettingKeys.RetryCount] = (ushort)0,
            [RabbitMQSettingKeys.RetrySeconds] = (ushort)0,
        };
        transport.SetupGet(t => t.ClientSettings).Returns(settings);

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("q");

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        return new Producer(transport.Object, queue.Object, bus.Object, NullLogger<Producer>.Instance);
    }

    private static byte[] OversizedMessage(long limit) => new byte[limit + 1];
    private static byte[] ExactSizeMessage(long limit) => new byte[limit];

    // ─── PublishAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task PublishAsync_OversizedMessage_ThrowsInvalidOperationException()
    {
        var producer = MakeProducer();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => producer.PublishAsync(typeof(object), OversizedMessage(SmallLimit)));

        Assert.Contains($"{SmallLimit + 1} bytes", ex.Message);
        Assert.Contains($"{SmallLimit} bytes", ex.Message);
    }

    [Fact]
    public async Task PublishAsync_ExactLimitMessage_DoesNotThrow()
    {
        // Exact-size message should pass the guard. The method will then attempt
        // network I/O which will fail because there is no broker — but the
        // InvalidOperationException from the size guard must NOT be thrown.
        var producer = MakeProducer();
        var ex = await Record.ExceptionAsync(
            () => producer.PublishAsync(typeof(object), ExactSizeMessage(SmallLimit)));

        Assert.False(ex is InvalidOperationException ioex && ioex.Message.Contains("exceeds maximum"),
            "Size guard should not fire for a message exactly at the limit.");
    }

    // ─── SendAsync(Type, byte[], …) ─────────────────────────────────────────

    [Fact]
    public async Task SendAsync_ByType_OversizedMessage_ThrowsInvalidOperationException()
    {
        var producer = MakeProducer();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => producer.SendAsync(typeof(object), OversizedMessage(SmallLimit)));

        Assert.Contains($"{SmallLimit + 1} bytes", ex.Message);
    }

    [Fact]
    public async Task SendAsync_ByType_ExactLimitMessage_DoesNotThrow()
    {
        var producer = MakeProducer();
        var ex = await Record.ExceptionAsync(
            () => producer.SendAsync(typeof(object), ExactSizeMessage(SmallLimit)));

        Assert.False(ex is InvalidOperationException ioex && ioex.Message.Contains("exceeds maximum"),
            "Size guard should not fire for a message exactly at the limit.");
    }

    // ─── SendAsync(string endPoint, Type, byte[], …) ────────────────────────

    [Fact]
    public async Task SendAsync_ByEndpoint_OversizedMessage_ThrowsInvalidOperationException()
    {
        var producer = MakeProducer();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => producer.SendAsync("some-queue", typeof(object), OversizedMessage(SmallLimit)));

        Assert.Contains($"{SmallLimit + 1} bytes", ex.Message);
    }

    [Fact]
    public async Task SendAsync_ByEndpoint_ExactLimitMessage_DoesNotThrow()
    {
        var producer = MakeProducer();
        var ex = await Record.ExceptionAsync(
            () => producer.SendAsync("some-queue", typeof(object), ExactSizeMessage(SmallLimit)));

        Assert.False(ex is InvalidOperationException ioex && ioex.Message.Contains("exceeds maximum"),
            "Size guard should not fire for a message exactly at the limit.");
    }

    // ─── SendBytesAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task SendBytesAsync_OversizedPacket_ThrowsInvalidOperationException()
    {
        var producer = MakeProducer();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => producer.SendBytesAsync("some-queue", typeof(byte[]), OversizedMessage(SmallLimit)));

        Assert.Contains($"{SmallLimit + 1} bytes", ex.Message);
    }

    [Fact]
    public async Task SendBytesAsync_ExactLimitPacket_DoesNotThrow()
    {
        var producer = MakeProducer();
        var ex = await Record.ExceptionAsync(
            () => producer.SendBytesAsync("some-queue", typeof(byte[]), ExactSizeMessage(SmallLimit)));

        Assert.False(ex is InvalidOperationException ioex && ioex.Message.Contains("exceeds maximum"),
            "Size guard should not fire for a packet exactly at the limit.");
    }

    // ─── Endpoint validation ────────────────────────────────────────────────
    // Blank endpoints publish to the default exchange with mandatory:false and
    // are silently dropped. SendAsync validates this; SendBytesAsync must too.

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public async Task SendBytesAsync_BlankEndpoint_ThrowsArgumentException(string endpoint)
    {
        var producer = MakeProducer();
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => producer.SendBytesAsync(endpoint, typeof(byte[]), new byte[] { 1 }));

        Assert.Contains("empty endpoint", ex.Message);
    }

    [Fact]
    public async Task SendBytesAsync_NullEndpoint_ThrowsArgumentException()
    {
        var producer = MakeProducer();
        await Assert.ThrowsAsync<ArgumentException>(
            () => producer.SendBytesAsync(null!, typeof(byte[]), new byte[] { 1 }));
    }

    // ─── MaximumMessageSize property honours config ──────────────────────────

    [Fact]
    public void MaximumMessageSize_ReflectsClientSetting()
    {
        var producer = MakeProducer(maxSize: 128 * 1024);
        Assert.Equal(128 * 1024, producer.MaximumMessageSize);
    }

    [Fact]
    public void MaximumMessageSize_DefaultsTo64KiB_WhenSettingAbsent()
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>()); // no MessageSize entry

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("q");

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        var producer = new Producer(transport.Object, queue.Object, bus.Object, NullLogger<Producer>.Instance);

        Assert.Equal(64 * 1024, producer.MaximumMessageSize);
    }
}
