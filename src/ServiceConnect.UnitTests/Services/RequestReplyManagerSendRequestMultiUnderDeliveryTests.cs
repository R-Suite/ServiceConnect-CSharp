using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;
using ServiceConnect.UnitTests.Fakes;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

/// <summary>
/// Regression-guards for the under-delivery contract on
/// <see cref="RequestReplyManager.SendRequestMultiAsync"/>: a positive
/// <see cref="RequestOptions.ExpectedReplyCount"/> must throw
/// <see cref="RequestTimeoutException"/> when fewer replies than requested arrive
/// before the timeout, with the partials surfaced on
/// <see cref="RequestTimeoutException.PartialReplies"/>. The alternative — silently
/// returning a partial list with no signal of under-delivery — would be asymmetric
/// with <c>PublishRequestAsync</c>'s callback-driven shape.
/// </summary>
public sealed class RequestReplyManagerSendRequestMultiUnderDeliveryTests
{
    private readonly Mock<IMessageSerializer> _mockSerializer;
    private readonly Mock<ISendMessagePipeline> _mockSendPipeline;

    public RequestReplyManagerSendRequestMultiUnderDeliveryTests()
    {
        _mockSerializer = new Mock<IMessageSerializer>();
        _mockSendPipeline = new Mock<ISendMessagePipeline>();
    }

