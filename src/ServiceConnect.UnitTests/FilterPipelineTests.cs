using System;
using System.Collections.Generic;
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
        public IBus Bus { get; set; } = null!;
        public abstract bool Process(Envelope envelope);
    }

    public abstract class FakeFilter2 : IFilter
    {
        public IBus Bus { get; set; } = null!;
        public abstract bool Process(Envelope envelope);
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
        public void ExecuteOutgoingFilters_WithNoFilters_ReturnsFalse()
        {
            var envelope = new Envelope();
            var result = _pipeline.ExecuteOutgoingFilters(envelope);
            Assert.False(result);
        }

        [Fact]
        public void ExecuteOutgoingFilters_WhenFilterReturnsTrue_ReturnsFalse()
        {
            // true from filter = continue processing => pipeline returns false (not stopped)
            var mockFilter = new Mock<FakeFilter1>();
            mockFilter.Setup(f => f.Process(It.IsAny<Envelope>())).Returns(true);

            _mockServiceProvider
                .Setup(sp => sp.GetService(typeof(FakeFilter1)))
                .Returns(mockFilter.Object);

            _config.OutgoingFilters.Add(typeof(FakeFilter1));

            var envelope = new Envelope();
            var result = _pipeline.ExecuteOutgoingFilters(envelope);

            Assert.False(result);
        }

        [Fact]
        public void ExecuteOutgoingFilters_WhenFilterReturnsFalse_ReturnsTrue()
        {
            // false from filter = stop pipeline => pipeline returns true (stopped)
            var mockFilter = new Mock<FakeFilter1>();
            mockFilter.Setup(f => f.Process(It.IsAny<Envelope>())).Returns(false);

            _mockServiceProvider
                .Setup(sp => sp.GetService(typeof(FakeFilter1)))
                .Returns(mockFilter.Object);

            _config.OutgoingFilters.Add(typeof(FakeFilter1));

            var envelope = new Envelope();
            var result = _pipeline.ExecuteOutgoingFilters(envelope);

            Assert.True(result);
        }

        [Fact]
        public void ExecuteOutgoingFilters_ExecutesFiltersInOrder()
        {
            var callOrder = new List<string>();

            var mockFilter1 = new Mock<FakeFilter1>();
            mockFilter1.Setup(f => f.Process(It.IsAny<Envelope>()))
                .Callback(() => callOrder.Add("filter1"))
                .Returns(true);

            var mockFilter2 = new Mock<FakeFilter2>();
            mockFilter2.Setup(f => f.Process(It.IsAny<Envelope>()))
                .Callback(() => callOrder.Add("filter2"))
                .Returns(true);

            _mockServiceProvider
                .Setup(sp => sp.GetService(typeof(FakeFilter1)))
                .Returns(mockFilter1.Object);
            _mockServiceProvider
                .Setup(sp => sp.GetService(typeof(FakeFilter2)))
                .Returns(mockFilter2.Object);

            _config.OutgoingFilters.Add(typeof(FakeFilter1));
            _config.OutgoingFilters.Add(typeof(FakeFilter2));

            var envelope = new Envelope();
            _pipeline.ExecuteOutgoingFilters(envelope);

            Assert.Equal(new[] { "filter1", "filter2" }, callOrder);
        }

        [Fact]
        public void ExecuteOutgoingFilters_StopsAtFirstBlockingFilter()
        {
            var mockFilter1 = new Mock<FakeFilter1>();
            mockFilter1.Setup(f => f.Process(It.IsAny<Envelope>())).Returns(false); // blocks

            var mockFilter2 = new Mock<FakeFilter2>();
            mockFilter2.Setup(f => f.Process(It.IsAny<Envelope>())).Returns(true);

            _mockServiceProvider
                .Setup(sp => sp.GetService(typeof(FakeFilter1)))
                .Returns(mockFilter1.Object);
            _mockServiceProvider
                .Setup(sp => sp.GetService(typeof(FakeFilter2)))
                .Returns(mockFilter2.Object);

            _config.OutgoingFilters.Add(typeof(FakeFilter1));
            _config.OutgoingFilters.Add(typeof(FakeFilter2));

            var envelope = new Envelope();
            _pipeline.ExecuteOutgoingFilters(envelope);

            // Filter2 should never have been called
            mockFilter2.Verify(f => f.Process(It.IsAny<Envelope>()), Times.Never);
        }

        [Fact]
        public void ExecuteOutgoingFilters_ThrowsWhenFilterNotRegistered()
        {
            _mockServiceProvider
                .Setup(sp => sp.GetService(typeof(FakeFilter1)))
                .Returns(null!);

            _config.OutgoingFilters.Add(typeof(FakeFilter1));

            var envelope = new Envelope();
            Assert.Throws<InvalidOperationException>(() => _pipeline.ExecuteOutgoingFilters(envelope));
        }

        [Fact]
        public void ExecuteBeforeConsumingFilters_WithNoFilters_ReturnsFalse()
        {
            var envelope = new Envelope();
            var result = _pipeline.ExecuteBeforeConsumingFilters(envelope);
            Assert.False(result);
        }

        [Fact]
        public void ExecuteAfterConsumingFilters_WithNoFilters_ReturnsFalse()
        {
            var envelope = new Envelope();
            var result = _pipeline.ExecuteAfterConsumingFilters(envelope);
            Assert.False(result);
        }
    }
}
