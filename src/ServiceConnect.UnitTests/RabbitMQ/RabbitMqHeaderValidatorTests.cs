using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Unit tests for <see cref="RabbitMqHeaderValidator"/>. These exercise the four
/// pre-dispatch rules at the validator level so that test failures isolate the rule
/// being violated rather than dragging in the host's admission/ack/nack lifecycle.
/// Host-level tests in <c>RabbitMqConsumerHostHeaderSizeTests</c> still cover the
/// integration surface.
/// </summary>
public sealed class RabbitMqHeaderValidatorTests
{
    private const long DefaultMaxBodySize = 64 * 1024;
    private const int DefaultMaxHeaderCount = 64;
    private const int DefaultMaxHeaderValueBytes = 8192;

    [Fact]
    public async Task ValidateAsync_MissingTypeNameHeader_RejectsAndPublishesTerminalFailure()
    {
        var (validator, publishChannel, capturedExceptions) = BuildValidator();

        // Headers dictionary present but neither TypeName nor FullTypeName populated.
        var args = MakeArgs(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["X-Other"] = "value",
        });

        var result = await validator.ValidateAsync(args, publishChannel.Object, CopyHeaders(args), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("missing type-name header", result.RejectReason);
        var ex = Assert.Single(capturedExceptions);
        Assert.Contains("Message headers must contain type name", ex);
    }

