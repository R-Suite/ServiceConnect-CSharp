using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Verifies that reserved transport headers are server-authoritative: caller-supplied values
/// must be silently overwritten by the producer for the five security-relevant keys.
/// </summary>
public class ProducerHeaderAuthorityTests
{
    private static Producer CreateProducer(string queueName = "my-queue")
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.RetryCount] = (ushort)0,
            [RabbitMQSettingKeys.RetrySeconds] = (ushort)0,
        });

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns(queueName);

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        return new Producer(transport.Object, queue.Object, bus.Object, NullLogger<Producer>.Instance);
    }

    private static void SetField<T>(Producer producer, string fieldName, T value)
    {
        typeof(Producer)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(producer, value);
    }

    /// <summary>
    /// Runs a SendAsync(endPoint, …) call with the given hostile header and captures the
    /// BasicProperties passed to BasicPublishAsync, returning the Headers dictionary.
    /// </summary>
    private static async Task<IDictionary<string, object?>> CaptureHeadersFromSendAsync(
        string hostileKey,
        string hostileValue,
        string endPoint = "target-queue")
    {
        var producer = CreateProducer();
        var channel = new Mock<IChannel>();
        IDictionary<string, object?>? captured = null;

        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, BasicProperties, ReadOnlyMemory<byte>, CancellationToken>(
                (_, _, _, props, _, _) => captured = props.Headers)
            .Returns(ValueTask.CompletedTask);

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        var hostileHeaders = new Dictionary<string, string>
        {
            [hostileKey] = hostileValue
        };

        await producer.SendAsync(endPoint, typeof(object), new byte[] { 1 }, headers: hostileHeaders);

        Assert.NotNull(captured);
        return captured!;
    }

    [Theory]
    [InlineData(HeaderKeys.DestinationAddress, "spoofed-queue")]
    [InlineData(HeaderKeys.MessageId, "00000000-0000-0000-0000-000000000000")]
    [InlineData(HeaderKeys.MessageType, "Spoofed")]
    [InlineData(HeaderKeys.TypeName, "Spoofed.Type")]
    [InlineData(HeaderKeys.FullTypeName, "Spoofed.Type, SpoofedAssembly")]
    public async Task SendAsync_CallerCannotOverride_ReservedHeader(string headerKey, string hostileValue)
    {
        var headers = await CaptureHeadersFromSendAsync(headerKey, hostileValue, endPoint: "target-queue");

        var actual = headers[headerKey]?.ToString();
        Assert.NotEqual(hostileValue, actual);
    }

    /// <summary>
    /// Runs SendBytesAsync with a hostile header and returns the captured BasicProperties.Headers.
    /// </summary>
    private static async Task<IDictionary<string, object?>> CaptureHeadersFromSendBytesAsync(
        string hostileKey,
        string hostileValue,
        Type logicalType)
    {
        var producer = CreateProducer();
        var channel = new Mock<IChannel>();
        IDictionary<string, object?>? captured = null;

        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, BasicProperties, ReadOnlyMemory<byte>, CancellationToken>(
                (_, _, _, props, _, _) => captured = props.Headers)
            .Returns(ValueTask.CompletedTask);

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        var hostileHeaders = new Dictionary<string, string>
        {
            [hostileKey] = hostileValue
        };

        await producer.SendBytesAsync("target-queue", logicalType, new byte[] { 1 }, headers: hostileHeaders);

        Assert.NotNull(captured);
        return captured!;
    }

    [Theory]
    [InlineData(HeaderKeys.DestinationAddress, "spoofed-queue")]
    [InlineData(HeaderKeys.MessageId, "00000000-0000-0000-0000-000000000000")]
    [InlineData(HeaderKeys.MessageType, "Spoofed")]
    [InlineData(HeaderKeys.TypeName, "Spoofed.Type")]
    [InlineData(HeaderKeys.FullTypeName, "Spoofed.Type, SpoofedAssembly")]
    public async Task SendBytesAsync_CallerCannotOverride_ReservedHeader(string headerKey, string hostileValue)
    {
        var headers = await CaptureHeadersFromSendBytesAsync(headerKey, hostileValue, logicalType: typeof(ProducerHeaderAuthorityTests));

        var actual = headers[headerKey]?.ToString();
        Assert.NotEqual(hostileValue, actual);
    }

    [Fact]
    public async Task SendBytesAsync_TypeHeaders_ComeFromLogicalTypeParameter()
    {
        var headers = await CaptureHeadersFromSendBytesAsync(HeaderKeys.TypeName, "hostile", logicalType: typeof(ProducerHeaderAuthorityTests));

        Assert.Equal(typeof(ProducerHeaderAuthorityTests).FullName, headers[HeaderKeys.TypeName]?.ToString());
        Assert.Equal(typeof(ProducerHeaderAuthorityTests).AssemblyQualifiedName, headers[HeaderKeys.FullTypeName]?.ToString());
        Assert.Equal(HeaderKeys.ByteStream, headers[HeaderKeys.MessageType]?.ToString());
    }
}
