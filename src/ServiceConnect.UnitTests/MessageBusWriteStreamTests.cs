using System.Reflection;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MessageBusWriteStreamTests
{
    private readonly Mock<IProducer> _producer = new();
    private readonly List<(string Endpoint, Type Type, byte[] Payload, IReadOnlyDictionary<string, string>? Headers)> _sends = [];

    public MessageBusWriteStreamTests()
    {
        _producer
            .Setup(p => p.SendBytesAsync(
                It.IsAny<string>(),
                It.IsAny<Type>(),
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Type, ReadOnlyMemory<byte>, IReadOnlyDictionary<string, string>?, CancellationToken>((ep, type, bytes, headers, _) =>
                _sends.Add((ep, type, bytes.ToArray(), headers)))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task WriteAsync_SeedsSequenceIdHeader_AndPassesMessageTypeToProducer()
    {
        await using var stream = new MessageBusWriteStream(_producer.Object, "dest", typeof(FakeStreamMsg));

        await stream.WriteAsync([1, 2, 3, 4], 0, 4);

        var send = _sends.Single();
        Assert.Equal(typeof(FakeStreamMsg), send.Type);
        Assert.False(string.IsNullOrWhiteSpace(send.Headers![HeaderKeys.SequenceId]));
        // Type-reserved headers are stamped server-side by the producer, not seeded by the stream.
        Assert.False(send.Headers!.ContainsKey(HeaderKeys.FullTypeName));
        Assert.False(send.Headers!.ContainsKey(HeaderKeys.TypeName));
        Assert.False(send.Headers!.ContainsKey(HeaderKeys.MessageType));
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
        await using var stream = new MessageBusWriteStream(_producer.Object, "dest", typeof(FakeStreamMsg));
        await stream.CloseAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => stream.WriteAsync([1], 0, 1));
    }

    [Fact]
    public async Task WriteAsync_NullBuffer_ThrowsArgumentNullException()
    {
        await using var stream = new MessageBusWriteStream(_producer.Object, "dest", typeof(FakeStreamMsg));

        await Assert.ThrowsAsync<ArgumentNullException>(() => stream.WriteAsync(null!, 0, 0));
    }

    [Fact]
    public async Task WriteAsync_InvalidOffset_ThrowsArgumentOutOfRangeException()
    {
        await using var stream = new MessageBusWriteStream(_producer.Object, "dest", typeof(FakeStreamMsg));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => stream.WriteAsync([1, 2], 3, 0));
    }

    [Fact]
    public async Task WriteAsync_InvalidCount_ThrowsArgumentOutOfRangeException()
    {
        await using var stream = new MessageBusWriteStream(_producer.Object, "dest", typeof(FakeStreamMsg));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => stream.WriteAsync([1, 2], 1, 2));
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

        // First CloseAsync sent exactly one close-marker; second call was a no-op.
        Assert.Single(_sends);
        Assert.Empty(_sends[0].Payload);
        Assert.Contains(HeaderKeys.LastPacketNumber, _sends[0].Headers!.Keys);
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

    [Fact]
    public void CloseAsync_UsesInterlockedClosedFlag()
    {
        Assert.Null(typeof(MessageBusWriteStream).GetField("_closed", BindingFlags.Instance | BindingFlags.NonPublic));

        var closedFlag = typeof(MessageBusWriteStream).GetField("_closedFlag", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(closedFlag);
        Assert.Equal(typeof(int), closedFlag!.FieldType);
    }

    [Fact]
    public async Task WriteAsync_WhenSendFails_NextWriteThrows_AndDoesNotCallProducerAgain()
    {
        var failingProducer = new Mock<IProducer>();
        failingProducer
            .Setup(p => p.SendBytesAsync(
                It.IsAny<string>(),
                It.IsAny<Type>(),
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transport down"));

        await using var stream = new MessageBusWriteStream(failingProducer.Object, "dest", typeof(FakeStreamMsg));

        // First write surfaces the underlying failure.
        var firstEx = await Assert.ThrowsAsync<InvalidOperationException>(() => stream.WriteAsync([1], 0, 1));
        Assert.Equal("transport down", firstEx.Message);

        // Second write must not attempt another send: a successful retry would consume
        // packet number N+1, leaving packet N permanently missing from the reader's view.
        var secondEx = await Assert.ThrowsAsync<InvalidOperationException>(() => stream.WriteAsync([2], 0, 1));
        Assert.Contains("faulted", secondEx.Message, StringComparison.OrdinalIgnoreCase);

        failingProducer.Verify(
            p => p.SendBytesAsync(
                It.IsAny<string>(),
                It.IsAny<Type>(),
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CloseAsync_AfterSendFault_DoesNotSendClosePacket()
    {
        var failingProducer = new Mock<IProducer>();
        failingProducer
            .Setup(p => p.SendBytesAsync(
                It.IsAny<string>(),
                It.IsAny<Type>(),
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transport down"));

        await using var stream = new MessageBusWriteStream(failingProducer.Object, "dest", typeof(FakeStreamMsg));

        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.WriteAsync([1], 0, 1));

        // CloseAsync on a faulted stream must complete without throwing AND without sending
        // a close packet — a close packet on a stream with a hole would set LastPacketNumber
        // to a value the reader can never reach.
        var ex = await Record.ExceptionAsync(() => stream.CloseAsync());
        Assert.Null(ex);

        failingProducer.Verify(
            p => p.SendBytesAsync(
                It.IsAny<string>(),
                It.IsAny<Type>(),
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task WriteAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var stream = new MessageBusWriteStream(_producer.Object, "dest", typeof(FakeStreamMsg));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => stream.WriteAsync(new byte[10], 0, 10, cts.Token));
    }

    [Fact]
    public async Task WriteAsync_HeaderAllocationOrSendThrow_SetsFaultedFlag()
    {
        var producer = new Mock<IProducer>();
        var sendException = new InvalidOperationException("simulated post-increment throw");
        producer
            .Setup(p => p.SendBytesAsync(
                It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(sendException);

        var stream = new MessageBusWriteStream(producer.Object, "queue", typeof(string));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            stream.WriteAsync([1, 2, 3], 0, 3, CancellationToken.None));

        var secondAttempt = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            stream.WriteAsync([4, 5, 6], 0, 3, CancellationToken.None));
        Assert.Contains("faulted", secondAttempt.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CloseAsync_TokenCancelledDuringClosePacketSend_PropagatesOce()
    {
        // The close-packet send blocks until the token is cancelled. If CloseAsync does NOT
        // forward the token, the mock's WaitAsync(ct) receives CancellationToken.None and never
        // unblocks — the test would hang indefinitely. The fix is line 195: pass cancellationToken.
        var sendStarted = new TaskCompletionSource();
        var sendBlock = new TaskCompletionSource();

        var producer = new Mock<IProducer>();
        producer
            .Setup(p => p.SendBytesAsync(
                It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Returns(async (string ep, Type t, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string>? h, CancellationToken ct) =>
            {
                sendStarted.SetResult();
                // WaitAsync(ct) only cancels if ct is the real token; if ct is CancellationToken.None it blocks forever.
                await sendBlock.Task.WaitAsync(ct).ConfigureAwait(false);
            });

        var stream = new MessageBusWriteStream(producer.Object, "queue", typeof(string));
        using var cts = new CancellationTokenSource();

        var closeTask = stream.CloseAsync(cts.Token);
        await sendStarted.Task;

        // Cancel the token — only propagates to SendBytesAsync if CloseAsync forwarded it.
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => closeTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task CloseAsync_CancelledDuringDrain_ThrowsOperationCanceledException()
    {
        // A producer whose SendBytesAsync never completes simulates a stalled in-flight write.
        var tcs = new TaskCompletionSource<bool>();
        var stalledProducer = new Mock<IProducer>();
        stalledProducer
            .Setup(p => p.SendBytesAsync(
                It.IsAny<string>(),
                It.IsAny<Type>(),
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(tcs.Task.ContinueWith(_ => { }));

        var stream = new MessageBusWriteStream(stalledProducer.Object, "dest", typeof(FakeStreamMsg));

        // Fire off a write that will never complete, keeping _inFlightWrites > 0.
        _ = stream.WriteAsync([1], 0, 1);

        // CloseAsync must abort the drain when the token is cancelled rather than
        // waiting up to the full 30-second CloseDrainTimeout.
        // Task.Delay surfaces cancellation as TaskCanceledException (subtype of OperationCanceledException).
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.CloseAsync(cts.Token));

        // Unblock the stalled write so the background task can complete cleanly.
        tcs.SetResult(true);
    }
}

file class FakeStreamMsg : Message
{
    public FakeStreamMsg() : base(Guid.NewGuid()) { }
}
