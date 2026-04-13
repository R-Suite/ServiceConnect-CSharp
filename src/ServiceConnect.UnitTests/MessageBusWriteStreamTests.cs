using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MessageBusWriteStreamTests
{
    private readonly Mock<IProducer> _producer = new();
    private readonly List<(string Endpoint, byte[] Payload, Dictionary<string, string>? Headers)> _sends = new();

    public MessageBusWriteStreamTests()
    {
        _producer
            .Setup(p => p.SendBytesAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<Dictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, byte[], Dictionary<string, string>?, CancellationToken>((ep, bytes, headers, _) =>
                _sends.Add((ep, bytes, headers)))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task WriteAsync_PopulatesBaseHeaders_WithSequenceIdTypeNameAndMessageType()
    {
        await using var stream = new MessageBusWriteStream(_producer.Object, "dest", typeof(FakeStreamMsg));

        await stream.WriteAsync([1, 2, 3, 4], 0, 4);

        var headers = _sends.Single().Headers!;
        Assert.False(string.IsNullOrWhiteSpace(headers[HeaderKeys.SequenceId]));
        Assert.Equal(typeof(FakeStreamMsg).AssemblyQualifiedName, headers[HeaderKeys.FullTypeName]);
        Assert.Equal(typeof(FakeStreamMsg).FullName, headers[HeaderKeys.TypeName]);
        Assert.Equal(HeaderKeys.ByteStream, headers[HeaderKeys.MessageType]);
    }

    [Fact]
    public async Task WriteAsync_CopiesSubArray_UsingOffsetAndCount()
    {
        await using var stream = new MessageBusWriteStream(_producer.Object, "dest", typeof(FakeStreamMsg));
        var buffer = new byte[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19 };

        await stream.WriteAsync(buffer, offset: 2, count: 4);

        var captured = _sends.Single().Payload;
        Assert.Equal(new byte[] { 12, 13, 14, 15 }, captured);
    }

    [Fact]
    public async Task WriteAsync_IncrementsPacketNumber_StartingAtZero()
    {
        await using var stream = new MessageBusWriteStream(_producer.Object, "dest", typeof(FakeStreamMsg));

        await stream.WriteAsync([1], 0, 1);
        await stream.WriteAsync([2], 0, 1);
        await stream.WriteAsync([3], 0, 1);

        Assert.Equal("0", _sends[0].Headers![HeaderKeys.PacketNumber]);
        Assert.Equal("1", _sends[1].Headers![HeaderKeys.PacketNumber]);
        Assert.Equal("2", _sends[2].Headers![HeaderKeys.PacketNumber]);
    }

    [Fact]
    public async Task WriteAsync_AfterClose_ThrowsObjectDisposedException()
    {
        var stream = new MessageBusWriteStream(_producer.Object, "dest", typeof(FakeStreamMsg));
        await stream.CloseAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => stream.WriteAsync([1], 0, 1));
    }

    [Fact]
    public async Task CloseAsync_SendsEmptyPayloadWithLastPacketNumberHeader()
    {
        var stream = new MessageBusWriteStream(_producer.Object, "dest", typeof(FakeStreamMsg));
        await stream.WriteAsync([1], 0, 1);
        await stream.WriteAsync([2], 0, 1);

        await stream.CloseAsync();

        var closeSend = _sends.Last();
        Assert.Empty(closeSend.Payload);
        Assert.Equal("2", closeSend.Headers![HeaderKeys.PacketNumber]);
        Assert.Equal("2", closeSend.Headers![HeaderKeys.LastPacketNumber]);
    }

    [Fact]
    public async Task CloseAsync_IsIdempotent()
    {
        var stream = new MessageBusWriteStream(_producer.Object, "dest", typeof(FakeStreamMsg));

        await stream.CloseAsync();
        await stream.CloseAsync();

        // One close-marker send; no additional calls on second CloseAsync.
        Assert.Single(_sends);
    }

    [Fact]
    public async Task DisposeAsync_CallsCloseAsync()
    {
        var stream = new MessageBusWriteStream(_producer.Object, "dest", typeof(FakeStreamMsg));

        await stream.DisposeAsync();

        // DisposeAsync produced the close-marker send.
        Assert.Single(_sends);
        Assert.Empty(_sends[0].Payload);
        Assert.Contains(HeaderKeys.LastPacketNumber, _sends[0].Headers!.Keys);
    }
}

file class FakeStreamMsg : Message
{
    public FakeStreamMsg() : base(Guid.NewGuid()) { }
}
