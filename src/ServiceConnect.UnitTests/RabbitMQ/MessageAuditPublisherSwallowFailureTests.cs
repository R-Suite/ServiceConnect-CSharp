using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Audit publish is best-effort: a failure publishing the audit copy must not propagate
/// into the consumer pipeline (which would nack-with-requeue and re-run the handler) and
/// must not throw on the cancellation path.
/// </summary>
public class MessageAuditPublisherSwallowFailureTests
{
    private static BasicDeliverEventArgs MakeArgs()
    {
        var props = new BasicProperties();
        return new BasicDeliverEventArgs("tag", 1, false, "", "q", props, new byte[] { 1, 2, 3 });
    }

    private static Mock<IQueueConfiguration> MakeQueueCfg()
    {
        var cfg = new Mock<IQueueConfiguration>();
        cfg.SetupGet(c => c.AuditingEnabled).Returns(true);
        cfg.SetupGet(c => c.AuditQueueName).Returns("audit");
        return cfg;
    }

    [Fact]
    public async Task PublishAuditIfEnabledAsync_BasicPublishThrows_SwallowsAndLogsWarning()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("broker quota exceeded"));

        // MessageAuditPublisher is internal, so Castle DynamicProxy can't proxy
        // ILogger<MessageAuditPublisher> from the test assembly. Use a hand-rolled
        // capturing logger instead of Mock<ILogger<>> to keep the test self-contained.
        var capturingLogger = new CapturingLogger<MessageAuditPublisher>();
        var publisher = new MessageAuditPublisher(MakeQueueCfg().Object, capturingLogger);
        var headers = new Dictionary<string, object> { [HeaderKeys.MessageType] = "SomeMessage" };

        // Must not throw — audit failure is swallowed.
        await publisher.PublishAuditIfEnabledAsync(channel.Object, MakeArgs(), headers);

        Assert.Contains(capturingLogger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Audit publish failed", StringComparison.Ordinal));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }

    [Fact]
    public async Task PublishAuditIfEnabledAsync_BasicPublishThrowsOperationCanceled_Propagates()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var publisher = new MessageAuditPublisher(MakeQueueCfg().Object, NullLogger<MessageAuditPublisher>.Instance);
        var headers = new Dictionary<string, object> { [HeaderKeys.MessageType] = "SomeMessage" };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => publisher.PublishAuditIfEnabledAsync(channel.Object, MakeArgs(), headers));
    }

    [Fact]
    public async Task PublishAuditIfEnabledAsync_PreservesAllSourceBasicProperties()
    {
        // The audit path uses BasicPropertiesCopier (field-by-field copy) rather than the
        // BasicProperties copy-constructor, mirroring MessageRetryHandler. Adding a new
        // AMQP BASIC field to the copier without updating this assertion is a silent
        // regression — the field would be dropped from every audit publish.
        BasicProperties? capturedProps = null;
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, BasicProperties, ReadOnlyMemory<byte>, CancellationToken>(
                (_, _, _, p, _, _) => capturedProps = p)
            .Returns(ValueTask.CompletedTask);

        var sourceProps = new BasicProperties
        {
            ContentType = "application/json",
            ContentEncoding = "utf-8",
            DeliveryMode = DeliveryModes.Persistent,
            Priority = (byte)5,
            CorrelationId = "corr-audit",
            ReplyTo = "reply.queue",
            Expiration = "60000",
            MessageId = "msg-audit",
            Timestamp = new AmqpTimestamp(1234567890),
            Type = "AuditMsg",
            UserId = "guest",
            AppId = "test-app",
            ClusterId = "cluster-1",
        };
        var args = new BasicDeliverEventArgs("ct", 1, false, "", "q", sourceProps, new byte[] { 1, 2, 3 });

        var publisher = new MessageAuditPublisher(MakeQueueCfg().Object, NullLogger<MessageAuditPublisher>.Instance);
        var headers = new Dictionary<string, object> { [HeaderKeys.MessageType] = "AuditMsg" };

        await publisher.PublishAuditIfEnabledAsync(channel.Object, args, headers);

        Assert.NotNull(capturedProps);
        Assert.Equal("application/json", capturedProps.ContentType);
        Assert.Equal("utf-8", capturedProps.ContentEncoding);
        Assert.Equal(DeliveryModes.Persistent, capturedProps.DeliveryMode);
        Assert.Equal((byte)5, capturedProps.Priority);
        Assert.Equal("corr-audit", capturedProps.CorrelationId);
        Assert.Equal("reply.queue", capturedProps.ReplyTo);
        Assert.Equal("60000", capturedProps.Expiration);
        Assert.Equal("msg-audit", capturedProps.MessageId);
        Assert.Equal(new AmqpTimestamp(1234567890), capturedProps.Timestamp);
        Assert.Equal("AuditMsg", capturedProps.Type);
        // UserId / AppId / ClusterId are deliberately dropped on republish — see
        // BasicPropertiesCopier's xmldoc for the validated_user_id rationale.
        Assert.False(capturedProps.IsUserIdPresent());
        Assert.False(capturedProps.IsAppIdPresent());
        Assert.False(capturedProps.IsClusterIdPresent());
    }
}
