using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

            _persistor.Setup(i => i.InsertAsync(messageId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

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

            _persistor.Setup(i => i.InsertAsync(messageId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ThrowsAsync(new Exception());

            var outgoingFilter = new OutgoingFilter(_persistor.Object);
            var envelope = new Envelope();
            envelope.Headers = new Dictionary<string, object>();
            envelope.Headers = new Dictionary<string, object> { { "MessageId", Encoding.ASCII.GetBytes(messageId.ToString()) } };


            // Act
            var result = outgoingFilter.Process(envelope);


            // Assert
            Assert.True(result);
        }

    }
}
