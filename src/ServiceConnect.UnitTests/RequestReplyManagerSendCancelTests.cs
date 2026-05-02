using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Covers the H17 fix: when the linked CTS (timeout) cancels the outbound send pipeline
/// before the send completes, and the caller's own token did NOT fire, the call must
/// surface the typed <see cref="RequestSendCancelledException"/> immediately rather than
/// stalling on the pending-reply TCS until the timeout deadline.
/// </summary>
public sealed class RequestReplyManagerSendCancelTests
{
    // Generous fail-fast bound: the typed exception path observes cancellation directly,
    // so anything beyond a couple of hundred milliseconds means we accidentally took the
    // timeout path. Bound is loose enough to survive scheduler jitter on busy CI.
    private const int FailFastBudgetMs = 500;

    [Fact]
    public async Task SendRequestAsync_SendPipelineCancelled_NotByCallerToken_ThrowsRequestSendCancelled()
    {
        var serializer = new Mock<IMessageSerializer>();
        serializer.Setup(s => s.Serialize(It.IsAny<FakeMessage1>())).Returns([1, 2, 3]);

        var pipeline = new Mock<ISendMessagePipeline>();
        pipeline
            .Setup(p => p.ExecuteSendMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Returns(async (SendContext _, CancellationToken ct) =>
            {
                // Block until the linked CTS (timeout) fires; this models a transport
                // that never acknowledges the send before the deadline.
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (ct.Register(() => tcs.TrySetCanceled(ct)))
                {
                    await tcs.Task.ConfigureAwait(false);
                }
            });

        var manager = new RequestReplyManager(serializer.Object, pipeline.Object);
        var options = new RequestOptions { Timeout = 200, EndPoint = "test-endpoint" };
        var headers = new Dictionary<string, string>();

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<RequestSendCancelledException>(() =>
            manager.SendRequestAsync<FakeMessage1, FakeMessage1>(new FakeMessage1(Guid.NewGuid()), headers, options));
        sw.Stop();

        // Pre-fix the caller would block on the reply TCS until the timeout fires
        // (~200ms) and observe RequestTimeoutException. Post-fix the typed exception
        // surfaces as soon as the send pipeline reports cancellation.
        Assert.True(sw.ElapsedMilliseconds < FailFastBudgetMs,
            $"Expected fail-fast (< {FailFastBudgetMs}ms); got {sw.ElapsedMilliseconds}ms.");
        Assert.NotEqual(Guid.Empty, ex.MessageId);
    }

    [Fact]
    public async Task SendRequestAsync_CallerTokenCancelled_StillThrowsOperationCanceled_NotRequestSendCancelled()
    {
        var serializer = new Mock<IMessageSerializer>();
        serializer.Setup(s => s.Serialize(It.IsAny<FakeMessage1>())).Returns([1, 2, 3]);

        var pipeline = new Mock<ISendMessagePipeline>();
        pipeline
            .Setup(p => p.ExecuteSendMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Returns(async (SendContext _, CancellationToken ct) =>
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (ct.Register(() => tcs.TrySetCanceled(ct)))
                {
                    await tcs.Task.ConfigureAwait(false);
                }
            });

        var manager = new RequestReplyManager(serializer.Object, pipeline.Object);
        // Long timeout so the caller token wins the race.
        var options = new RequestOptions { Timeout = 60_000, EndPoint = "test-endpoint" };
        var headers = new Dictionary<string, string>();

        using var callerCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // ThrowsAnyAsync because the runtime surfaces TaskCanceledException (an OCE
        // subclass). The discriminator we care about is "not the typed send-cancelled
        // exception" — caller-token cancellation must propagate as a vanilla OCE so
        // existing handlers keep working.
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.SendRequestAsync<FakeMessage1, FakeMessage1>(new FakeMessage1(Guid.NewGuid()), headers, options, callerCts.Token));

        Assert.IsNotType<RequestSendCancelledException>(ex);
    }

    [Fact]
    public async Task SendRequestAsync_TimeoutFires_AndSendCompletedFirst_ThrowsRequestTimeout()
    {
        var serializer = new Mock<IMessageSerializer>();
        serializer.Setup(s => s.Serialize(It.IsAny<FakeMessage1>())).Returns([1, 2, 3]);

        var pipeline = new Mock<ISendMessagePipeline>();
        pipeline
            .Setup(p => p.ExecuteSendMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var manager = new RequestReplyManager(serializer.Object, pipeline.Object);
        var options = new RequestOptions { Timeout = 100, EndPoint = "test-endpoint" };
        var headers = new Dictionary<string, string>();

        // Send completes immediately; no reply ever arrives, so the timeout path wins
        // and the canonical RequestTimeoutException must surface.
        await Assert.ThrowsAsync<RequestTimeoutException>(() =>
            manager.SendRequestAsync<FakeMessage1, FakeMessage1>(new FakeMessage1(Guid.NewGuid()), headers, options));
    }

    [Fact]
    public async Task SendRequestMultiAsync_SendPipelineCancelled_NotByCallerToken_ThrowsRequestSendCancelled()
    {
        var serializer = new Mock<IMessageSerializer>();
        serializer.Setup(s => s.Serialize(It.IsAny<FakeMessage1>())).Returns([1, 2, 3]);

        var pipeline = new Mock<ISendMessagePipeline>();
        pipeline
            .Setup(p => p.ExecuteSendMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Returns(async (SendContext _, CancellationToken ct) =>
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (ct.Register(() => tcs.TrySetCanceled(ct)))
                {
                    await tcs.Task.ConfigureAwait(false);
                }
            });

        var manager = new RequestReplyManager(serializer.Object, pipeline.Object);
        var options = new RequestOptions
        {
            Timeout = 200,
            EndPoints = ["endpoint-a", "endpoint-b"],
            ExpectedReplyCount = 2,
        };
        var headers = new Dictionary<string, string>();

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<RequestSendCancelledException>(() =>
            manager.SendRequestMultiAsync<FakeMessage1, FakeMessage1>(new FakeMessage1(Guid.NewGuid()), headers, options));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < FailFastBudgetMs,
            $"Expected fail-fast (< {FailFastBudgetMs}ms); got {sw.ElapsedMilliseconds}ms.");
        Assert.NotEqual(Guid.Empty, ex.MessageId);
    }

    [Fact]
    public async Task PublishRequestAsync_SendPipelineCancelled_NotByCallerToken_ThrowsRequestSendCancelled()
    {
        var serializer = new Mock<IMessageSerializer>();
        serializer.Setup(s => s.Serialize(It.IsAny<FakeMessage1>())).Returns([1, 2, 3]);

        var pipeline = new Mock<ISendMessagePipeline>();
        pipeline
            .Setup(p => p.ExecutePublishMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Returns(async (SendContext _, CancellationToken ct) =>
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (ct.Register(() => tcs.TrySetCanceled(ct)))
                {
                    await tcs.Task.ConfigureAwait(false);
                }
            });

        var manager = new RequestReplyManager(serializer.Object, pipeline.Object);
        var options = new RequestOptions { Timeout = 200, ExpectedReplyCount = 1 };
        var headers = new Dictionary<string, string>();

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<RequestSendCancelledException>(() =>
            manager.PublishRequestAsync<FakeMessage1, FakeMessage1>(new FakeMessage1(Guid.NewGuid()), headers, options, _ => { }));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < FailFastBudgetMs,
            $"Expected fail-fast (< {FailFastBudgetMs}ms); got {sw.ElapsedMilliseconds}ms.");
        Assert.NotEqual(Guid.Empty, ex.MessageId);
    }
}
