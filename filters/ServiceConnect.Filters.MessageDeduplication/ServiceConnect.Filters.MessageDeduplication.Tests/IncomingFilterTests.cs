using System;
using System.Collections.Generic;
using System.Text;
using Moq;
using ServiceConnect.Filters.MessageDeduplication.Filters;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.Filters.MessageDeduplication.Tests
{
    public class IncomingFilterTests
    {
        readonly Mock<IMessageDeduplicationPersistor> _persistor;

        public IncomingFilterTests()
        {
            _persistor = new Mock<IMessageDeduplicationPersistor>();
        }

        [Fact]
        public void ShouldProcessNewMessageWhenRedeliveredFlagIsNotSet()
        {
            // Arrange
            var incomingFilter = new IncomingFilter(_persistor.Object);
            var envelope = new Envelope();
            envelope.Headers = new Dictionary<string, object>();


            // Act
            var result = incomingFilter.Process(envelope);


            // Assert
            Assert.True(result);
        }

        [Fact]
        public void ShouldProcessNewMessageWhenRedeliveredFlagIsSet()
        {
            // Arrange
            var incomingFilter = new IncomingFilter(_persistor.Object);
            Guid messageId = Guid.NewGuid();
            var envelope = new Envelope();
            envelope.Headers = new Dictionary<string, object> { { "Redelivered", true }, { "MessageId",  Encoding.ASCII.GetBytes(messageId.ToString()) } };


            // Act
            var result = incomingFilter.Process(envelope);


            // Assert
            Assert.True(result);
        }

        [Fact]
        public void ShouldNotProcessDuplicateMessageWhenRedeliveredFlagIsSet()
        {
            // Arrange
            Guid messageId = Guid.NewGuid();
            _persistor.Setup(i => i.GetMessageExists(messageId)).Returns(true);
            var incomingFilter = new IncomingFilter(_persistor.Object);
            var envelope = new Envelope();
            envelope.Headers = new Dictionary<string, object> { { "Redelivered", true }, { "MessageId", Encoding.ASCII.GetBytes(messageId.ToString()) } };


            // Act
            var result = incomingFilter.Process(envelope);


            // Assert
            Assert.False(result);
        }

        [Fact]
        public void ShouldRethrowAnyInternalException()
        {
            // Arrange
            Guid messageId = Guid.NewGuid();
            _persistor.Setup(i => i.GetMessageExists(messageId)).Throws(new Exception());
            var incomingFilter = new IncomingFilter(_persistor.Object);
            var envelope = new Envelope();
            envelope.Headers = new Dictionary<string, object> { { "Redelivered", true }, { "MessageId", Encoding.ASCII.GetBytes(messageId.ToString()) } };


            // Act / Assert
            Assert.Throws<Exception>(() => incomingFilter.Process(envelope));
        }
    }
}
