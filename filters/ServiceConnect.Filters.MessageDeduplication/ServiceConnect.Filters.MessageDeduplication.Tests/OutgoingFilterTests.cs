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
    public class OutgoingFilterTests
    {
        private readonly Mock<IMessageDeduplicationPersistor> _persistor;

        public OutgoingFilterTests()
        {
            _persistor = new Mock<IMessageDeduplicationPersistor>();
        }

        [Fact]
        public void ShouldPersistTheMessage()
        {
            // Arrange
            Guid messageId = Guid.NewGuid();

            var deduplicationSettings = DeduplicationFilterSettings.Instance;
            deduplicationSettings.DisableMsgExpiry = true;

            _persistor.Setup(i => i.Insert(messageId, It.IsAny<DateTime>()));

            var outgoingFilter = new OutgoingFilter(_persistor.Object);
            var envelope = new Envelope();
            envelope.Headers = new Dictionary<string, object>();
            envelope.Headers = new Dictionary<string, object> { { "MessageId", Encoding.ASCII.GetBytes(messageId.ToString()) } };


            // Act
            var result = outgoingFilter.Process(envelope);


            // Assert
            Assert.True(result);
            _persistor.VerifyAll();
        }

        [Fact]
        public void ShouldSwallowPersistanceException()
        {
            // Arrange
            Guid messageId = Guid.NewGuid();

            var deduplicationSettings = DeduplicationFilterSettings.Instance;
            deduplicationSettings.DisableMsgExpiry = true;

            _persistor.Setup(i => i.Insert(messageId, It.IsAny<DateTime>())).Throws(new Exception());

            var outgoingFilter = new OutgoingFilter(_persistor.Object);
            var envelope = new Envelope();
            envelope.Headers = new Dictionary<string, object>();
            envelope.Headers = new Dictionary<string, object> { { "MessageId", Encoding.ASCII.GetBytes(messageId.ToString()) } };


            // Act
            var result = outgoingFilter.Process(envelope);


            // Assert
            Assert.True(result);
        }

        [Fact]
        public void ShouldNotSetTimerWhenUsingRedisPersistor()
        {
            // Arrange
            Guid messageId = Guid.NewGuid();

            var deduplicationSettings = DeduplicationFilterSettings.Instance;
            deduplicationSettings.DisableMsgExpiry = false;

            IMessageDeduplicationPersistor persistor = new MessageDeduplicationPersistorRedis();

            var outgoingFilter = new OutgoingFilter(persistor);
            var envelope = new Envelope();
            envelope.Headers = new Dictionary<string, object>();
            envelope.Headers = new Dictionary<string, object> { { "MessageId", Encoding.ASCII.GetBytes(messageId.ToString()) } };


            // Act
            var result = outgoingFilter.Process(envelope);


            // Assert
            Assert.Null(outgoingFilter.Timer);
        }
    }
}
