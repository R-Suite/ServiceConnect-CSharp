using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Pins down the post-refactor invariants for the RequestState machine. Pre-refactor,
/// RequestState carried a separate _inFlightReplies counter that EndReply decremented
/// outside the close-action ordering — under pathological scheduling the counter could
/// underflow and a late close-action could fire after Close had already returned.
///
/// Post-refactor the counter is gone and Close + reply handling share a single lock,
/// so these scenarios are structurally impossible. The tests guard against regression.
/// </summary>
public sealed class RequestReplyManagerInFlightCounterTests
{
    [Fact]
    public async Task SendRequestMultiAsync_DuplicateRepliesAfterCompletion_AreIgnoredWithoutSideEffects()
    {
        // Drive the request to completion with the expected number of replies, then push
        // additional replies through TryProcessReply. The state has been removed from
        // _pendingRequests after completion, so TryProcessReply should return false and
        // the user-supplied onReply (we count via the responses-collection size) must
        // not see the duplicates.
        var serializer = new Mock<IMessageSerializer>();
        serializer.Setup(s => s.Serialize(It.IsAny<FakeMessage1>())).Returns([1, 2, 3]);

        // Each Deserialize call hands back a fresh reply object so the manager's Add
        // into the responses list always sees a real value.
        serializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(() => new FakeMessage1(Guid.NewGuid()) { Username = "ok" });

        var pipeline = new Mock<ISendMessagePipeline>();
        RequestReplyManager? manager = null;
        string? capturedMessageId = null;

        pipeline.Setup(p => p.ExecuteSendMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _ct) =>
            {
                capturedMessageId = ctx.Headers["RequestMessageId"];
                _ = Task.Run(() =>
                {
                    // Two replies satisfy ExpectedReplyCount and complete the request.
                    manager!.ProcessReply(capturedMessageId!, new byte[] { 1 }, typeof(FakeMessage1));
                    manager!.ProcessReply(capturedMessageId!, new byte[] { 1 }, typeof(FakeMessage1));
                });
            })
            .Returns(Task.CompletedTask);

        manager = new RequestReplyManager(serializer.Object, pipeline.Object);

        var options = new RequestOptions { Timeout = 5_000, ExpectedReplyCount = 2 };
        var responses = await manager.SendRequestMultiAsync<FakeMessage1, FakeMessage1>(
            new FakeMessage1(Guid.NewGuid()),
            new Dictionary<string, string>(),
            options);

        Assert.Equal(2, responses.Count);
        Assert.NotNull(capturedMessageId);

        // Now feed two more replies post-completion. They must be rejected — the state
        // was removed from _pendingRequests when the second reply completed the request.
        // No exception, no underflow, no spurious onReply invocations.
        var lateAccepted1 = manager.TryProcessReply(capturedMessageId!, new byte[] { 1 }, typeof(FakeMessage1));
        var lateAccepted2 = manager.TryProcessReply(capturedMessageId!, new byte[] { 1 }, typeof(FakeMessage1));

        Assert.False(lateAccepted1);
        Assert.False(lateAccepted2);
        // Deserialize was invoked exactly twice (for the two real replies). The two late
        // replies returned false from TryProcessReply before reaching Deserialize.
        serializer.Verify(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)), Times.Exactly(2));
    }

    [Fact]
    public async Task ProcessReply_AfterCloseFromTimeout_DoesNotInvokeOnReply()
    {
        // The timeout's Close() callback wins the race against a late reply. Once Close
        // has flipped _closed = true, TryHandleReply must return false without calling
        // OnReply — even if that reply was already deserialized and queued by the
        // transport. Pre-refactor this was also true under the lock, but the separate
        // _inFlightReplies counter created an underflow window after EndReply ran.
        var serializer = new Mock<IMessageSerializer>();
        serializer.Setup(s => s.Serialize(It.IsAny<FakeMessage1>())).Returns([1, 2, 3]);
        serializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(() => new FakeMessage1(Guid.NewGuid()) { Username = "late" });

        var pipeline = new Mock<ISendMessagePipeline>();
        RequestReplyManager? manager = null;
        string? capturedMessageId = null;
        var onReplyInvocations = 0;

        pipeline.Setup(p => p.ExecutePublishMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) => capturedMessageId = ctx.Headers["RequestMessageId"])
            .Returns(Task.CompletedTask);

        manager = new RequestReplyManager(serializer.Object, pipeline.Object);

        // Timeout 1 ms — the awaited PublishRequestAsync will time out before we feed
        // the late reply. ExpectedReplyCount is unset so a 0-reply timeout completes
        // successfully with no exception.
        var options = new RequestOptions { Timeout = 1 };
        var publishTask = manager.PublishRequestAsync<FakeMessage1, FakeMessage1>(
            new FakeMessage1(Guid.NewGuid()),
            new Dictionary<string, string>(),
            options,
            _ => Interlocked.Increment(ref onReplyInvocations));

        await publishTask;

        Assert.NotNull(capturedMessageId);

        // After the publish task completes, the request id may already have been removed
        // from _pendingRequests. Either way, the late reply must NOT invoke onReply.
        manager.TryProcessReply(capturedMessageId!, new byte[] { 1 }, typeof(FakeMessage1));

        Assert.Equal(0, onReplyInvocations);
    }
}
