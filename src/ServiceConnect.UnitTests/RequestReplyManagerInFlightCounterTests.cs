using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;
using ServiceConnect.UnitTests.Fakes;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Regression-guards: duplicate replies are rejected at the manager boundary, and
/// replies arriving after timeout-driven Close are rejected. The legacy
/// <c>_inFlightReplies</c> counter (and its underflow risk) was structurally eliminated
/// by removing the counter; these tests guard the user-visible invariant (no extra
/// OnReply, no exception) rather than the internal counter.
///
/// Both facts exercise the <c>_pendingRequests.TryGetValue</c> early-return in
/// <c>TryProcessReply</c>: once a request completes or times out the entry is removed,
/// so late replies exit before reaching any per-request state.
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
        serializer.SetupSerializeAny<FakeMessage1>([1, 2, 3]);

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

        manager = new RequestReplyManager(serializer.Object, pipeline.Object, new BusConfiguration());

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
        // The timeout's cancellation removes the request from _pendingRequests before
        // returning. A late reply arriving after that removal hits the TryGetValue
        // early-return in TryProcessReply and returns false without ever reaching
        // per-request state. Post-refactor: _pendingRequests is removed during request
        // completion, so late replies exit at the dictionary lookup. Test guards the
        // user-visible invariant: no OnReply for replies arriving after the request
        // completes.
        var serializer = new Mock<IMessageSerializer>();
        serializer.SetupSerializeAny<FakeMessage1>([1, 2, 3]);
        serializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(() => new FakeMessage1(Guid.NewGuid()) { Username = "late" });

        var pipeline = new Mock<ISendMessagePipeline>();
        RequestReplyManager? manager = null;
        string? capturedMessageId = null;
        var onReplyInvocations = 0;

        pipeline.Setup(p => p.ExecutePublishMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) => capturedMessageId = ctx.Headers["RequestMessageId"])
            .Returns(Task.CompletedTask);

        manager = new RequestReplyManager(serializer.Object, pipeline.Object, new BusConfiguration());

        // The awaited PublishRequestAsync must time out AFTER the send pipeline has
        // completed; a too-tight timeout (e.g. 1 ms) loses the race under suite load
        // and PublishRequestAsync throws RequestTimeoutException via the send-not-
        // completed branch (line 380-384 of RequestReplyManager). 50 ms is short
        // enough to keep the test fast but long enough that the synchronous mock
        // pipeline always wins.
        // ExpectedReplyCount is unset so a 0-reply timeout completes successfully.
        var options = new RequestOptions { Timeout = 50 };
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
