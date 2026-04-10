using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Moq;
using ServiceConnect.Interfaces;
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

        public ConsumeContextTests()
        {
            _mockBus = new Mock<IBus>();
            _mockBus
                .Setup(b => b.SendAsync(It.IsAny<ConsumeContextTestReply>(), It.IsAny<SendOptions?>()))
                .Returns(Task.CompletedTask);
        }

        [Fact]
        public void Properties_AreSetFromConstructor()
        {
            var headers = new Dictionary<string, object>
            {
                { HeaderKeys.MessageId, "msg-123" }
            };

            var context = new ConsumeContext(_mockBus.Object, headers);

            Assert.Same(_mockBus.Object, context.Bus);
            Assert.Same(headers, context.Headers);
            Assert.Equal("msg-123", context.MessageId);
        }

        [Fact]
        public void MessageId_ReturnsNull_WhenNotInHeaders()
        {
            var headers = new Dictionary<string, object>();
            var context = new ConsumeContext(_mockBus.Object, headers);

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

            var context = new ConsumeContext(_mockBus.Object, headers);

            Assert.Equal(expected, context.CorrelationId);
        }

        [Fact]
        public void CorrelationId_ReturnsEmptyGuid_WhenNotInHeaders()
        {
            var headers = new Dictionary<string, object>();
            var context = new ConsumeContext(_mockBus.Object, headers);

            Assert.Equal(Guid.Empty, context.CorrelationId);
        }

        [Fact]
        public void Reply_SendsToSourceAddress()
        {
            var requestMessageId = Guid.NewGuid().ToString();
            var headers = new Dictionary<string, object>
            {
                { HeaderKeys.SourceAddress, "rabbitmq://localhost/reply-queue" },
                { HeaderKeys.RequestMessageId, requestMessageId }
            };

            var context = new ConsumeContext(_mockBus.Object, headers);
            var reply = new ConsumeContextTestReply(Guid.NewGuid()) { Value = "hello" };

            context.Reply(reply);

            _mockBus.Verify(b => b.SendAsync(
                reply,
                It.Is<SendOptions?>(o =>
                    o != null &&
                    o.EndPoint == "rabbitmq://localhost/reply-queue" &&
                    o.Headers != null &&
                    o.Headers["ResponseMessageId"] == requestMessageId)),
                Times.Once);
        }
    }
}