    [Fact]
    public async Task ValidateAsync_NullHeaders_RejectsAsMissingTypeName()
    {
        var (validator, publishChannel, capturedExceptions) = BuildValidator();

        var args = MakeArgs(headers: null);

        var result = await validator.ValidateAsync(args, publishChannel.Object, CopyHeaders(args), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("missing type-name header", result.RejectReason);
        Assert.Single(capturedExceptions);
    }

    [Fact]
    public async Task ValidateAsync_OversizedBody_RejectsAndPublishesTerminalFailure()
    {
        var (validator, publishChannel, capturedExceptions) = BuildValidator(maxBodySize: 16);

        var args = MakeArgs(
            headers: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [HeaderKeys.FullTypeName] = "Foo.Bar",
            },
            body: new byte[32]);

        var result = await validator.ValidateAsync(args, publishChannel.Object, CopyHeaders(args), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("oversized body", result.RejectReason);
        var ex = Assert.Single(capturedExceptions);
        Assert.Contains("Inbound message size 32 bytes exceeds configured limit 16 bytes", ex);
    }

    [Fact]
    public async Task ValidateAsync_TooManyHeaders_RejectsAndPublishesTerminalFailure()
    {
        var (validator, publishChannel, capturedExceptions) = BuildValidator(maxHeaderCount: 4);

        var headers = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [HeaderKeys.FullTypeName] = "Foo.Bar",
            ["A"] = "1",
            ["B"] = "2",
            ["C"] = "3",
            ["D"] = "4",
        };
        var args = MakeArgs(headers);

        var result = await validator.ValidateAsync(args, publishChannel.Object, CopyHeaders(args), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("too many headers", result.RejectReason);
        var ex = Assert.Single(capturedExceptions);
        Assert.Contains("Inbound header count 5 exceeds configured limit 4", ex);
    }

    [Fact]
    public async Task ValidateAsync_OversizedByteArrayHeader_RejectsAndPublishesTerminalFailure()
    {
        var (validator, publishChannel, capturedExceptions) = BuildValidator();

        var args = MakeArgs(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [HeaderKeys.FullTypeName] = "Foo.Bar",
            ["X-Big-Bytes"] = new byte[DefaultMaxHeaderValueBytes + 1],
        });

        var result = await validator.ValidateAsync(args, publishChannel.Object, CopyHeaders(args), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("oversized header value", result.RejectReason);
        var ex = Assert.Single(capturedExceptions);
        Assert.Contains("X-Big-Bytes", ex);
        Assert.Contains((DefaultMaxHeaderValueBytes + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), ex);
    }

    [Fact]
    public async Task ValidateAsync_OversizedStringHeader_UsesUtf8ByteCountAndRejects()
    {
        var (validator, publishChannel, capturedExceptions) = BuildValidator();

        // ASCII chars are 1 byte each in UTF-8, so a string of length N has UTF-8 byte count N.
        const int OverLimit = DefaultMaxHeaderValueBytes + 1;
        var args = MakeArgs(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [HeaderKeys.FullTypeName] = "Foo.Bar",
            ["X-Big-String"] = new string('x', OverLimit),
        });

        var result = await validator.ValidateAsync(args, publishChannel.Object, CopyHeaders(args), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("oversized header value", result.RejectReason);
        var ex = Assert.Single(capturedExceptions);
        Assert.Contains("X-Big-String", ex);
        Assert.Contains(OverLimit.ToString(System.Globalization.CultureInfo.InvariantCulture), ex);
    }

    [Fact]
    public async Task ValidateAsync_AllRulesPass_AcceptsWithoutPublishing()
    {
        var (validator, publishChannel, capturedExceptions) = BuildValidator();

        var args = MakeArgs(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [HeaderKeys.FullTypeName] = "Foo.Bar",
            ["X-Small"] = "small",
        });

        var result = await validator.ValidateAsync(args, publishChannel.Object, CopyHeaders(args), CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Null(result.RejectReason);
        Assert.Empty(capturedExceptions);
        publishChannel.Verify(
            c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void Constructor_NullRetryHandler_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new RabbitMqHeaderValidator(
            retryHandler: null!,
            maxInboundMessageSize: DefaultMaxBodySize,
            maxHeaderCount: DefaultMaxHeaderCount,
            maxHeaderValueBytes: DefaultMaxHeaderValueBytes,
            shutdownPublishTokenFactory: () => CancellationToken.None));
    }

    [Fact]
    public void Constructor_NullShutdownPublishTokenFactory_Throws()
    {
        var retry = new MessageRetryHandler(3, "err", "q", NullLogger.Instance);
        Assert.Throws<ArgumentNullException>(() => new RabbitMqHeaderValidator(
            retryHandler: retry,
            maxInboundMessageSize: DefaultMaxBodySize,
            maxHeaderCount: DefaultMaxHeaderCount,
            maxHeaderValueBytes: DefaultMaxHeaderValueBytes,
            shutdownPublishTokenFactory: null!));
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private static (RabbitMqHeaderValidator Validator, Mock<IChannel> PublishChannel, List<string> CapturedExceptions) BuildValidator(
        long maxBodySize = DefaultMaxBodySize,
        int maxHeaderCount = DefaultMaxHeaderCount,
        int maxHeaderValueBytes = DefaultMaxHeaderValueBytes)
    {
        var capturedExceptions = new List<string>();
        var publishChannel = new Mock<IChannel>(MockBehavior.Loose);
        publishChannel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, BasicProperties, ReadOnlyMemory<byte>, CancellationToken>(
                (_, _, _, props, _, _) =>
                {
                    if (props.Headers != null &&
                        props.Headers.TryGetValue(HeaderKeys.Exception, out var raw) &&
                        raw is not null)
                    {
                        var json = raw switch
                        {
                            string s => s,
                            byte[] b => System.Text.Encoding.UTF8.GetString(b),
                            _ => raw.ToString() ?? string.Empty,
                        };
                        capturedExceptions.Add(json);
                    }
                })
            .Returns(ValueTask.CompletedTask);

        var retry = new MessageRetryHandler(3, "err", "q", NullLogger.Instance);
        var validator = new RabbitMqHeaderValidator(
            retry,
            maxBodySize,
            maxHeaderCount,
            maxHeaderValueBytes,
            shutdownPublishTokenFactory: () => CancellationToken.None);

        return (validator, publishChannel, capturedExceptions);
    }

    private static BasicDeliverEventArgs MakeArgs(IDictionary<string, object?>? headers = null, byte[]? body = null)
        => new(
            consumerTag: "ct",
            deliveryTag: 1,
            redelivered: false,
            exchange: "",
            routingKey: "q",
            properties: new BasicProperties { Headers = headers },
            body: body ?? [1]);

    // The validator takes the copied headers from the host. For test purposes we just allocate
    // a fresh dictionary that mirrors the input — the validator only forwards it to
    // MessageRetryHandler for the Exception-header stamp, so faithful copy semantics are not
    // required (these tests don't verify CopyInboundHeaders' eager-decode invariant).
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
            if (kvp.Value is null)
            {
                continue;
            }
            copy[kvp.Key] = kvp.Value;
        }
        return copy;
    }
}
