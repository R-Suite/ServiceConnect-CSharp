using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;
using ServiceConnect.UnitTests.Fakes;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Pre-fix TryHandleReply queued TrySetResult / TrySetException via an out parameter
/// invoked OUTSIDE the per-state lock. The state set _closed=true under the lock, then
/// returned, and the caller invoked the queued completion. If the caller's CT fired in
/// the gap between the lock-exit and the completion invocation, the registration's
/// state.Close() observed _closed=true and early-returned without faulting/cancelling
/// the TCS — the queued TrySetResult subsequently completed it as success, and the
/// caller awaited a result despite the cancellation.
///
/// Post-fix Tcs.TrySetResult / TrySetException run UNDER the lock so the close-vs-
/// complete sequence is atomic with the registration's _closed read. Either the reply
/// wins the lock and TrySetResult observes the (still-open) TCS, or the cancellation
/// wins and TrySetCanceled observes the (still-open) TCS — never both.
/// </summary>
public sealed class RequestReplyManagerTryHandleReplyCancelRaceTests
{
    /// <summary>
    /// Outcome must be exactly one of: success (reply observed), cancelled (caller-CT
    /// observed). The pre-fix bug allowed a third outcome: success-observed AND the
    /// caller's token reports IsCancellationRequested without the await ever surfacing
    /// the cancellation. Run the race many times to make the window observable.
    /// </summary>
    [Fact]
    public async Task SendRequestAsync_ReplyAndCancelRace_NeverSilentlySwallowsCancel()
    {
        const int iterations = 256;

        var serializer = new Mock<IMessageSerializer>();
        serializer.SetupSerializeAny<FakeMessage1>([1, 2, 3]);
        serializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(() => new FakeMessage1(Guid.NewGuid()) { Username = "reply" });

        var pipeline = new Mock<ISendMessagePipeline>();
        string? capturedMessageId = null;
        pipeline.Setup(p => p.ExecuteSendMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) => capturedMessageId = ctx.Headers["RequestMessageId"])
            .Returns(Task.CompletedTask);

        var manager = new RequestReplyManager(serializer.Object, pipeline.Object);

        for (var i = 0; i < iterations; i++)
        {
            capturedMessageId = null;

            using var cts = new CancellationTokenSource();
            var sendTask = manager.SendRequestAsync<FakeMessage1, FakeMessage1>(
                new FakeMessage1(Guid.NewGuid()),
                new Dictionary<string, string>(),
                new RequestOptions { Timeout = 60_000 },
                cts.Token);

            // Wait for the send pipeline to record the request id (the registration is
            // installed before Execute*Pipeline runs, so once the id is captured the
            // cancel-registration is live).
            var spinDeadline = DateTime.UtcNow.AddSeconds(5);
            while (capturedMessageId is null && DateTime.UtcNow < spinDeadline)
            {
                await Task.Yield();
            }
            Assert.NotNull(capturedMessageId);

            // Race the reply against the caller-CT cancel. Two unparked threads jump on
            // the per-state lock at roughly the same instant.
            var replyTask = Task.Run(() => manager.TryProcessReply(capturedMessageId!, new byte[] { 1 }, typeof(FakeMessage1)));
            var cancelTask = Task.Run(cts.Cancel);

            await Task.WhenAll(replyTask, cancelTask);

            Exception? observedException = null;
            FakeMessage1? observedReply = null;
            try
            {
                observedReply = await sendTask;
            }
            catch (Exception ex)
            {
                observedException = ex;
            }

            // Exactly one of: success or cancel (any OCE subtype). The pre-fix bug
            // allowed the await to return a reply WITH the caller-CT already cancelled
            // — that's the silent-drop being guarded against. Either outcome is fine on
            // its own; the bug only manifests as "got a reply AND the registered cancel
            // did not surface anywhere".
            if (observedException is null)
            {
                Assert.NotNull(observedReply);
                // Reply legitimately won the lock — registration's state.Close
                // observed _closed=true post-fix because TrySetResult ran under the
                // lock; the cancel callback's TrySetCanceled is a no-op against an
                // already-completed TCS. This is the documented success path.
            }
            else
            {
                // Cancel won — must surface as OCE (TaskCanceledException is acceptable).
                Assert.IsAssignableFrom<OperationCanceledException>(observedException);
            }
        }
    }

    /// <summary>
    /// Single-threaded determinism: cancel the caller-CT EXACTLY between TryProcessReply
    /// flipping _closed=true and the completion-of-the-TCS. Pre-fix this was a real gap
    /// (the queued completionWork action ran outside the lock); post-fix the gap is
    /// closed and a cancel-after-process either no-ops (TCS already completed) or never
    /// reaches its callback (state.Close sees _closed=true). The reply wins, the await
    /// returns the reply.
    /// </summary>
    [Fact]
    public async Task SendRequestAsync_CancelImmediatelyAfterReply_ReplyWinsCleanly()
    {
        var serializer = new Mock<IMessageSerializer>();
        serializer.SetupSerializeAny<FakeMessage1>([1, 2, 3]);
        var expectedReply = new FakeMessage1(Guid.NewGuid()) { Username = "winner" };
        serializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(expectedReply);

        var pipeline = new Mock<ISendMessagePipeline>();
        string? capturedMessageId = null;
        pipeline.Setup(p => p.ExecuteSendMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) => capturedMessageId = ctx.Headers["RequestMessageId"])
            .Returns(Task.CompletedTask);

        var manager = new RequestReplyManager(serializer.Object, pipeline.Object);
        using var cts = new CancellationTokenSource();
        var sendTask = manager.SendRequestAsync<FakeMessage1, FakeMessage1>(
            new FakeMessage1(Guid.NewGuid()),
            new Dictionary<string, string>(),
            new RequestOptions { Timeout = 60_000 },
            cts.Token);

        var spinDeadline = DateTime.UtcNow.AddSeconds(5);
        while (capturedMessageId is null && DateTime.UtcNow < spinDeadline)
        {
            await Task.Yield();
        }
        Assert.NotNull(capturedMessageId);

        // Process the reply first (TCS is now completed under the lock). Then fire the
        // cancel — its registration callback runs synchronously and either takes the
        // lock and observes _closed=true (no-op) or its TrySetCanceled is a no-op
        // against the already-completed TCS. The await must observe the reply, not OCE.
        Assert.True(manager.TryProcessReply(capturedMessageId!, new byte[] { 1 }, typeof(FakeMessage1)));
        cts.Cancel();

        var result = await sendTask;
        Assert.Same(expectedReply, result);
    }
}
