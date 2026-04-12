using System;
using System.Collections.Generic;
using System.Text;
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

            Task sendAction(Type type, byte[] bytes, Dictionary<string, string> hdrs, string? endpoint)
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

            Task sendAction(Type type, byte[] bytes, Dictionary<string, string> hdrs, string? endpoint)
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

            Task sendAction(Type type, byte[] bytes, Dictionary<string, string> hdrs, string? endpoint)
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

            Task sendAction(Type type, byte[] bytes, Dictionary<string, string> hdrs, string? endpoint)
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
    }
}
