using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests
{
    public class RequestReplyManagerTests
    {
        private readonly Mock<IMessageSerializer> _mockSerializer;

        public RequestReplyManagerTests()
        {
            _mockSerializer = new Mock<IMessageSerializer>();
        }

        [Fact]
        public void Constructor_ThrowsWhenSerializerIsNull()
        {
            Assert.Throws<ArgumentNullException>(() => new RequestReplyManager(null!));
        }

        [Fact]
        public async Task SendRequestAsync_SendsMessageWithRequestMessageIdHeader()
        {
            // Arrange
            var replyId = Guid.NewGuid();
            var reply = new FakeMessage1(replyId) { Username = "TestUser" };

            RequestReplyManager? manager = null;
            string? capturedMessageId = null;

            _mockSerializer.Setup(s => s.Deserialize(It.IsAny<byte[]>(), typeof(FakeMessage1)))
                .Returns(reply);

            var options = new RequestOptions { Timeout = 5000 };
            var headers = new Dictionary<string, string>();
            var messageBytes = new byte[] { 1, 2, 3 };

            Task sendAction(Type type, byte[] bytes, Dictionary<string, string> hdrs, string? endpoint, CancellationToken ct)
            {
                capturedMessageId = hdrs["RequestMessageId"];
                // Simulate a reply inline on a background task
                Task.Run(async () =>
                {
                    await Task.Delay(10);
                    manager!.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));
                });
                return Task.CompletedTask;
            }

            manager = new RequestReplyManager(_mockSerializer.Object);

            // Act
            var result = await manager.SendRequestAsync<FakeMessage1, FakeMessage1>(
                messageBytes, headers, sendAction, options);

            // Assert
            Assert.NotNull(capturedMessageId);
            Assert.Equal(reply.Username, result.Username);
        }

        [Fact]
        public async Task SendRequestAsync_ThrowsRequestTimeoutException_WhenNoReply()
        {
            // Arrange
            var manager = new RequestReplyManager(_mockSerializer.Object);
            var options = new RequestOptions { Timeout = 100 };
            var headers = new Dictionary<string, string>();
            var messageBytes = new byte[] { 1, 2, 3 };

            Task sendAction(Type type, byte[] bytes, Dictionary<string, string> hdrs, string? endpoint, CancellationToken ct)
                => Task.CompletedTask;

            // Act & Assert
            await Assert.ThrowsAsync<RequestTimeoutException>(() =>
                manager.SendRequestAsync<FakeMessage1, FakeMessage1>(
                    messageBytes, headers, sendAction, options));
        }

        [Fact]
        public async Task SendRequestAsync_SendsToEndPoint_WhenSpecified()
        {
            // Arrange
            var replyId = Guid.NewGuid();
            var reply = new FakeMessage1(replyId) { Username = "EndpointUser" };

            RequestReplyManager? manager = null;
            string? capturedEndpoint = null;
            string? capturedMessageId = null;

            _mockSerializer.Setup(s => s.Deserialize(It.IsAny<byte[]>(), typeof(FakeMessage1)))
                .Returns(reply);

            var options = new RequestOptions { Timeout = 5000, EndPoint = "my-queue" };
            var headers = new Dictionary<string, string>();
            var messageBytes = new byte[] { 1, 2, 3 };

            Task sendAction(Type type, byte[] bytes, Dictionary<string, string> hdrs, string? endpoint, CancellationToken ct)
            {
                capturedEndpoint = endpoint;
                capturedMessageId = hdrs["RequestMessageId"];
                Task.Run(async () =>
                {
                    await Task.Delay(10);
                    manager!.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));
                });
                return Task.CompletedTask;
            }

            manager = new RequestReplyManager(_mockSerializer.Object);

            // Act
            await manager.SendRequestAsync<FakeMessage1, FakeMessage1>(
                messageBytes, headers, sendAction, options);

            // Assert
            Assert.Equal("my-queue", capturedEndpoint);
        }

        [Fact]
        public void ProcessReply_IgnoresUnknownMessageId()
        {
            // Arrange
            var manager = new RequestReplyManager(_mockSerializer.Object);
            var unknownId = Guid.NewGuid().ToString();

            // Act & Assert — should not throw
            var ex = Record.Exception(() =>
                manager.ProcessReply(unknownId, new byte[] { 1, 2, 3 }, typeof(FakeMessage1)));

            Assert.Null(ex);
            _mockSerializer.Verify(s => s.Deserialize(It.IsAny<byte[]>(), It.IsAny<Type>()), Times.Never);
        }

        [Fact]
        public async Task SendRequestMultiAsync_CollectsMultipleReplies()
        {
            // Arrange
            var reply1 = new FakeMessage1(Guid.NewGuid()) { Username = "User1" };
            var reply2 = new FakeMessage1(Guid.NewGuid()) { Username = "User2" };

            var callCount = 0;
            _mockSerializer.Setup(s => s.Deserialize(It.IsAny<byte[]>(), typeof(FakeMessage1)))
                .Returns(() => ++callCount == 1 ? reply1 : reply2);

            RequestReplyManager? manager = null;
            string? capturedMessageId = null;

            var options = new RequestOptions { Timeout = 5000, ExpectedReplyCount = 2 };
            var headers = new Dictionary<string, string>();
            var messageBytes = new byte[] { 1, 2, 3 };

            Task sendAction(Type type, byte[] bytes, Dictionary<string, string> hdrs, string? endpoint, CancellationToken ct)
            {
                capturedMessageId = hdrs["RequestMessageId"];
                Task.Run(async () =>
                {
                    await Task.Delay(10);
                    manager!.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));
                    await Task.Delay(10);
                    manager!.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));
                });
                return Task.CompletedTask;
            }

            manager = new RequestReplyManager(_mockSerializer.Object);

            // Act
            var results = await manager.SendRequestMultiAsync<FakeMessage1, FakeMessage1>(
                messageBytes, headers, sendAction, options);

            // Assert
            Assert.Equal(2, results.Count);
        }

        // --- CancellationToken tests (Task 8) ---

        [Fact]
        public async Task SendRequestAsync_ExternalCancel_ThrowsOCE_NotRequestTimeoutException()
        {
            var rrm = new RequestReplyManager(Mock.Of<IMessageSerializer>());
            using var externalCts = new CancellationTokenSource();
            var options = new RequestOptions { Timeout = 300000 }; // 5 minutes ms

            var task = rrm.SendRequestAsync<FakeMessage1, FakeMessage1>(
                new byte[] { 0 }, new Dictionary<string, string>(),
                (type, bytes, headers, endpoint, ct) => Task.CompletedTask,
                options,
                externalCts.Token);

            externalCts.CancelAfter(50);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }

        [Fact]
        public async Task SendRequestAsync_Timeout_ThrowsRequestTimeoutException()
        {
            var rrm = new RequestReplyManager(Mock.Of<IMessageSerializer>());
            var options = new RequestOptions { Timeout = 50 }; // 50 ms

            var task = rrm.SendRequestAsync<FakeMessage1, FakeMessage1>(
                new byte[] { 0 }, new Dictionary<string, string>(),
                (type, bytes, headers, endpoint, ct) => Task.CompletedTask,
                options,
                CancellationToken.None);

            await Assert.ThrowsAsync<RequestTimeoutException>(() => task);
        }

        [Fact]
        public async Task SendRequestAsync_PreCancelledToken_ThrowsImmediately()
        {
            var rrm = new RequestReplyManager(Mock.Of<IMessageSerializer>());
            using var externalCts = new CancellationTokenSource();
            externalCts.Cancel();
            var options = new RequestOptions { Timeout = 300000 };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                rrm.SendRequestAsync<FakeMessage1, FakeMessage1>(
                    new byte[] { 0 }, new Dictionary<string, string>(),
                    (type, bytes, headers, endpoint, ct) => Task.CompletedTask,
                    options, externalCts.Token));
        }

        // --- R-021: atomic pending-request removal on timeout ---

        /// <summary>
        /// Verifies that a ProcessReply call arriving after the timeout fires does NOT
        /// appear in the result set.  Before the R-021 fix, the entry remained in
        /// _pendingRequests between TrySetResult and the finally-block TryRemove, so a
        /// concurrently-arriving reply could still append to the response list and end
        /// up in the snapshot returned to the caller.
        /// </summary>
        [Fact]
        public async Task SendRequestMultiAsync_LateReplyAfterTimeout_IsNotIncludedInResults()
        {
            // Arrange
            var lateReply = new FakeMessage1(Guid.NewGuid()) { Username = "LateUser" };

            _mockSerializer.Setup(s => s.Deserialize(It.IsAny<byte[]>(), typeof(FakeMessage1)))
                .Returns(lateReply);

            RequestReplyManager? manager = null;
            string? capturedMessageId = null;

            // Use a very short timeout so the task completes before any reply arrives.
            var options = new RequestOptions { Timeout = 50 };
            var headers = new Dictionary<string, string>();
            var messageBytes = new byte[] { 1, 2, 3 };

            // sendAction captures the message id but does NOT send a reply — the
            // request will time out on its own.
            Task sendAction(Type type, byte[] bytes, Dictionary<string, string> hdrs, string? endpoint, CancellationToken ct)
            {
                capturedMessageId = hdrs["RequestMessageId"];
                return Task.CompletedTask;
            }

            manager = new RequestReplyManager(_mockSerializer.Object);

            // Act — let it time out
            var results = await manager.SendRequestMultiAsync<FakeMessage1, FakeMessage1>(
                messageBytes, headers, sendAction, options);

            // The task has now completed (timed out).  Simulate a late reply arriving
            // after the timeout has already fired and the entry should be removed.
            Assert.NotNull(capturedMessageId);
            manager.ProcessReply(capturedMessageId!, messageBytes, typeof(FakeMessage1));

            // Assert — the late reply must NOT appear in the snapshot returned before it arrived.
            Assert.Empty(results);

            // Also verify that a second call with the same id is a no-op (entry gone).
            // If the entry were still present, Deserialize would be called a second time.
            _mockSerializer.Verify(
                s => s.Deserialize(It.IsAny<byte[]>(), typeof(FakeMessage1)),
                Times.Never);
        }
    }
}
