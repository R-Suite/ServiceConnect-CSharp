using System;
using System.Collections.Generic;
using System.Linq;
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
/// Pre-refactor, the OnReply user callback ran outside the per-request state lock —
/// two concurrent replies could enter the callback at the same time, breaking the
/// documented "one callback invocation per accepted reply" contract for callers that
/// did not internally lock. Post-refactor TryHandleReply holds the state lock for the
/// whole reply lifecycle (deserialize, OnReply, completion), so concurrent replies
/// serialize through the callback.
///
/// Uses <c>PublishRequestAsync</c> because its <c>onReply</c> delegate is supplied
/// directly by the caller, making re-entrancy observable at the user-callback boundary.
/// </summary>
public sealed class RequestReplyManagerCallbackReentrancyTests
{
    [Fact]
    public async Task PublishRequestAsync_ConcurrentReplies_OnReplyNotInvokedConcurrently()
    {
        const int expectedReplyCount = 5;

        var serializer = new Mock<IMessageSerializer>();
        serializer.Setup(s => s.Serialize(It.IsAny<FakeMessage1>())).Returns([1, 2, 3]);
        serializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(() => new FakeMessage1(Guid.NewGuid()) { Username = "x" });

        var pipeline = new Mock<ISendMessagePipeline>();
        RequestReplyManager? manager = null;
        string? capturedMessageId = null;

        pipeline.Setup(p => p.ExecuteSendMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) => capturedMessageId = ctx.Headers["RequestMessageId"])
            .Returns(Task.CompletedTask);

        manager = new RequestReplyManager(serializer.Object, pipeline.Object);

        // Build a request whose internal onReply (set up by SendRequestMultiAsync to
        // append to a responses list) we wrap by spying on the public-visible side
        // effect: the reply count grows monotonically and SendRequestMultiAsync's
        // internal lock prevents collection corruption. To observe re-entrancy we need
        // to inspect the lock around the user-visible callback; SendRequestMultiAsync
        // doesn't expose one, so we use PublishRequestAsync which forwards the user's
        // onReply directly.

        var concurrency = 0;
        var maxConcurrency = 0;
        var totalReplies = 0;
        var maxConcurrencyLock = new object();

        var options = new RequestOptions { Timeout = 30_000, ExpectedReplyCount = expectedReplyCount };

        pipeline.Setup(p => p.ExecutePublishMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) => capturedMessageId = ctx.Headers["RequestMessageId"])
            .Returns(Task.CompletedTask);

        var publishTask = manager.PublishRequestAsync<FakeMessage1, FakeMessage1>(
            new FakeMessage1(Guid.NewGuid()),
            new Dictionary<string, string>(),
            options,
            _ =>
            {
                var current = Interlocked.Increment(ref concurrency);
                lock (maxConcurrencyLock)
                {
                    if (current > maxConcurrency)
                    {
                        maxConcurrency = current;
                    }
                }
                // Sleep briefly so racing threads have a chance to overlap if the
                // callback isn't serialized.
                Thread.Sleep(5);
                Interlocked.Decrement(ref concurrency);
                Interlocked.Increment(ref totalReplies);
            });

        // Wait for the publish pipeline to record the request id.
        var spinDeadline = DateTime.UtcNow.AddSeconds(5);
        while (capturedMessageId is null && DateTime.UtcNow < spinDeadline)
        {
            await Task.Yield();
        }
        Assert.NotNull(capturedMessageId);

        // Fire ExpectedReplyCount replies in parallel from independent worker threads.
        var workers = Enumerable.Range(0, expectedReplyCount)
            .Select(_ => Task.Run(() => manager.TryProcessReply(capturedMessageId!, new byte[] { 1 }, typeof(FakeMessage1))))
            .ToArray();

        await Task.WhenAll(workers);
        await publishTask;

        Assert.Equal(expectedReplyCount, totalReplies);
        // The single state lock around OnReply means only one thread can be inside the
        // callback at any instant. If the lock were ever dropped while OnReply runs,
        // maxConcurrency would observe at least 2.
        Assert.Equal(1, maxConcurrency);
    }
}
