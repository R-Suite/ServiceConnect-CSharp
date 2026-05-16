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

        await stream.WriteAsync(new byte[] { 1, 2, 3, 4 });

        var send = _sends.Single();
        Assert.Equal(typeof(FakeStreamMsg), send.Type);
        Assert.False(string.IsNullOrWhiteSpace(send.Headers![HeaderKeys.SequenceId]));
        // Type-reserved headers are stamped server-side by the producer, not seeded by the stream.
        Assert.False(send.Headers!.ContainsKey(HeaderKeys.FullTypeName));
        Assert.False(send.Headers!.ContainsKey(HeaderKeys.TypeName));
        Assert.False(send.Headers!.ContainsKey(HeaderKeys.MessageType));
    }

    [Fact]
    public async Task WriteAsync_SliceViaAsMemory_SendsCorrectBytes()
    {
        // Callers that previously used WriteAsync(buffer, offset, count) now slice
        // via buffer.AsMemory(offset, count) before calling WriteAsync.
        await using var stream = new MessageBusWriteStream(_producer.Object, "dest", typeof(FakeStreamMsg));
        var buffer = new byte[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19 };

        await stream.WriteAsync(buffer.AsMemory(2, 4));

        var captured = _sends.Single().Payload;
        Assert.Equal(new byte[] { 12, 13, 14, 15 }, captured);
    }

    [Fact]
    public async Task WriteAsync_IncrementsPacketNumber_StartingAtZero()
    {
        await using var stream = new MessageBusWriteStream(_producer.Object, "dest", typeof(FakeStreamMsg));

        await stream.WriteAsync(new byte[] { 1 });
        await stream.WriteAsync(new byte[] { 2 });
        await stream.WriteAsync(new byte[] { 3 });

        Assert.Equal("0", _sends[0].Headers![HeaderKeys.PacketNumber]);
        Assert.Equal("1", _sends[1].Headers![HeaderKeys.PacketNumber]);
        Assert.Equal("2", _sends[2].Headers![HeaderKeys.PacketNumber]);
    }

    [Fact]
    public async Task WriteAsync_AfterClose_ThrowsObjectDisposedException()
    {
        await using var stream = new MessageBusWriteStream(_producer.Object, "dest", typeof(FakeStreamMsg));
        await stream.CloseAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => stream.WriteAsync(new byte[] { 1 }));
    }

    [Fact]
    public async Task CloseAsync_SendsEmptyPayloadWithLastPacketNumberHeader()
    {
        var stream = new MessageBusWriteStream(_producer.Object, "dest", typeof(FakeStreamMsg));
        await stream.WriteAsync(new byte[] { 1 });
        await stream.WriteAsync(new byte[] { 2 });

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
        var firstEx = await Assert.ThrowsAsync<InvalidOperationException>(() => stream.WriteAsync(new byte[] { 1 }));
        Assert.Equal("transport down", firstEx.Message);

        // Second write must not attempt another send: a successful retry would consume
        // packet number N+1, leaving packet N permanently missing from the reader's view.
        var secondEx = await Assert.ThrowsAsync<InvalidOperationException>(() => stream.WriteAsync(new byte[] { 2 }));
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

        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.WriteAsync(new byte[] { 1 }));

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
    public async Task CloseAsync_FaultDuringDrain_DoesNotShipClosePacket()
    {
        // Race: WriteAsync passes _closeStarted, increments _packetNumber, awaits SendBytesAsync.
        // CloseAsync starts concurrently — _faulted=0, falls through, enters the drain loop.
        // SendBytesAsync then throws (set _faulted=1 → finally decrements _inFlightWrites).
        // Drain exits cleanly, but _packetNumber now reflects a slot whose packet never shipped.
        // Without re-checking _faulted after the drain, CloseAsync would emit a close packet
        // declaring LastPacketNumber for the unreachable slot, leaving the reader unable to
        // satisfy IsComplete.
        var sendStarted = new TaskCompletionSource();
        var faultSend = new TaskCompletionSource();

        var producer = new Mock<IProducer>();
        producer
            .Setup(p => p.SendBytesAsync(
                It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                sendStarted.TrySetResult();
                await faultSend.Task.ConfigureAwait(false);
                throw new InvalidOperationException("transport down");
            });

        var stream = new MessageBusWriteStream(producer.Object, "dest", typeof(FakeStreamMsg));

        var writeTask = stream.WriteAsync(new byte[] { 1, 2, 3 });

        // Wait until the write has reserved its slot and is parked in SendBytesAsync.
        await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // CloseAsync now enters the drain (since _inFlightWrites == 1).
        var closeTask = stream.CloseAsync();

        // Give CloseAsync a chance to advance into the drain loop before we trigger the fault.
        // A short delay is sufficient — the drain spins on _inFlightWrites and yields via
        // SpinOnce, so the close call observes _faulted=0 and reaches the drain quickly.
        await Task.Delay(50);

        // Fault the in-flight send. WriteAsync's catch sets _faulted=1; finally decrements
        // _inFlightWrites to 0. The drain exits and CloseAsync re-checks the fault flag.
        faultSend.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => writeTask.WaitAsync(TimeSpan.FromSeconds(5)));
        await closeTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Exactly one SendBytesAsync — the failed data packet. No close packet must ship,
        // because its LastPacketNumber would point at the stranded slot.
        producer.Verify(p => p.SendBytesAsync(
                It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task WriteAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var stream = new MessageBusWriteStream(_producer.Object, "dest", typeof(FakeStreamMsg));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => stream.WriteAsync(new byte[10], cts.Token));
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
            stream.WriteAsync(new byte[] { 1, 2, 3 }, CancellationToken.None));

        var secondAttempt = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            stream.WriteAsync(new byte[] { 4, 5, 6 }, CancellationToken.None));
        Assert.Contains("faulted", secondAttempt.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CloseAsync_TokenCancelledDuringClosePacketSend_PropagatesOce()
    {
        // The close-packet send blocks until the token is cancelled. If CloseAsync does NOT
        // forward the token, the mock's WaitAsync(ct) receives CancellationToken.None and never
        // unblocks — the test would hang indefinitely. CloseAsync must forward cancellationToken
        // through to the producer's send call.
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
    public async Task CloseAsync_RetriesAfterTransientSendFailure()
    {
        // Pre-fix CloseAsync set _closedFlag=1 on entry and a SendBytesAsync throw left
        // the flag set with no close packet shipped — retry short-circuited. Post-fix
        // the flag is set only after SendBytesAsync returns; a transient failure leaves
        // _closedFlag=0 so the retry can complete the close.
        var attempts = 0;
        var producer = new Mock<IProducer>();
        producer
            .Setup(p => p.SendBytesAsync(
                It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new InvalidOperationException("transient");
                }
                return Task.CompletedTask;
            });

        var stream = new MessageBusWriteStream(producer.Object, "dest", typeof(FakeStreamMsg));

        // First close attempt fails.
        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.CloseAsync());

        // Second close attempt succeeds. Setting _closedFlag only after a successful
        // send means the retry can still emit the close packet; a CAS-set on entry would
        // short-circuit here and silently drop the close packet on the wire.
        await stream.CloseAsync();

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task WriteAsync_AfterFailedCloseAttempt_StillRejected()
    {
        // _closeStarted is set on first CloseAsync entry and never reset. Even if the
        // close itself failed, WriteAsync must reject — a stream that began closing
        // cannot un-close. This mirrors the fault-flag's permanence.
        var producer = new Mock<IProducer>();
        producer
            .Setup(p => p.SendBytesAsync(
                It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transient"));

        var stream = new MessageBusWriteStream(producer.Object, "dest", typeof(FakeStreamMsg));

        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.CloseAsync());

        // Subsequent Write must reject even though _closedFlag is still 0.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stream.WriteAsync(new byte[] { 1 }));
    }

    [Fact]
    public async Task CloseAsync_SuccessfulSecondCallIsIdempotent()
    {
        // After a successful close, a second CloseAsync call is a no-op (no second
        // close packet shipped).
        var stream = new MessageBusWriteStream(_producer.Object, "dest", typeof(FakeStreamMsg));
        await stream.WriteAsync(new byte[] { 1 });

        await stream.CloseAsync();
        await stream.CloseAsync();

        // One close packet, not two.
        var closes = _sends.Where(s => s.Headers!.ContainsKey(HeaderKeys.LastPacketNumber)).ToList();
        Assert.Single(closes);
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
        _ = stream.WriteAsync(new byte[] { 1 });

        // CloseAsync must abort the drain when the token is cancelled rather than
        // waiting up to the full 30-second CloseDrainTimeout.
        // Task.Delay surfaces cancellation as TaskCanceledException (subtype of OperationCanceledException).
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.CloseAsync(cts.Token));

        // Unblock the stalled write so the background task can complete cleanly.
        tcs.SetResult(true);
    }

    [Fact]
    public async Task DisposeAsync_WhenCloseGateIsWedged_CompletesAfterTimeout()
    {
        // Verify that DisposeAsync does not park indefinitely when _closeInProgress is
        // already held by a wedged holder. DisposeAsync must exit once the close budget
        // elapses rather than spinning forever on CancellationToken.None.
        //
        // Uses the internal constructor to inject a short timeout (200 ms) so the test
        // completes in well under a second instead of waiting the full 30-second budget.
        var closeField = typeof(MessageBusWriteStream)
            .GetField("_closeInProgress", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var stream = new MessageBusWriteStream(
            _producer.Object, "dest", typeof(FakeStreamMsg),
            TimeProvider.System, TimeSpan.FromMilliseconds(200));

        // Wedge the single-flight gate: simulate a holder that will never release.
        closeField.SetValue(stream, 1);

        // DisposeAsync must return — the 200 ms CTS fires and the OCE is swallowed.
        // Guard with a 5-second hard deadline so a regression parks xUnit rather than hanging.
        await stream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DisposeAsync_WhenDrainTimesOut_DoesNotLeakTimeoutException()
    {
        var producer = new Mock<IProducer>();
        // SendBytesAsync never completes within the test window — simulates a stalled writer.
        var tcs = new TaskCompletionSource();
        producer
            .Setup(p => p.SendBytesAsync(It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Returns(tcs.Task);

        var stream = new MessageBusWriteStream(
            producer.Object,
            "ep",
            typeof(byte[]),
            TimeProvider.System,
            TimeSpan.FromMilliseconds(100));  // short drain timeout for test

        // Start a write that won't complete.
        _ = stream.WriteAsync(new byte[] { 1 });

        // Give the write a moment to register in _inFlightWrites.
        await Task.Delay(20);

        // DisposeAsync must NOT throw despite the drain timeout firing.
        await stream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        // Release the producer mock so the test ends cleanly.
        tcs.SetResult();
    }
}

file class FakeStreamMsg : Message
{
    public FakeStreamMsg() : base(Guid.NewGuid()) { }
}
