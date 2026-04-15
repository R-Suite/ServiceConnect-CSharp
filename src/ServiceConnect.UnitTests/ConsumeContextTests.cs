using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Moq;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests
{
    public class ConsumeContextTestReply : Message
    {
        public ConsumeContextTestReply(Guid correlationId) : base(correlationId) { }
        public string? Value { get; set; }
    }

    public class ConsumeContextTests
    {
        private readonly Mock<IBus> _mockBus;
        private readonly IQueueConfiguration _queueConfig;
        private readonly IBusConfiguration _busConfig;

        public ConsumeContextTests()
        {
            _mockBus = new Mock<IBus>();
            _mockBus
                .Setup(b => b.SendAsync(It.IsAny<ConsumeContextTestReply>(), It.IsAny<SendOptions?>()))
                .Returns(Task.CompletedTask);

            var queueConfig = new QueueConfiguration
            {
                QueueName = "my-queue",
                ErrorQueueName = "errors",
                AuditQueueName = "audit"
            };
            _queueConfig = queueConfig;

            _busConfig = new BusConfiguration();
        }

        [Fact]
        public void Properties_AreSetFromConstructor()
        {
            var headers = new Dictionary<string, object>
            {
                { HeaderKeys.MessageId, "msg-123" }
            };

            var context = new ConsumeContext(_mockBus.Object, headers, _queueConfig, _busConfig);

            Assert.Same(_mockBus.Object, context.Bus);
            // Headers is wrapped in ReadOnlyDictionary (R-088); check contents rather than reference.
            Assert.Equal(headers, context.Headers);
            Assert.Equal("msg-123", context.MessageId);
        }

        [Fact]
        public void MessageId_ReturnsNull_WhenNotInHeaders()
        {
            var headers = new Dictionary<string, object>();
            var context = new ConsumeContext(_mockBus.Object, headers, _queueConfig, _busConfig);

            Assert.Null(context.MessageId);
        }

        [Fact]
        public void CorrelationId_ParsesFromHeaders()
        {
            var expected = Guid.NewGuid();
            var headers = new Dictionary<string, object>
            {
                { HeaderKeys.CorrelationId, expected.ToString() }
            };

            var context = new ConsumeContext(_mockBus.Object, headers, _queueConfig, _busConfig);

            Assert.Equal(expected, context.CorrelationId);
        }

        [Fact]
        public void CorrelationId_ReturnsEmptyGuid_WhenNotInHeaders()
        {
            var headers = new Dictionary<string, object>();
            var context = new ConsumeContext(_mockBus.Object, headers, _queueConfig, _busConfig);

            Assert.Equal(Guid.Empty, context.CorrelationId);
        }

        [Fact]
        public async Task ReplyAsync_SendsToSourceAddress_WhenKnownQueue()
        {
            var requestMessageId = Guid.NewGuid().ToString();
            var headers = new Dictionary<string, object>
            {
                { HeaderKeys.SourceAddress, "my-queue" },
                { HeaderKeys.RequestMessageId, requestMessageId }
            };

            var context = new ConsumeContext(_mockBus.Object, headers, _queueConfig, _busConfig);
            var reply = new ConsumeContextTestReply(Guid.NewGuid()) { Value = "hello" };

            await context.ReplyAsync(reply);

            _mockBus.Verify(b => b.SendAsync(
                reply,
                It.Is<SendOptions?>(o =>
                    o.HasValue &&
                    o.Value.EndPoint == "my-queue" &&
                    o.Value.Headers != null &&
                    o.Value.Headers["ResponseMessageId"] == requestMessageId)),
                Times.Once);
        }

        [Fact]
        public async Task ReplyAsync_ThrowsWhenSourceAddressNotKnown()
        {
            var headers = new Dictionary<string, object>
            {
                { HeaderKeys.SourceAddress, "unknown-evil-queue" }
            };

            var context = new ConsumeContext(_mockBus.Object, headers, _queueConfig, _busConfig);
            var reply = new ConsumeContextTestReply(Guid.NewGuid()) { Value = "hello" };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => context.ReplyAsync(reply));
            Assert.Contains("not a recognized queue", ex.Message);
            Assert.Contains("unknown-evil-queue", ex.Message);
        }

        [Fact]
        public async Task ReplyAsync_AllowsUnknownQueue_WhenValidationDisabled()
        {
            var headers = new Dictionary<string, object>
            {
                { HeaderKeys.SourceAddress, "unknown-but-allowed" },
                { HeaderKeys.RequestMessageId, Guid.NewGuid().ToString() }
            };

            var busConfig = new BusConfiguration { ValidateReplyDestinations = false };
            var context = new ConsumeContext(_mockBus.Object, headers, _queueConfig, busConfig);
            var reply = new ConsumeContextTestReply(Guid.NewGuid()) { Value = "hello" };

            await context.ReplyAsync(reply);

            _mockBus.Verify(b => b.SendAsync(
                reply,
                It.Is<SendOptions?>(o => o.HasValue && o.Value.EndPoint == "unknown-but-allowed")),
                Times.Once);
        }

        [Fact]
        public async Task ReplyAsync_AllowsReplyToQueueMappingDestination()
        {
            var queueConfig = new QueueConfiguration { QueueName = "my-queue" };
            queueConfig.AddQueueMapping(typeof(ConsumeContextTestReply), "mapped-reply-queue");

            var headers = new Dictionary<string, object>
            {
                { HeaderKeys.SourceAddress, "mapped-reply-queue" },
                { HeaderKeys.RequestMessageId, Guid.NewGuid().ToString() }
            };

            var context = new ConsumeContext(_mockBus.Object, headers, queueConfig, _busConfig);
            var reply = new ConsumeContextTestReply(Guid.NewGuid()) { Value = "hello" };

            await context.ReplyAsync(reply);

            _mockBus.Verify(b => b.SendAsync(
                reply,
                It.Is<SendOptions?>(o => o.HasValue && o.Value.EndPoint == "mapped-reply-queue")),
                Times.Once);
        }

        [Fact]
        public async Task ReplyAsync_AllowsReplyToErrorQueue()
        {
            var headers = new Dictionary<string, object>
            {
                { HeaderKeys.SourceAddress, "errors" },
                { HeaderKeys.RequestMessageId, Guid.NewGuid().ToString() }
            };

            var context = new ConsumeContext(_mockBus.Object, headers, _queueConfig, _busConfig);
            var reply = new ConsumeContextTestReply(Guid.NewGuid()) { Value = "hello" };

            await context.ReplyAsync(reply);

            _mockBus.Verify(b => b.SendAsync(
                reply,
                It.Is<SendOptions?>(o => o.HasValue && o.Value.EndPoint == "errors")),
                Times.Once);
        }

        [Fact]
        public void IsKnownQueue_MatchesCaseInsensitively()
        {
            var queueConfig = new QueueConfiguration
            {
                QueueName = "MyQueue",
                ErrorQueueName = "Errors",
                AuditQueueName = "Audit"
            };

            Assert.True(ConsumeContext.IsKnownQueue("myqueue", queueConfig));
            Assert.True(ConsumeContext.IsKnownQueue("ERRORS", queueConfig));
            Assert.True(ConsumeContext.IsKnownQueue("audit", queueConfig));
            Assert.False(ConsumeContext.IsKnownQueue("unknown", queueConfig));
        }
    }
}
