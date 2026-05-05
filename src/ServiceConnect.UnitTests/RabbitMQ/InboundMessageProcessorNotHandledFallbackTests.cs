using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class InboundMessageProcessorNotHandledFallbackTests
{
    [Fact]
    public async Task ProcessAsync_NotHandled_FullTypeNameNullFallsBackToTypeName()
    {
        // Regression guard for the not-handled fallback type-name resolution. Note: the production
        // fix at InboundMessageProcessor.cs:169-172 (`|| typeNameRaw is null`) is defensive symmetry
        // with the line-87 pattern. The internal `headers` dict is built from
        // args.BasicProperties.Headers via a loop that filters out null-valued entries (lines 60-66
        // of InboundMessageProcessor), so a wire-headers `FullTypeName=null` never reaches the
        // fallback site. This test exercises the resolved type name in the not-handled exception
        // payload; it does NOT exercise the fix in isolation. The fix protects against any future
        // path that bypasses the upstream null filter.
        BasicProperties? capturedProps = null;
        var channel = new Mock<IChannel>();
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, BasicProperties, ReadOnlyMemory<byte>, CancellationToken>(
                (_, _, _, p, _, _) => capturedProps = p)
            .Returns(ValueTask.CompletedTask);

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.AuditingEnabled).Returns(false);
        queueConfig.SetupGet(q => q.AuditQueueName).Returns("audit");
        queueConfig.SetupGet(q => q.AuditRoutingKey).Returns(string.Empty);
        queueConfig.SetupGet(q => q.QueueName).Returns("main-q");

        var auditPublisher = new MessageAuditPublisher(queueConfig.Object);
        var retryHandler = new MessageRetryHandler(maxRetries: 3, errorExchange: "error.exchange", consumerQueueName: "main-q", NullLogger.Instance);

        var processor = new InboundMessageProcessor(
            consumerEventHandler: (_, _, _, _) =>
                Task.FromResult(new ConsumeEventResult { Success = true, NotHandled = true }),
            retryHandler: retryHandler,
            auditPublisher: auditPublisher,
            queueConfiguration: queueConfig.Object,
            timeProvider: TimeProvider.System,
            logger: NullLogger.Instance,
            retryQueueName: "main-q.Retries",
            errorsDisabled: false,
            deadLetterUnhandledMessages: true,
            includeMachineNameInHeaders: false,
            shutdownTimedOut: () => false,
            shutdownPublishToken: () => CancellationToken.None);

        // FullTypeName key present in wire headers but value is null; TypeName carries
        // the real type. The null-filtering loop drops FullTypeName from the internal
        // headers dict, so TryGetValue misses it and the TypeName fallback is used.
        // After the || typeNameRaw is null fix, an explicit null entry (if ever present
        // in the dict) also triggers the fallback.
        var props = new BasicProperties
        {
            Headers = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [HeaderKeys.FullTypeName] = null,
                [HeaderKeys.TypeName] = "Foo.Bar",
            },
        };

        var args = new BasicDeliverEventArgs(
            consumerTag: "ct",
            deliveryTag: 1,
            redelivered: false,
            exchange: "",
            routingKey: "main-q",
            properties: props,
            body: new byte[] { 1 });

        await processor.ProcessAsync(channel.Object, args, CancellationToken.None);

        Assert.NotNull(capturedProps);
        Assert.NotNull(capturedProps.Headers);
        Assert.True(capturedProps.Headers.TryGetValue(HeaderKeys.Exception, out var exJson));
        Assert.NotNull(exJson);
        var json = Assert.IsType<string>(exJson);
        // The exception message must include the resolved type name, not the "<unknown>"
        // sentinel that would appear if neither FullTypeName nor TypeName resolved.
        Assert.Contains("Foo.Bar", json);
        Assert.DoesNotContain("<unknown>", json);
    }
}
