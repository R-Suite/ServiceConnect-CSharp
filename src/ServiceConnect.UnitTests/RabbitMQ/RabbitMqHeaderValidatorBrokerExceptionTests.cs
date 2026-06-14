using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Pins the contract that a broker exception thrown by
/// <see cref="IMessageRetryHandler.HandleTerminalFailureAsync"/> during header
/// validation does not escape <see cref="RabbitMqHeaderValidator.ValidateAsync"/>.
/// The inbound message is permanently invalid regardless of whether the error-exchange
/// publish succeeds, so the caller must still receive a Reject result and ack the delivery.
/// </summary>
public sealed class RabbitMqHeaderValidatorBrokerExceptionTests
{
    private const long DefaultMaxBodySize = 64 * 1024;
    private const int DefaultMaxHeaderCount = 64;
    private const int DefaultMaxHeaderValueBytes = 8192;

    /// <summary>
    /// When the terminal-failure publish channel is closed, ValidateAsync must return a
    /// Reject result instead of propagating the broker exception. Without this guard the
    /// host's generic catch issues a nack-with-requeue, redelivering a permanently-invalid
    /// message for as long as the publish channel remains unhealthy.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_when_publish_channel_is_closed_returns_Reject_instead_of_propagating()
    {
        var retryHandler = new Mock<IMessageRetryHandler>(MockBehavior.Strict);
        retryHandler
            .Setup(r => r.HandleTerminalFailureAsync(
                It.IsAny<IChannel>(),
                It.IsAny<BasicDeliverEventArgs>(),
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<Exception>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new global::RabbitMQ.Client.Exceptions.AlreadyClosedException(
                new ShutdownEventArgs(ShutdownInitiator.Peer, 0, "broker reset")));

        var validator = BuildValidator(retryHandler.Object);
        var publishChannel = new Mock<IChannel>(MockBehavior.Loose).Object;

        // Rule 2 (missing type-name header): headers dict present but neither TypeName
        // nor FullTypeName set, so the validator routes to HandleTerminalFailureAsync.
        var args = MakeArgs(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["X-Other"] = "unrelated",
        });

        var result = await validator.ValidateAsync(args, publishChannel, CopyHeaders(args), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("missing type-name header", result.RejectReason);
    }

    /// <summary>
    /// When BasicPublishAsync(mandatory:true) targets an unroutable exchange — typical of
    /// error-exchange topology drift — RabbitMQ.Client throws PublishException. The
    /// broker-exception guard must swallow it and return Reject so the inbound delivery is
    /// acked rather than nacked-with-requeue (which would loop the same permanently-invalid
    /// message indefinitely).
    /// </summary>
    [Fact]
    public async Task ValidateAsync_when_publish_throws_PublishException_returns_Reject()
    {
        var retryHandler = new Mock<IMessageRetryHandler>(MockBehavior.Strict);
        retryHandler
            .Setup(r => r.HandleTerminalFailureAsync(
                It.IsAny<IChannel>(),
                It.IsAny<BasicDeliverEventArgs>(),
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<Exception>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new global::RabbitMQ.Client.Exceptions.PublishException(1, false));

        var validator = BuildValidator(retryHandler.Object);
        var publishChannel = new Mock<IChannel>(MockBehavior.Loose).Object;

        // Rule 2 (missing type-name header): headers dict present but neither TypeName
        // nor FullTypeName set, so the validator routes to HandleTerminalFailureAsync.
        var args = MakeArgs(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["X-Other"] = "unrelated",
        });

        var result = await validator.ValidateAsync(args, publishChannel, CopyHeaders(args), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("missing type-name header", result.RejectReason);
    }

    /// <summary>
    /// OperationCanceledException must propagate from ValidateAsync so cooperative
    /// shutdown is distinguishable from a broker swallow. This counter-pins that the
    /// new broker-exception guard does not accidentally swallow cancellation.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_when_OCE_during_terminal_failure_propagates()
    {
        var retryHandler = new Mock<IMessageRetryHandler>(MockBehavior.Strict);
        retryHandler
            .Setup(r => r.HandleTerminalFailureAsync(
                It.IsAny<IChannel>(),
                It.IsAny<BasicDeliverEventArgs>(),
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<Exception>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var validator = BuildValidator(retryHandler.Object);
        var publishChannel = new Mock<IChannel>(MockBehavior.Loose).Object;

        var args = MakeArgs(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["X-Other"] = "unrelated",
        });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => validator.ValidateAsync(args, publishChannel, CopyHeaders(args), CancellationToken.None));
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private static RabbitMqHeaderValidator BuildValidator(IMessageRetryHandler retryHandler)
        => new(
            retryHandler,
            DefaultMaxBodySize,
            DefaultMaxHeaderCount,
            DefaultMaxHeaderValueBytes,
            shutdownPublishTokenFactory: () => CancellationToken.None,
            logger: NullLogger.Instance);

    private static BasicDeliverEventArgs MakeArgs(IDictionary<string, object?>? headers = null, byte[]? body = null)
        => new(
            consumerTag: "ct",
            deliveryTag: 1,
            redelivered: false,
            exchange: "",
            routingKey: "q",
            properties: new BasicProperties { Headers = headers },
            body: body ?? [1]);

    private static Dictionary<string, object> CopyHeaders(BasicDeliverEventArgs args)
    {
        var copy = new Dictionary<string, object>(StringComparer.Ordinal);
        var src = args.BasicProperties.Headers;
        if (src == null)
        {
            return copy;
        }
        foreach (var kvp in src)
        {
            if (kvp.Value is not null)
            {
                copy[kvp.Key] = kvp.Value;
            }
        }
        return copy;
    }
}
