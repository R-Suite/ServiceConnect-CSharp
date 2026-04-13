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
    public class OutgoingDeduplicationFilterTests
    {
        private readonly Mock<IMessageDeduplicationPersistor> _persistor = new();

        private OutgoingDeduplicationFilter CreateFilter()
        {
            OutgoingDeduplicationFilter.OverridePersistorForTesting(_persistor.Object);
            return new OutgoingDeduplicationFilter();
        }

        private static Envelope EnvelopeWithMessageId(Guid id)
        {
            return new Envelope
            {
                Headers = new Dictionary<string, object>
                {
                    { "MessageId", Encoding.ASCII.GetBytes(id.ToString()) }
                }
            };
        }

        [Fact]
        public async Task ProcessAsync_HappyPath_CallsInsertAndReturnsTrue()
        {
            var id = Guid.NewGuid();
            _persistor.Setup(p => p.InsertAsync(id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var filter = CreateFilter();
            var result = await filter.ProcessAsync(EnvelopeWithMessageId(id));

            Assert.True(result);
            _persistor.VerifyAll();
        }

        [Fact]
        public async Task ProcessAsync_PersistorThrows_ExceptionPropagates()
        {
            var id = Guid.NewGuid();
            _persistor.Setup(p => p.InsertAsync(id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("boom"));

            var filter = CreateFilter();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                filter.ProcessAsync(EnvelopeWithMessageId(id)));
        }

        [Fact]
        public async Task ProcessAsync_PreCancelledToken_ThrowsOCE()
        {
            var id = Guid.NewGuid();
            _persistor.Setup(p => p.InsertAsync(id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var filter = CreateFilter();

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                filter.ProcessAsync(EnvelopeWithMessageId(id), cts.Token));
        }
    }
}
