using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
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
        private readonly DeduplicationFilterSettings _settings = new() { MsgExpiryHours = 24 };

        private OutgoingDeduplicationFilter CreateFilter() =>
            new(_persistor.Object, Options.Create(_settings));

        private static Envelope EnvelopeWithMessageId(Guid id) =>
            new() { Headers = new Dictionary<string, object> { { "MessageId", Encoding.ASCII.GetBytes(id.ToString()) } } };

        [Fact]
        public async Task ProcessAsync_HappyPath_CallsInsertAndContinues()
        {
            var id = Guid.NewGuid();
            _persistor.Setup(p => p.InsertAsync(id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var filter = CreateFilter();
            var result = await filter.ProcessAsync(EnvelopeWithMessageId(id));

            Assert.Equal(FilterAction.Continue, result);
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
