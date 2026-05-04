using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Locks in the Group F item 3 invariant: inbound copy loops eagerly decode byte[] header
/// values to string so downstream HeaderDecoder.Decode calls hit the string fast-path
/// instead of re-running Encoding.UTF8.GetString on every read.
/// </summary>
public sealed class InboundHeaderDecodeCachingTests
{
    [Fact]
    public void CopyInboundHeaders_ReplacesByteArrayValuesWithDecodedStrings()
    {
        var args = BuildDeliverArgs(new Dictionary<string, object?>
        {
            ["X-Trace"] = Encoding.UTF8.GetBytes("abc-123"),
            ["X-Count"] = 42,
            ["X-Empty"] = null,
            ["X-Plain"] = "already-string",
        });

        var copied = RabbitMqConsumerHost.CopyInboundHeadersForTests(args);

        Assert.IsType<string>(copied["X-Trace"]);
        Assert.Equal("abc-123", copied["X-Trace"]);
        Assert.Equal(42, copied["X-Count"]);
        Assert.False(copied.ContainsKey("X-Empty"));
        Assert.Equal("already-string", copied["X-Plain"]);
    }

    [Fact]
    public void HeaderDecoder_Decode_ReturnsCachedStringWithoutReDecoding_AfterEagerDecode()
    {
        var args = BuildDeliverArgs(new Dictionary<string, object?>
        {
            ["X-Trace"] = Encoding.UTF8.GetBytes("identity-test"),
        });
        var copied = RabbitMqConsumerHost.CopyInboundHeadersForTests(args);

        var first = HeaderDecoder.Decode(copied["X-Trace"]);
        var second = HeaderDecoder.Decode(copied["X-Trace"]);

        Assert.Equal("identity-test", first);
        Assert.Same(first, second);
        Assert.Same(copied["X-Trace"], first);
    }

    [Fact]
    public void InboundMessageProcessorCopy_ReplacesByteArrayValuesWithDecodedStrings()
    {
        var args = BuildDeliverArgs(new Dictionary<string, object?>
        {
            [HeaderKeys.TypeName] = Encoding.UTF8.GetBytes("My.Type.Name"),
            [HeaderKeys.MessageId] = Encoding.UTF8.GetBytes("msg-42"),
            ["X-Typed"] = true,
        });

        var copied = InboundMessageProcessor.CopyInboundHeadersForTests(args);

        Assert.IsType<string>(copied[HeaderKeys.TypeName]);
        Assert.Equal("My.Type.Name", copied[HeaderKeys.TypeName]);
        Assert.IsType<string>(copied[HeaderKeys.MessageId]);
        Assert.Equal("msg-42", copied[HeaderKeys.MessageId]);
        Assert.Equal(true, copied["X-Typed"]);
    }

    private static BasicDeliverEventArgs BuildDeliverArgs(IDictionary<string, object?> headers)
    {
        var props = new BasicProperties { Headers = headers };
        return new BasicDeliverEventArgs(
            consumerTag: "test-consumer",
            deliveryTag: 1,
            redelivered: false,
            exchange: string.Empty,
            routingKey: "test-queue",
            properties: props,
            body: ReadOnlyMemory<byte>.Empty);
    }
}
