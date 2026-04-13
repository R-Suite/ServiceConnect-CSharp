using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests
{
    // Marker abstract classes for ordering tests
    public abstract class FakeFilter1 : IFilter
    {
        public abstract Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default);
    }

    public abstract class FakeFilter2 : IFilter
    {
        public abstract Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default);
    }

    public class FilterPipelineTests
    {
        private readonly PipelineConfiguration _config;
        private readonly Mock<IServiceProvider> _mockServiceProvider;
        private readonly FilterPipeline _pipeline;

        public FilterPipelineTests()
        {
            _config = new PipelineConfiguration();
            _mockServiceProvider = new Mock<IServiceProvider>();
            _pipeline = new FilterPipeline(_config, _mockServiceProvider.Object);
        }

        [Fact]
        public async Task ExecuteOutgoingFiltersAsync_WithNoFilters_ReturnsFalse()
        {
            var envelope = new Envelope();
            var result = await _pipeline.ExecuteOutgoingFiltersAsync(envelope);
            Assert.False(result);
        }

        [Fact]
        public async Task ExecuteOutgoingFiltersAsync_WhenFilterReturnsTrue_ReturnsFalse()
        {
            // true from filter = continue processing => pipeline returns false (not stopped)
            var mockFilter = new Mock<FakeFilter1>();
            mockFilter.Setup(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

            _mockServiceProvider
                .Setup(sp => sp.GetService(typeof(FakeFilter1)))
                .Returns(mockFilter.Object);

            _config.OutgoingFilters.Add(typeof(FakeFilter1));

            var envelope = new Envelope();
            var result = await _pipeline.ExecuteOutgoingFiltersAsync(envelope);

            Assert.False(result);
        }

        [Fact]
        public async Task ExecuteOutgoingFiltersAsync_WhenFilterReturnsFalse_ReturnsTrue()
        {
            // false from filter = stop pipeline => pipeline returns true (stopped)
            var mockFilter = new Mock<FakeFilter1>();
            mockFilter.Setup(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

            _mockServiceProvider
                .Setup(sp => sp.GetService(typeof(FakeFilter1)))
                .Returns(mockFilter.Object);

            _config.OutgoingFilters.Add(typeof(FakeFilter1));

            var envelope = new Envelope();
            var result = await _pipeline.ExecuteOutgoingFiltersAsync(envelope);

            Assert.True(result);
        }

        [Fact]
        public async Task ExecuteOutgoingFiltersAsync_ExecutesFiltersInOrder()
        {
            var callOrder = new List<string>();

            var mockFilter1 = new Mock<FakeFilter1>();
            mockFilter1.Setup(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
                .Callback(() => callOrder.Add("filter1"))
                .ReturnsAsync(true);

            var mockFilter2 = new Mock<FakeFilter2>();
            mockFilter2.Setup(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
                .Callback(() => callOrder.Add("filter2"))
                .ReturnsAsync(true);

            _mockServiceProvider
                .Setup(sp => sp.GetService(typeof(FakeFilter1)))
                .Returns(mockFilter1.Object);
            _mockServiceProvider
                .Setup(sp => sp.GetService(typeof(FakeFilter2)))
                .Returns(mockFilter2.Object);

            _config.OutgoingFilters.Add(typeof(FakeFilter1));
            _config.OutgoingFilters.Add(typeof(FakeFilter2));

            var envelope = new Envelope();
            await _pipeline.ExecuteOutgoingFiltersAsync(envelope);

            Assert.Equal(new[] { "filter1", "filter2" }, callOrder);
        }

        [Fact]
        public async Task ExecuteOutgoingFiltersAsync_StopsAtFirstBlockingFilter()
        {
            var mockFilter1 = new Mock<FakeFilter1>();
            mockFilter1.Setup(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(false); // blocks

            var mockFilter2 = new Mock<FakeFilter2>();
            mockFilter2.Setup(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

            _mockServiceProvider
                .Setup(sp => sp.GetService(typeof(FakeFilter1)))
                .Returns(mockFilter1.Object);
            _mockServiceProvider
                .Setup(sp => sp.GetService(typeof(FakeFilter2)))
                .Returns(mockFilter2.Object);

            _config.OutgoingFilters.Add(typeof(FakeFilter1));
            _config.OutgoingFilters.Add(typeof(FakeFilter2));

            var envelope = new Envelope();
            await _pipeline.ExecuteOutgoingFiltersAsync(envelope);

            // Filter2 should never have been called
            mockFilter2.Verify(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task ExecuteOutgoingFiltersAsync_ThrowsWhenFilterNotRegistered()
        {
            _mockServiceProvider
                .Setup(sp => sp.GetService(typeof(FakeFilter1)))
                .Returns(null!);

            _config.OutgoingFilters.Add(typeof(FakeFilter1));

            var envelope = new Envelope();
            await Assert.ThrowsAsync<InvalidOperationException>(() => _pipeline.ExecuteOutgoingFiltersAsync(envelope));
        }

        [Fact]
        public async Task ExecuteBeforeConsumingFiltersAsync_WithNoFilters_ReturnsFalse()
        {
            var envelope = new Envelope();
            var result = await _pipeline.ExecuteBeforeConsumingFiltersAsync(envelope);
            Assert.False(result);
        }

        [Fact]
        public async Task ExecuteAfterConsumingFiltersAsync_WithNoFilters_ReturnsFalse()
        {
            var envelope = new Envelope();
            var result = await _pipeline.ExecuteAfterConsumingFiltersAsync(envelope);
            Assert.False(result);
        }

        [Fact]
        public async Task ExecuteOutgoingFiltersAsync_PreCancelledToken_ThrowsOCEBeforeFilterRuns()
        {
            var mockFilter = new Mock<FakeFilter1>();
            mockFilter.Setup(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

            _mockServiceProvider
                .Setup(sp => sp.GetService(typeof(FakeFilter1)))
                .Returns(mockFilter.Object);

            _config.OutgoingFilters.Add(typeof(FakeFilter1));

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var envelope = new Envelope();
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                _pipeline.ExecuteOutgoingFiltersAsync(envelope, cts.Token));

            mockFilter.Verify(f => f.ProcessAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
