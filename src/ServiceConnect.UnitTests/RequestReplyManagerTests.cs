using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;
using ServiceConnect.UnitTests.Fakes;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests;

public class RequestReplyManagerTests
{
    private readonly Mock<IMessageSerializer> _mockSerializer;
    private readonly Mock<ISendMessagePipeline> _mockSendPipeline;

    public RequestReplyManagerTests()
    {
        _mockSerializer = new Mock<IMessageSerializer>();
        _mockSendPipeline = new Mock<ISendMessagePipeline>();
    }

    /// <summary>
    /// Polls <paramref name="condition"/> every 10 ms for up to <paramref name="maxWait"/>,
    /// returning when the condition becomes true or the budget is exhausted.
    /// </summary>
    private static async Task WaitForCondition(Func<bool> condition, TimeSpan maxWait)
    {
        var deadline = DateTime.UtcNow + maxWait;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public void IRequestReplyManager_ProcessReply_ReturnsVoid()
    {
        var method = typeof(IRequestReplyManager).GetMethod(nameof(IRequestReplyManager.ProcessReply));

        Assert.NotNull(method);
        Assert.Equal(typeof(void), method!.ReturnType);
    }

    [Fact]
    public void Constructor_ThrowsWhenSerializerIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new RequestReplyManager(null!, _mockSendPipeline.Object));
        Assert.Throws<ArgumentNullException>(() => new RequestReplyManager(_mockSerializer.Object, null!));
    }

    [Fact]
    public async Task SendRequestAsync_SendsMessageWithRequestMessageIdHeader()
    {
        // Arrange
        var replyId = Guid.NewGuid();
        var reply = new FakeMessage1(replyId) { Username = "TestUser" };
        var request = new FakeMessage1(Guid.NewGuid()) { Username = "Sender" };

        RequestReplyManager? manager = null;
        string? capturedMessageId = null;

        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerializeAny<FakeMessage1>(messageBytes);
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(reply);

        var options = new RequestOptions { Timeout = 5000 };
        var headers = new Dictionary<string, string>();

        _mockSendPipeline.Setup(pipeline => pipeline.ExecuteSendMessagePipelineAsync(
                It.Is<SendContext>(ctx =>
                    ctx.MessageType == typeof(FakeMessage1) &&
                    ctx.MessageBytes.ToArray().SequenceEqual(messageBytes) &&
                    ctx.EndPoint == null &&
                    ctx.Operation == SendOperation.Request),
                It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) =>
            {
                capturedMessageId = ctx.Headers["RequestMessageId"];
                Task.Run(() =>
                {
                    manager!.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));
                });
            })
            .Returns(Task.CompletedTask);

        manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);

        // Act
        var result = await manager.SendRequestAsync<FakeMessage1, FakeMessage1>(
            request, headers, options);

        // Assert
        Assert.NotNull(capturedMessageId);
        Assert.Equal(reply.Username, result.Username);
    }

    [Fact]
    public async Task SendRequestAsync_ThrowsRequestTimeoutException_WhenNoReply()
    {
        // Arrange
        var options = new RequestOptions { Timeout = 100 };
        var headers = new Dictionary<string, string>();
        var request = new FakeMessage1(Guid.NewGuid());
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerializeAny<FakeMessage1>(messageBytes);

        _mockSendPipeline.Setup(pipeline => pipeline.ExecuteSendMessagePipelineAsync(
                It.Is<SendContext>(ctx =>
                    ctx.MessageType == typeof(FakeMessage1) &&
                    ctx.MessageBytes.ToArray().SequenceEqual(messageBytes) &&
                    ctx.EndPoint == null &&
                    ctx.Operation == SendOperation.Request),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);

        // Act & Assert
        await Assert.ThrowsAsync<RequestTimeoutException>(() =>
            manager.SendRequestAsync<FakeMessage1, FakeMessage1>(
                request, headers, options));
    }

    [Fact]
    public async Task SendRequestAsync_SendsToEndPoint_WhenSpecified()
    {
        // Arrange
        var replyId = Guid.NewGuid();
        var reply = new FakeMessage1(replyId) { Username = "EndpointUser" };
        var request = new FakeMessage1(Guid.NewGuid());

        RequestReplyManager? manager = null;
        string? capturedEndpoint = null;
        string? capturedMessageId = null;

        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerializeAny<FakeMessage1>(messageBytes);
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(reply);

        var options = new RequestOptions { Timeout = 5000, EndPoint = "my-queue" };
        var headers = new Dictionary<string, string>();

        _mockSendPipeline.Setup(pipeline => pipeline.ExecuteSendMessagePipelineAsync(
                It.Is<SendContext>(ctx =>
                    ctx.MessageType == typeof(FakeMessage1) &&
                    ctx.EndPoint == "my-queue" &&
                    ctx.Operation == SendOperation.Request),
                It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) =>
            {
                capturedEndpoint = ctx.EndPoint;
                capturedMessageId = ctx.Headers["RequestMessageId"];
                Task.Run(() =>
                {
                    manager!.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));
                });
            })
            .Returns(Task.CompletedTask);

        manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);

        // Act
        await manager.SendRequestAsync<FakeMessage1, FakeMessage1>(
            request, headers, options);

        // Assert
        Assert.Equal("my-queue", capturedEndpoint);
    }

    [Fact]
    public async Task SendRequestAsync_RemovesPendingRequest_WhenSendPipelineThrows()
    {
        var pipelineException = new InvalidOperationException("send failed");
        var headers = new Dictionary<string, string>();
        var request = new FakeMessage1(Guid.NewGuid());
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerializeAny<FakeMessage1>(messageBytes);
        var options = new RequestOptions { Timeout = 5000 };
        string? capturedMessageId = null;

        _mockSendPipeline.Setup(pipeline => pipeline.ExecuteSendMessagePipelineAsync(
                It.Is<SendContext>(ctx => ctx.EndPoint == null),
                It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) =>
            {
                capturedMessageId = ctx.Headers["RequestMessageId"];
            })
            .ThrowsAsync(pipelineException);

        var manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.SendRequestAsync<FakeMessage1, FakeMessage1>(request, headers, options));

        Assert.Same(pipelineException, ex);
        Assert.NotNull(capturedMessageId);

        manager.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));

        _mockSerializer.Verify(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)), Times.Never);
    }

    [Fact]
    public void ProcessReply_ReturnsFalse_WhenMessageIdIsUnknown()
    {
        var manager = (IReplyStatusRequestReplyManager)new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);

        var handled = manager.TryProcessReply(Guid.NewGuid().ToString(), new byte[] { 1, 2, 3 }, typeof(FakeMessage1));

        Assert.False(handled);
        _mockSerializer.Verify(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<Type>()), Times.Never);
    }

    [Fact]
    public async Task SendRequestMultiAsync_CollectsMultipleReplies()
    {
        // Arrange
        var reply1 = new FakeMessage1(Guid.NewGuid()) { Username = "User1" };
        var reply2 = new FakeMessage1(Guid.NewGuid()) { Username = "User2" };
        var request = new FakeMessage1(Guid.NewGuid());
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerializeAny<FakeMessage1>(messageBytes);

        var callCount = 0;
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(() => ++callCount == 1 ? reply1 : reply2);

        RequestReplyManager? manager = null;
        string? capturedMessageId = null;

        var options = new RequestOptions { Timeout = 5000, ExpectedReplyCount = 2 };
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

        manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);

        // Act
        var results = await manager.SendRequestMultiAsync<FakeMessage1, FakeMessage1>(
            request, headers, options);

        // Assert
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SendRequestMultiAsync_IgnoresRepliesBeyondExpectedReplyCount()
    {
        var firstReply = new FakeMessage1(Guid.NewGuid()) { Username = "User1" };
        var secondReply = new FakeMessage1(Guid.NewGuid()) { Username = "User2" };
        var thirdReply = new FakeMessage1(Guid.NewGuid()) { Username = "User3" };
        var request = new FakeMessage1(Guid.NewGuid());
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerializeAny<FakeMessage1>(messageBytes);

        var deserializedReplies = new Queue<FakeMessage1>([firstReply, secondReply, thirdReply]);
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(deserializedReplies.Dequeue);

        RequestReplyManager? manager = null;
        string? capturedMessageId = null;

        var options = new RequestOptions { Timeout = 5000, ExpectedReplyCount = 2 };
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
                    manager.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));
                    manager.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));
                });
            })
            .Returns(Task.CompletedTask);

        manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);

        var results = await manager.SendRequestMultiAsync<FakeMessage1, FakeMessage1>(request, headers, options);

        Assert.Equal(2, results.Count);
        Assert.Collection(results,
            reply => Assert.Equal("User1", reply.Username),
            reply => Assert.Equal("User2", reply.Username));
        _mockSerializer.Verify(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)), Times.Exactly(2));
    }

    [Fact]
    public async Task SendRequestMultiAsync_SendsToEndPoint_WhenSingleEndPointSpecified()
    {
        // Arrange
        var reply = new FakeMessage1(Guid.NewGuid()) { Username = "EndpointUser" };
        var request = new FakeMessage1(Guid.NewGuid());
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerializeAny<FakeMessage1>(messageBytes);

        RequestReplyManager? manager = null;
        string? capturedEndpoint = null;
        string? capturedMessageId = null;

        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(reply);

        var options = new RequestOptions { Timeout = 5000, EndPoint = "single-queue", ExpectedReplyCount = 1 };
        var headers = new Dictionary<string, string>();

        _mockSendPipeline.Setup(pipeline => pipeline.ExecuteSendMessagePipelineAsync(
                It.Is<SendContext>(ctx => ctx.EndPoint == "single-queue"),
                It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) =>
            {
                capturedEndpoint = ctx.EndPoint;
                capturedMessageId = ctx.Headers["RequestMessageId"];
                Task.Run(() =>
                {
                    manager!.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));
                });
            })
            .Returns(Task.CompletedTask);

        manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);

        // Act
        var results = await manager.SendRequestMultiAsync<FakeMessage1, FakeMessage1>(
            request, headers, options);

        // Assert
        Assert.Equal("single-queue", capturedEndpoint);
        Assert.Single(results);
        Assert.Equal(reply.Username, results[0].Username);
    }

    [Fact]
    public async Task SendRequestMultiAsync_UsesEndPoint_WhenSet()
    {
        var reply = new FakeMessage1(Guid.NewGuid()) { Username = "EndpointUser" };
        var request = new FakeMessage1(Guid.NewGuid());
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerializeAny<FakeMessage1>(messageBytes);

        RequestReplyManager? manager = null;
        string? capturedEndpoint = null;
        string? capturedMessageId = null;

        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(reply);

        var options = new RequestOptions
        {
            Timeout = 5000,
            EndPoint = "target-queue",
            ExpectedReplyCount = 1,
        };
        var headers = new Dictionary<string, string>();

        _mockSendPipeline.Setup(pipeline => pipeline.ExecuteSendMessagePipelineAsync(
                It.Is<SendContext>(ctx => ctx.EndPoint == "target-queue"),
                It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) =>
            {
                capturedEndpoint = ctx.EndPoint;
                capturedMessageId = ctx.Headers["RequestMessageId"];
                Task.Run(() =>
                {
                    manager!.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));
                });
            })
            .Returns(Task.CompletedTask);

        manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);

        var results = await manager.SendRequestMultiAsync<FakeMessage1, FakeMessage1>(
            request,
            headers,
            options);

        Assert.Equal("target-queue", capturedEndpoint);
        Assert.Single(results);
        Assert.Equal(reply.Username, results[0].Username);
    }

    [Fact]
    public async Task SendRequestMultiAsync_RemovesPendingRequest_WhenSendPipelineThrows()
    {
        var pipelineException = new InvalidOperationException("send failed");
        var headers = new Dictionary<string, string>();
        var request = new FakeMessage1(Guid.NewGuid());
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerializeAny<FakeMessage1>(messageBytes);
        var options = new RequestOptions { Timeout = 5000 };
        string? capturedMessageId = null;

        _mockSendPipeline.Setup(pipeline => pipeline.ExecuteSendMessagePipelineAsync(
                It.Is<SendContext>(ctx => ctx.EndPoint == null),
                It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) =>
            {
                capturedMessageId = ctx.Headers["RequestMessageId"];
            })
            .ThrowsAsync(pipelineException);

        var manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.SendRequestMultiAsync<FakeMessage1, FakeMessage1>(request, headers, options));

        Assert.Same(pipelineException, ex);
        Assert.NotNull(capturedMessageId);

        manager.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));

        _mockSerializer.Verify(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)), Times.Never);
    }

    [Fact]
    public async Task PublishRequestAsync_UsesPublishPipeline_AndInvokesCallbackBeforeCompletion()
    {
        // Arrange
        var reply = new FakeMessage1(Guid.NewGuid()) { Username = "PublishReply" };
        var request = new FakeMessage1(Guid.NewGuid());
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerializeAny<FakeMessage1>(messageBytes);
        RequestReplyManager? manager = null;
        string? capturedMessageId = null;
        var callbackInvoked = false;

        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(reply);

        var options = new RequestOptions { Timeout = 5000, ExpectedReplyCount = 1 };
        var headers = new Dictionary<string, string>();

        _mockSendPipeline.Setup(pipeline => pipeline.ExecutePublishMessagePipelineAsync(
                It.Is<SendContext>(ctx =>
                    ctx.MessageType == typeof(FakeMessage1) &&
                    ctx.EndPoint == null &&
                    ctx.Operation == SendOperation.Request),
                It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) =>
            {
                capturedMessageId = ctx.Headers["RequestMessageId"];
                Task.Run(() =>
                {
                    manager!.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));
                });
            })
            .Returns(Task.CompletedTask);

        manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);

        // Act
        Task? publishTask = null;
        publishTask = manager.PublishRequestAsync<FakeMessage1, FakeMessage1>(
            request,
            headers,
            options,
            response =>
            {
                callbackInvoked = true;
                Assert.Equal(reply.Username, response.Username);
                Assert.False(publishTask?.IsCompleted ?? false);
            });

        await publishTask;

        // Assert
        Assert.True(callbackInvoked);
        _mockSendPipeline.Verify(pipeline => pipeline.ExecutePublishMessagePipelineAsync(
                It.Is<SendContext>(ctx =>
                    ctx.MessageType == typeof(FakeMessage1) &&
                    ctx.EndPoint == null &&
                    ctx.Operation == SendOperation.Request),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _mockSendPipeline.Verify(pipeline => pipeline.ExecuteSendMessagePipelineAsync(
                It.IsAny<SendContext>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PublishRequestAsync_AdmittedReplyAcrossTimeout_WaitsForReplyBeforeCompleting()
    {
        var reply = new FakeMessage1(Guid.NewGuid()) { Username = "PublishReply" };
        var request = new FakeMessage1(Guid.NewGuid());
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerializeAny<FakeMessage1>(messageBytes);
        var deserializeStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDeserialize = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestReplyManager? manager = null;
        string? capturedMessageId = null;
        var callbackInvoked = false;
        Task? publishTask = null;

        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(() =>
            {
                deserializeStarted.TrySetResult(null);
                releaseDeserialize.Task.GetAwaiter().GetResult();
                return reply;
            });

        var options = new RequestOptions { Timeout = 50, ExpectedReplyCount = 1 };
        var headers = new Dictionary<string, string>();

        _mockSendPipeline.Setup(pipeline => pipeline.ExecutePublishMessagePipelineAsync(
                It.Is<SendContext>(ctx => ctx.EndPoint == null),
                It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) =>
            {
                capturedMessageId = ctx.Headers["RequestMessageId"];
                Task.Run(() => manager!.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1)));
            })
            .Returns(Task.CompletedTask);

        manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);

        publishTask = manager.PublishRequestAsync<FakeMessage1, FakeMessage1>(
            request,
            headers,
            options,
            response =>
            {
                callbackInvoked = true;
                Assert.Equal(reply.Username, response.Username);
                Assert.False(publishTask!.IsCompleted);
            });

        await deserializeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(options.Timeout + 100);

        Assert.False(publishTask.IsCompleted);

        releaseDeserialize.TrySetResult(null);

        await publishTask;

        Assert.True(callbackInvoked);
    }

    [Fact]
    public async Task PublishRequestAsync_AdmittedReplyFailureAfterTimeout_FaultsWithReplyFailure()
    {
        var deserializeStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDeserialize = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deserializeException = new InvalidOperationException("deserialize failed after timeout");
        var request = new FakeMessage1(Guid.NewGuid());
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerializeAny<FakeMessage1>(messageBytes);
        RequestReplyManager? manager = null;
        string? capturedMessageId = null;

        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(() =>
            {
                deserializeStarted.TrySetResult(null);
                releaseDeserialize.Task.GetAwaiter().GetResult();
                throw deserializeException;
            });

        var options = new RequestOptions { Timeout = 50, ExpectedReplyCount = 1 };
        var headers = new Dictionary<string, string>();

        _mockSendPipeline.Setup(pipeline => pipeline.ExecutePublishMessagePipelineAsync(
                It.Is<SendContext>(ctx => ctx.EndPoint == null),
                It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) =>
            {
                capturedMessageId = ctx.Headers["RequestMessageId"];
                Task.Run(() =>
                {
                    try
                    {
                        manager!.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));
                    }
                    catch
                    {
                    }
                });
            })
            .Returns(Task.CompletedTask);

        manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);

        var publishTask = manager.PublishRequestAsync<FakeMessage1, FakeMessage1>(
            request,
            headers,
            options,
            _ => Assert.Fail("Callback should not run when deserialize fails."));

        await deserializeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(options.Timeout + 100);

        Assert.False(publishTask.IsCompleted);

        releaseDeserialize.TrySetResult(null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => publishTask);
        Assert.Same(deserializeException, ex);
    }

    [Fact]
    public async Task PublishRequestAsync_ReplyArrivingAfterTimeoutClose_IsIgnored()
    {
        // Under the single-lock state machine the timeout's Close action and the reply
        // path both contend for RequestState._stateLock. A reply that lands AFTER Close
        // has acquired the lock and flipped the state to closed is rejected (no
        // deserialize, no callback). A reply that landed earlier and is mid-lifecycle
        // completes — a slow Deserialize is no longer interrupted by the timeout, since
        // attempting to interrupt a partially-mutated state was the source of the
        // original C10/H18 defects this state machine was rewritten to fix.
        var firstReply = new FakeMessage1(Guid.NewGuid()) { Username = "Reply1" };
        var request = new FakeMessage1(Guid.NewGuid());
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerializeAny<FakeMessage1>(messageBytes);
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(firstReply);

        RequestReplyManager? manager = null;
        string? capturedMessageId = null;
        var callbacks = new List<string>();

        var options = new RequestOptions { Timeout = 25 };
        var headers = new Dictionary<string, string>();

        _mockSendPipeline.Setup(pipeline => pipeline.ExecutePublishMessagePipelineAsync(
                It.Is<SendContext>(ctx => ctx.EndPoint == null),
                It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) =>
            {
                capturedMessageId = ctx.Headers["RequestMessageId"];
            })
            .Returns(Task.CompletedTask);

        manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);

        var publishTask = manager.PublishRequestAsync<FakeMessage1, FakeMessage1>(
            request,
            headers,
            options,
            reply => callbacks.Add(reply.Username));

        // Wait for the timeout to fire and for the publish task to fully complete.
        // Once publishTask returns, the state has been Close'd and removed from the
        // pending-requests map.
        await publishTask;
        Assert.NotNull(capturedMessageId);

        // A reply arriving after Close must be rejected: the manager removed the state
        // from _pendingRequests, so TryProcessReply returns false before any callback
        // can run, and Deserialize is never invoked.
        var lateAccepted = manager.TryProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));

        Assert.False(lateAccepted);
        Assert.Empty(callbacks);
        _mockSerializer.Verify(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)), Times.Never);
    }

    [Fact]
    public async Task PublishRequestAsync_CallbackException_FaultsPromptly()
    {
        var reply = new FakeMessage1(Guid.NewGuid()) { Username = "PublishReply" };
        var request = new FakeMessage1(Guid.NewGuid());
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerializeAny<FakeMessage1>(messageBytes);
        RequestReplyManager? manager = null;
        string? capturedMessageId = null;
        var callbackException = new InvalidOperationException("callback failed");

        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(reply);

        var options = new RequestOptions { Timeout = 5000, ExpectedReplyCount = 1 };
        var headers = new Dictionary<string, string>();

        _mockSendPipeline.Setup(pipeline => pipeline.ExecutePublishMessagePipelineAsync(
                It.Is<SendContext>(ctx => ctx.EndPoint == null),
                It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) =>
            {
                capturedMessageId = ctx.Headers["RequestMessageId"];
                Task.Run(() =>
                {
                    try
                    {
                        manager!.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));
                    }
                    catch
                    {
                        // Keep the test focused on the task returned to the publisher.
                    }
                });
            })
            .Returns(Task.CompletedTask);

        manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);

        var publishTask = manager.PublishRequestAsync<FakeMessage1, FakeMessage1>(
            request,
            headers,
            options,
            _ => throw callbackException);

        var completedTask = await Task.WhenAny(publishTask, Task.Delay(500));

        Assert.Same(publishTask, completedTask);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => publishTask);
        Assert.Same(callbackException, ex);
    }

    [Fact]
    public async Task SendRequestAsync_DeserializeException_FaultsPromptly()
    {
        RequestReplyManager? manager = null;
        string? capturedMessageId = null;
        var deserializeException = new InvalidOperationException("deserialize failed");
        var options = new RequestOptions { Timeout = 5000 };
        var headers = new Dictionary<string, string>();
        var request = new FakeMessage1(Guid.NewGuid());
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerializeAny<FakeMessage1>(messageBytes);

        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Throws(deserializeException);

        _mockSendPipeline.Setup(pipeline => pipeline.ExecuteSendMessagePipelineAsync(
                It.Is<SendContext>(ctx => ctx.EndPoint == null),
                It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) =>
            {
                capturedMessageId = ctx.Headers["RequestMessageId"];
                Task.Run(() =>
                {
                    try
                    {
                        manager!.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));
                    }
                    catch
                    {
                    }
                });
            })
            .Returns(Task.CompletedTask);

        manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);

        var requestTask = manager.SendRequestAsync<FakeMessage1, FakeMessage1>(request, headers, options);
        var completedTask = await Task.WhenAny(requestTask, Task.Delay(500));

        Assert.Same(requestTask, completedTask);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => requestTask);
        Assert.Same(deserializeException, ex);
    }

    [Fact]
    public async Task PublishRequestAsync_DeserializeException_FaultsPromptly_AndStopsLaterReplies()
    {
        RequestReplyManager? manager = null;
        string? capturedMessageId = null;
        var deserializeException = new InvalidOperationException("deserialize failed");
        var callbackCount = 0;
        var options = new RequestOptions { Timeout = 5000, ExpectedReplyCount = 2 };
        var headers = new Dictionary<string, string>();
        var request = new FakeMessage1(Guid.NewGuid());
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerializeAny<FakeMessage1>(messageBytes);

        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Throws(deserializeException);

        _mockSendPipeline.Setup(pipeline => pipeline.ExecutePublishMessagePipelineAsync(
                It.Is<SendContext>(ctx => ctx.EndPoint == null),
                It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) =>
            {
                capturedMessageId = ctx.Headers["RequestMessageId"];
                Task.Run(async () =>
                {
                    try
                    {
                        manager!.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));
                    }
                    catch
                    {
                    }

                    await Task.Delay(50);

                    try
                    {
                        manager!.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));
                    }
                    catch
                    {
                    }
                });
            })
            .Returns(Task.CompletedTask);

        manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);

        var publishTask = manager.PublishRequestAsync<FakeMessage1, FakeMessage1>(
            request,
            headers,
            options,
            _ => callbackCount++);

        var completedTask = await Task.WhenAny(publishTask, Task.Delay(500));

        Assert.Same(publishTask, completedTask);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => publishTask);
        Assert.Same(deserializeException, ex);

        // Give the background Task.Run time to attempt a second reply, then assert it was suppressed.
        await WaitForCondition(() => false, TimeSpan.FromMilliseconds(200));

        Assert.Equal(0, callbackCount);
        _mockSerializer.Verify(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)), Times.Once);
    }

    [Fact]
    public async Task PublishRequestAsync_CallbackException_OnNonFinalReply_StopsLaterReplies()
    {
        var firstReply = new FakeMessage1(Guid.NewGuid()) { Username = "Reply1" };
        var secondReply = new FakeMessage1(Guid.NewGuid()) { Username = "Reply2" };
        var request = new FakeMessage1(Guid.NewGuid());
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerializeAny<FakeMessage1>(messageBytes);

        var deserializedReplies = new Queue<FakeMessage1>([firstReply, secondReply]);
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(deserializedReplies.Dequeue);

        RequestReplyManager? manager = null;
        string? capturedMessageId = null;
        var callbackCount = 0;
        var callbackException = new InvalidOperationException("callback failed");
        var options = new RequestOptions { Timeout = 5000, ExpectedReplyCount = 2 };
        var headers = new Dictionary<string, string>();

        _mockSendPipeline.Setup(pipeline => pipeline.ExecutePublishMessagePipelineAsync(
                It.Is<SendContext>(ctx => ctx.EndPoint == null),
                It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) =>
            {
                capturedMessageId = ctx.Headers["RequestMessageId"];
                Task.Run(() =>
                {
                    try
                    {
                        manager!.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));
                    }
                    catch
                    {
                    }

                    try
                    {
                        manager!.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));
                    }
                    catch
                    {
                    }
                });
            })
            .Returns(Task.CompletedTask);

        manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);

        var publishTask = manager.PublishRequestAsync<FakeMessage1, FakeMessage1>(
            request,
            headers,
            options,
            _ =>
            {
                callbackCount++;
                throw callbackException;
            });

        var completedTask = await Task.WhenAny(publishTask, Task.Delay(500));

        Assert.Same(publishTask, completedTask);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => publishTask);
        Assert.Same(callbackException, ex);

        // Give the background Task.Run time to attempt a second reply, then assert it was suppressed.
        await WaitForCondition(() => false, TimeSpan.FromMilliseconds(200));

        Assert.Equal(1, callbackCount);
        _mockSerializer.Verify(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)), Times.Once);
    }

    [Fact]
    public async Task PublishRequestAsync_IgnoresRepliesBeyondExpectedReplyCount()
    {
        var firstReply = new FakeMessage1(Guid.NewGuid()) { Username = "Reply1" };
        var secondReply = new FakeMessage1(Guid.NewGuid()) { Username = "Reply2" };
        var thirdReply = new FakeMessage1(Guid.NewGuid()) { Username = "Reply3" };
        var request = new FakeMessage1(Guid.NewGuid());
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerializeAny<FakeMessage1>(messageBytes);

        var deserializedReplies = new Queue<FakeMessage1>([firstReply, secondReply, thirdReply]);
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(deserializedReplies.Dequeue);

        RequestReplyManager? manager = null;
        string? capturedMessageId = null;
        var callbacks = new List<string>();

        var options = new RequestOptions { Timeout = 5000, ExpectedReplyCount = 2 };
        var headers = new Dictionary<string, string>();

        _mockSendPipeline.Setup(pipeline => pipeline.ExecutePublishMessagePipelineAsync(
                It.Is<SendContext>(ctx => ctx.EndPoint == null),
                It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) =>
            {
                capturedMessageId = ctx.Headers["RequestMessageId"];
                Task.Run(() =>
                {
                    manager!.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));
                    manager.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));
                    manager.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));
                });
            })
            .Returns(Task.CompletedTask);

        manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);

        var publishTask = manager.PublishRequestAsync<FakeMessage1, FakeMessage1>(
            request,
            headers,
            options,
            reply => callbacks.Add(reply.Username));

        await publishTask;

        Assert.Equal(["Reply1", "Reply2"], callbacks);
        _mockSerializer.Verify(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)), Times.Exactly(2));
    }

    [Fact]
    public async Task PublishRequestAsync_RemovesPendingRequest_WhenPublishPipelineThrows()
    {
        var pipelineException = new InvalidOperationException("publish failed");
        var headers = new Dictionary<string, string>();
        var request = new FakeMessage1(Guid.NewGuid());
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerializeAny<FakeMessage1>(messageBytes);
        var options = new RequestOptions { Timeout = 5000, ExpectedReplyCount = 1 };
        string? capturedMessageId = null;

        _mockSendPipeline.Setup(pipeline => pipeline.ExecutePublishMessagePipelineAsync(
                It.Is<SendContext>(ctx => ctx.EndPoint == null),
                It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) =>
            {
                capturedMessageId = ctx.Headers["RequestMessageId"];
            })
            .ThrowsAsync(pipelineException);

        var manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.PublishRequestAsync<FakeMessage1, FakeMessage1>(
                request,
                headers,
                options,
                _ => { }));

        Assert.Same(pipelineException, ex);
        Assert.NotNull(capturedMessageId);

        manager.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));

        _mockSerializer.Verify(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)), Times.Never);
    }

    [Fact]
    public async Task PublishRequestAsync_ThrowsRequestSendCancelled_WhenOutboundPublishStallsBeforeCompletion()
    {
        var observedCancellation = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;
        var request = new FakeMessage1(Guid.NewGuid());
        _mockSerializer.SetupSerializeAny<FakeMessage1>([1, 2, 3]);

        _mockSendPipeline.Setup(pipeline => pipeline.ExecutePublishMessagePipelineAsync(
                It.Is<SendContext>(ctx => ctx.EndPoint == null),
                It.IsAny<CancellationToken>()))
            .Returns<SendContext, CancellationToken>(async (_, token) =>
            {
                observedCancellation.TrySetResult(token);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });

        var manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);
        var options = new RequestOptions { Timeout = 100 };

        var publishTask = manager.PublishRequestAsync<FakeMessage1, FakeMessage1>(
            request,
            new Dictionary<string, string>(),
            options,
            _ => callbackCount++);

        var pipelineToken = await observedCancellation.Task;
        var completedTask = await Task.WhenAny(publishTask, Task.Delay(1000));

        Assert.Same(publishTask, completedTask);
        Assert.True(pipelineToken.CanBeCanceled);
        Assert.True(pipelineToken.IsCancellationRequested);
        Assert.Equal(0, callbackCount);
        // Stalled-send-then-timeout now fails fast with the typed cancellation exception
        // rather than waiting on the reply TCS to surface RequestTimeoutException.
        await Assert.ThrowsAsync<RequestSendCancelledException>(() => publishTask);
    }

    [Fact]
    public async Task SendRequestAsync_ThrowsRequestSendCancelled_WhenOutboundSendIsStalled()
    {
        var observedCancellation = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new FakeMessage1(Guid.NewGuid());
        _mockSerializer.SetupSerializeAny<FakeMessage1>([1, 2, 3]);

        _mockSendPipeline.Setup(pipeline => pipeline.ExecuteSendMessagePipelineAsync(
                It.Is<SendContext>(ctx => ctx.EndPoint == null),
                It.IsAny<CancellationToken>()))
            .Returns<SendContext, CancellationToken>(async (_, token) =>
            {
                observedCancellation.TrySetResult(token);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });

        var manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);
        var options = new RequestOptions { Timeout = 100 };

        var requestTask = manager.SendRequestAsync<FakeMessage1, FakeMessage1>(
            request,
            new Dictionary<string, string>(),
            options);

        var pipelineToken = await observedCancellation.Task;
        var completedTask = await Task.WhenAny(requestTask, Task.Delay(1000));

        Assert.Same(requestTask, completedTask);
        Assert.True(pipelineToken.CanBeCanceled);
        Assert.True(pipelineToken.IsCancellationRequested);
        // The send pipeline never finished before the linked CTS fired, so the typed
        // send-cancelled exception must surface instead of the reply-side timeout.
        await Assert.ThrowsAsync<RequestSendCancelledException>(() => requestTask);
    }

    // --- CancellationToken tests (Task 8) ---

    [Fact]
    public async Task SendRequestAsync_ExternalCancel_ThrowsOCE_NotRequestTimeoutException()
    {
        var rrm = new RequestReplyManager(Mock.Of<IMessageSerializer>(), Mock.Of<ISendMessagePipeline>());
        using var externalCts = new CancellationTokenSource();
        var options = new RequestOptions { Timeout = 300000 }; // 5 minutes ms

        var task = rrm.SendRequestAsync<FakeMessage1, FakeMessage1>(
            new FakeMessage1(Guid.NewGuid()), new Dictionary<string, string>(), options,
            externalCts.Token);

        externalCts.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task SendRequestAsync_Timeout_ThrowsRequestTimeoutException()
    {
        var rrm = new RequestReplyManager(Mock.Of<IMessageSerializer>(), Mock.Of<ISendMessagePipeline>());
        var options = new RequestOptions { Timeout = 50 }; // 50 ms

        var task = rrm.SendRequestAsync<FakeMessage1, FakeMessage1>(
            new FakeMessage1(Guid.NewGuid()), new Dictionary<string, string>(), options,
            CancellationToken.None);

        await Assert.ThrowsAsync<RequestTimeoutException>(() => task);
    }

    [Fact]
    public async Task SendRequestAsync_PreCancelledToken_ThrowsImmediately()
    {
        var rrm = new RequestReplyManager(Mock.Of<IMessageSerializer>(), Mock.Of<ISendMessagePipeline>());
        using var externalCts = new CancellationTokenSource();
        externalCts.Cancel();
        var options = new RequestOptions { Timeout = 300000 };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            rrm.SendRequestAsync<FakeMessage1, FakeMessage1>(
                new FakeMessage1(Guid.NewGuid()), new Dictionary<string, string>(),
                options, externalCts.Token));
    }

    // --- Gap 4: timeout-vs-cancel same-instant race ---

    [Fact]
    public async Task SendRequestAsync_TimeoutAndCancelFiredSimultaneously_ExactlyOneOutcomeAndHandleIsCleanedUp()
    {
        // Forces timeout and external cancellation to fire at the same instant.
        // Uses TaskCompletionSource to block the send pipeline async (no thread-pool blocking),
        // then releases it only after arming both signals. Asserts exactly one of the
        // two deterministic outcomes (RequestTimeoutException or OperationCanceledException)
        // and that the request handle is cleaned up so a subsequent ProcessReply is a no-op.

        var sendEnteredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSendTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? capturedMessageId = null;
        var request = new FakeMessage1(Guid.NewGuid());
        _mockSerializer.SetupSerializeAny<FakeMessage1>([1, 2, 3]);

        using var externalCts = new CancellationTokenSource();

        // Use Returns with an async lambda so blocking happens asynchronously.
        _mockSendPipeline
            .Setup(p => p.ExecuteSendMessagePipelineAsync(
                It.Is<SendContext>(ctx => ctx.EndPoint == null),
                It.IsAny<CancellationToken>()))
            .Returns<SendContext, CancellationToken>(
                async (ctx, _) =>
                {
                    capturedMessageId = ctx.Headers["RequestMessageId"];
                    sendEnteredTcs.TrySetResult();
                    // Await the gate asynchronously — no thread-pool thread is blocked.
                    await releaseSendTcs.Task.ConfigureAwait(false);
                });

        _mockSerializer
            .Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(new FakeMessage1(Guid.NewGuid()));

        // Very short timeout — will fire shortly after the send pipeline completes.
        var options = new RequestOptions { Timeout = 1 };
        var manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);

        var requestTask = manager.SendRequestAsync<FakeMessage1, FakeMessage1>(
            request, new Dictionary<string, string>(), options,
            externalCts.Token);

        // Wait for the send pipeline to be entered.
        await sendEnteredTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Arm both signals: cancel the external token, then release the pipeline
        // so the 1ms timeout and the cancellation race to complete the TCS first.
        externalCts.Cancel();
        releaseSendTcs.TrySetResult();

        var ex = await Record.ExceptionAsync(() => requestTask.WaitAsync(TimeSpan.FromSeconds(5)));

        // Exactly one of the two deterministic outcomes is acceptable.
        Assert.True(
            ex is RequestTimeoutException or OperationCanceledException,
            $"Expected RequestTimeoutException or OperationCanceledException, got: {ex?.GetType().Name}: {ex?.Message}");

        // The handle must be cleaned up: a subsequent reply for the same id must be a no-op.
        Assert.NotNull(capturedMessageId);
        var rrm = (IReplyStatusRequestReplyManager)manager;
        var handledLate = rrm.TryProcessReply(capturedMessageId!, new byte[] { 0 }, typeof(FakeMessage1));
        Assert.False(handledLate, "request handle was not cleaned up after timeout/cancel");
        _mockSerializer.Verify(
            s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)),
            Times.Never);
    }

    // Atomic pending-request removal on timeout.

    /// <summary>
    /// Verifies that a ProcessReply call arriving after the timeout fires does NOT
    /// appear in the result set. The pending request must be removed atomically
    /// with TrySetResult so a late reply has no entry to append to and cannot
    /// smuggle itself into the snapshot returned to the caller.
    /// </summary>
    [Fact]
    public async Task SendRequestMultiAsync_LateReplyAfterTimeout_IsNotIncludedInResults()
    {
        // Arrange
        var lateReply = new FakeMessage1(Guid.NewGuid()) { Username = "LateUser" };
        var request = new FakeMessage1(Guid.NewGuid());
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerializeAny<FakeMessage1>(messageBytes);

        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(lateReply);

        RequestReplyManager? manager = null;
        string? capturedMessageId = null;

        // Use a very short timeout so the task completes before any reply arrives.
        var options = new RequestOptions { Timeout = 50 };
        var headers = new Dictionary<string, string>();

        _mockSendPipeline.Setup(pipeline => pipeline.ExecuteSendMessagePipelineAsync(
                It.Is<SendContext>(ctx => ctx.EndPoint == null),
                It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) =>
            {
                capturedMessageId = ctx.Headers["RequestMessageId"];
            })
            .Returns(Task.CompletedTask);

        manager = new RequestReplyManager(_mockSerializer.Object, _mockSendPipeline.Object);

        // Act — let it time out
        var results = await manager.SendRequestMultiAsync<FakeMessage1, FakeMessage1>(
            request, headers, options);

        // The task has now completed (timed out).  Simulate a late reply arriving
        // after the timeout has already fired and the entry should be removed.
        Assert.NotNull(capturedMessageId);
        manager.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));

        // Assert — the late reply must NOT appear in the snapshot returned before it arrived.
        Assert.Empty(results);

        // Also verify that a second call with the same id is a no-op (entry gone).
        // If the entry were still present, Deserialize would be called a second time.
        _mockSerializer.Verify(
            s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)),
            Times.Never);
    }
}