    [Fact]
    public async Task SendRequestMultiAsync_FewerRepliesThanExpected_ThrowsWithPartialReplies()
    {
        // Caller asks for 3 replies, exactly 1 arrives, timeout fires. The exception
        // must surface the single partial via PartialReplies.
        var firstReply = new FakeMessage1(Guid.NewGuid()) { Username = "first" };
        var request = new FakeMessage1(Guid.NewGuid());
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerializeAny<FakeMessage1>(messageBytes);
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(firstReply);

        RequestReplyManager? manager = null;
        string? capturedMessageId = null;

        var options = new RequestOptions { Timeout = 100, ExpectedReplyCount = 3 };
        var headers = new Dictionary<string, string>();

        _mockSendPipeline.Setup(pipeline => pipeline.ExecuteSendMessagePipelineAsync(
                It.Is<SendContext>(ctx => ctx.EndPoint == null),
                It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) =>
            {
                capturedMessageId = ctx.Headers["RequestMessageId"];
                Task.Run(() =>
                {
                    // Deliver only one of the three expected replies; let the other two
                    // miss the timeout window.
                    manager!.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));
                });
            })
            .Returns(Task.CompletedTask);

        manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object, new BusConfiguration());

        var ex = await Assert.ThrowsAsync<RequestTimeoutException>(() =>
            manager.SendRequestMultiAsync<FakeMessage1, FakeMessage1>(request, headers, options));

        Assert.NotNull(ex.PartialReplies);
        Assert.Single(ex.PartialReplies);
        Assert.IsType<FakeMessage1>(ex.PartialReplies[0]);
        Assert.Equal("first", ((FakeMessage1)ex.PartialReplies[0]).Username);
        Assert.Equal(TimeSpan.FromMilliseconds(options.Timeout), ex.Elapsed);
    }

    [Fact]
    public async Task SendRequestMultiAsync_AllExpectedRepliesArrive_ReturnsListNormally()
    {
        // Sanity: full delivery still returns the populated list (no exception). Guards
        // against the under-delivery branch firing on the success path.
        var reply = new FakeMessage1(Guid.NewGuid()) { Username = "ok" };
        var request = new FakeMessage1(Guid.NewGuid());
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerializeAny<FakeMessage1>(messageBytes);
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(reply);

        RequestReplyManager? manager = null;
        string? capturedMessageId = null;

        var options = new RequestOptions { Timeout = 5_000, ExpectedReplyCount = 2 };
        var headers = new Dictionary<string, string>();

        _mockSendPipeline.Setup(pipeline => pipeline.ExecuteSendMessagePipelineAsync(
                It.Is<SendContext>(ctx => ctx.EndPoint == null),
                It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) =>
            {
                capturedMessageId = ctx.Headers["RequestMessageId"];
                Task.Run(() =>
                {
                    manager!.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));
                    manager!.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));
                });
            })
            .Returns(Task.CompletedTask);

        manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object, new BusConfiguration());

        var results = await manager.SendRequestMultiAsync<FakeMessage1, FakeMessage1>(
            request, headers, options);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SendRequestMultiAsync_CallerCancelBeforeTimeout_ThrowsOCE_NotRequestTimeout()
    {
        // External cancellation must surface as OperationCanceledException, not as the
        // new RequestTimeoutException — even when ExpectedReplyCount is positive and
        // under-delivered. Mirrors the existing SendRequestAsync_ExternalCancel guard.
        var request = new FakeMessage1(Guid.NewGuid());
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerializeAny<FakeMessage1>(messageBytes);

        using var externalCts = new CancellationTokenSource();
        var options = new RequestOptions { Timeout = 300_000, ExpectedReplyCount = 5 };
        var headers = new Dictionary<string, string>();

        _mockSendPipeline.Setup(pipeline => pipeline.ExecuteSendMessagePipelineAsync(
                It.Is<SendContext>(ctx => ctx.EndPoint == null),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object, new BusConfiguration());

        var task = manager.SendRequestMultiAsync<FakeMessage1, FakeMessage1>(
            new FakeMessage1(Guid.NewGuid()), headers, options, externalCts.Token);

        externalCts.CancelAfter(50);

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.IsNotType<RequestTimeoutException>(ex);
    }

    [Fact]
    public async Task SendRequestMultiAsync_NoExpectedReplyCount_ReturnsEmptyOnTimeoutWithoutException()
    {
        // ExpectedReplyCount unset → "fire and collect whatever shows up" — under-delivery
        // does not apply. The call must return cleanly with whatever arrived (nothing, in
        // this case) rather than throwing.
        var request = new FakeMessage1(Guid.NewGuid());
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerializeAny<FakeMessage1>(messageBytes);

        var options = new RequestOptions { Timeout = 50 }; // ExpectedReplyCount unset (null)
        var headers = new Dictionary<string, string>();

        _mockSendPipeline.Setup(pipeline => pipeline.ExecuteSendMessagePipelineAsync(
                It.Is<SendContext>(ctx => ctx.EndPoint == null),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object, new BusConfiguration());

        var results = await manager.SendRequestMultiAsync<FakeMessage1, FakeMessage1>(
            request, headers, options);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SendRequestMultiAsync_ZeroExpectedReplyCount_ReturnsCleanlyWithoutException()
    {
        // Explicit ExpectedReplyCount = 0 is the documented "no expectation" sentinel.
        // Must not trigger the new under-delivery branch.
        var request = new FakeMessage1(Guid.NewGuid());
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerializeAny<FakeMessage1>(messageBytes);

        var options = new RequestOptions { Timeout = 50, ExpectedReplyCount = 0 };
        var headers = new Dictionary<string, string>();

        _mockSendPipeline.Setup(pipeline => pipeline.ExecuteSendMessagePipelineAsync(
                It.Is<SendContext>(ctx => ctx.EndPoint == null),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object, new BusConfiguration());

        var results = await manager.SendRequestMultiAsync<FakeMessage1, FakeMessage1>(
            request, headers, options);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SendRequestMultiAsync_PositiveExpected_ZeroRepliesArrive_ThrowsWithEmptyPartials()
    {
        // Edge case: ExpectedReplyCount > 0 but no replies at all — exception must still
        // be raised, with PartialReplies empty (not null).
        var request = new FakeMessage1(Guid.NewGuid());
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerializeAny<FakeMessage1>(messageBytes);

        var options = new RequestOptions { Timeout = 50, ExpectedReplyCount = 2 };
        var headers = new Dictionary<string, string>();

        _mockSendPipeline.Setup(pipeline => pipeline.ExecuteSendMessagePipelineAsync(
                It.Is<SendContext>(ctx => ctx.EndPoint == null),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object, new BusConfiguration());

        var ex = await Assert.ThrowsAsync<RequestTimeoutException>(() =>
            manager.SendRequestMultiAsync<FakeMessage1, FakeMessage1>(request, headers, options));

        Assert.NotNull(ex.PartialReplies);
        Assert.Empty(ex.PartialReplies);
    }
}
