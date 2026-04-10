using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests
{
    public class BusTests
    {
        private readonly Mock<IMessageSerializer> _mockSerializer;
        private readonly Mock<IFilterPipeline> _mockFilterPipeline;
        private readonly Mock<ISendMessagePipeline> _mockSendPipeline;
        private readonly Mock<IRequestReplyManager> _mockRequestReplyManager;
        private readonly Mock<IBusConfiguration> _mockConfig;
        private readonly Mock<ILogger<Bus>> _mockLogger;
        private readonly Bus _bus;

        public BusTests()
        {
            _mockSerializer = new Mock<IMessageSerializer>();
            _mockFilterPipeline = new Mock<IFilterPipeline>();
            _mockSendPipeline = new Mock<ISendMessagePipeline>();
            _mockRequestReplyManager = new Mock<IRequestReplyManager>();
            _mockConfig = new Mock<IBusConfiguration>();
            _mockLogger = new Mock<ILogger<Bus>>();

            // Default: filters pass through (false = not stopped)
            _mockFilterPipeline.Setup(x => x.ExecuteOutgoingFilters(It.IsAny<Envelope>())).Returns(false);
            _mockSerializer.Setup(x => x.Serialize(It.IsAny<FakeMessage1>())).Returns(new byte[] { 1, 2, 3 });

            _bus = new Bus(
                _mockSerializer.Object,
                _mockFilterPipeline.Object,
                _mockSendPipeline.Object,
                _mockRequestReplyManager.Object,
                _mockConfig.Object,
                _mockLogger.Object);
        }

        [Fact]
        public void IsConnected_ShouldBeFalse_WhenNotConsuming()
        {
            Assert.False(_bus.IsConnected);
        }

        [Fact]
        public void StartConsuming_ShouldSetIsConnectedToTrue()
        {
            _bus.StartConsuming();
            Assert.True(_bus.IsConnected);
        }

        [Fact]
        public void StopConsuming_ShouldSetIsConnectedToFalse()
        {
            _bus.StartConsuming();
            _bus.StopConsuming();
            Assert.False(_bus.IsConnected);
        }

        [Fact]
        public void Dispose_ShouldSetIsConnectedToFalse()
        {
            _bus.StartConsuming();
            _bus.Dispose();
            Assert.False(_bus.IsConnected);
        }

        [Fact]
        public async Task PublishAsync_ShouldSerializeAndPublish()
        {
            // Arrange
            var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
            var messageBytes = new byte[] { 1, 2, 3 };
            _mockSerializer.Setup(x => x.Serialize(message)).Returns(messageBytes);
            _mockSendPipeline.Setup(x => x.ExecutePublishMessagePipelineAsync(
                typeof(FakeMessage1), messageBytes, It.IsAny<Dictionary<string, string>>(), null))
                .Returns(Task.CompletedTask);

            // Act
            await _bus.PublishAsync(message);

            // Assert
            _mockSerializer.Verify(x => x.Serialize(message), Times.Once);
            _mockFilterPipeline.Verify(x => x.ExecuteOutgoingFilters(It.IsAny<Envelope>()), Times.Once);
            _mockSendPipeline.Verify(x => x.ExecutePublishMessagePipelineAsync(
                typeof(FakeMessage1), messageBytes, It.IsAny<Dictionary<string, string>>(), null), Times.Once);
        }

        [Fact]
        public async Task PublishAsync_ShouldNotPublish_WhenFilterBlocksMessage()
        {
            // Arrange
            _mockFilterPipeline.Setup(x => x.ExecuteOutgoingFilters(It.IsAny<Envelope>())).Returns(true);
            var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };

            // Act
            await _bus.PublishAsync(message);

            // Assert
            _mockSendPipeline.Verify(x => x.ExecutePublishMessagePipelineAsync(
                It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<string>()),
                Times.Never);
        }

        [Fact]
        public async Task PublishAsync_WithRoutingKey_ShouldIncludeRoutingKeyInHeaders()
        {
            // Arrange
            var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
            var options = new PublishOptions { RoutingKey = "my-routing-key" };

            _mockSendPipeline.Setup(x => x.ExecutePublishMessagePipelineAsync(
                It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), null))
                .Returns(Task.CompletedTask);

            // Act
            await _bus.PublishAsync(message, options);

            // Assert
            _mockSendPipeline.Verify(x => x.ExecutePublishMessagePipelineAsync(
                typeof(FakeMessage1),
                It.IsAny<byte[]>(),
                It.Is<Dictionary<string, string>>(h => h.ContainsKey("RoutingKey") && h["RoutingKey"] == "my-routing-key"),
                null), Times.Once);
        }

        [Fact]
        public async Task SendAsync_ShouldSerializeAndSend()
        {
            // Arrange
            var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
            var messageBytes = new byte[] { 1, 2, 3 };
            _mockSerializer.Setup(x => x.Serialize(message)).Returns(messageBytes);
            _mockSendPipeline.Setup(x => x.ExecuteSendMessagePipelineAsync(
                typeof(FakeMessage1), messageBytes, It.IsAny<Dictionary<string, string>>(), null))
                .Returns(Task.CompletedTask);

            // Act
            await _bus.SendAsync(message);

            // Assert
            _mockSerializer.Verify(x => x.Serialize(message), Times.Once);
            _mockSendPipeline.Verify(x => x.ExecuteSendMessagePipelineAsync(
                typeof(FakeMessage1), messageBytes, It.IsAny<Dictionary<string, string>>(), null), Times.Once);
        }

        [Fact]
        public async Task SendAsync_WithEndPoint_ShouldSendToEndPoint()
        {
            // Arrange
            var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
            var options = new SendOptions { EndPoint = "MyEndPoint" };

            _mockSendPipeline.Setup(x => x.ExecuteSendMessagePipelineAsync(
                It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), "MyEndPoint"))
                .Returns(Task.CompletedTask);

            // Act
            await _bus.SendAsync(message, options);

            // Assert
            _mockSendPipeline.Verify(x => x.ExecuteSendMessagePipelineAsync(
                typeof(FakeMessage1), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), "MyEndPoint"), Times.Once);
        }

        [Fact]
        public async Task SendAsync_WithMultipleEndPoints_ShouldSendToEach()
        {
            // Arrange
            var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
            var options = new SendOptions { EndPoints = new List<string> { "EP1", "EP2" } };

            _mockSendPipeline.Setup(x => x.ExecuteSendMessagePipelineAsync(
                It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<string>()))
                .Returns(Task.CompletedTask);

            // Act
            await _bus.SendAsync(message, options);

            // Assert
            _mockSendPipeline.Verify(x => x.ExecuteSendMessagePipelineAsync(
                typeof(FakeMessage1), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), "EP1"), Times.Once);
            _mockSendPipeline.Verify(x => x.ExecuteSendMessagePipelineAsync(
                typeof(FakeMessage1), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), "EP2"), Times.Once);
        }

        [Fact]
        public async Task SendAsync_ShouldNotSend_WhenFilterBlocksMessage()
        {
            // Arrange
            _mockFilterPipeline.Setup(x => x.ExecuteOutgoingFilters(It.IsAny<Envelope>())).Returns(true);
            var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };

            // Act
            await _bus.SendAsync(message);

            // Assert
            _mockSendPipeline.Verify(x => x.ExecuteSendMessagePipelineAsync(
                It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<string>()),
                Times.Never);
        }

        [Fact]
        public void Route_ShouldSendToFirstDestination_WithRoutingSlipForRemaining()
        {
            // Arrange
            var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
            var destinations = new List<string> { "Dest1", "Dest2", "Dest3" };

            _mockSendPipeline.Setup(x => x.ExecuteSendMessagePipelineAsync(
                It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), "Dest1"))
                .Returns(Task.CompletedTask);

            // Act
            _bus.Route(message, destinations);

            // Assert
            _mockSendPipeline.Verify(x => x.ExecuteSendMessagePipelineAsync(
                typeof(FakeMessage1),
                It.IsAny<byte[]>(),
                It.Is<Dictionary<string, string>>(h => h.ContainsKey("RoutingSlip") && h["RoutingSlip"] == "Dest2,Dest3"),
                "Dest1"), Times.Once);
        }

        [Fact]
        public void Route_ShouldThrow_WhenNoDestinations()
        {
            // Arrange
            var message = new FakeMessage1(Guid.NewGuid());

            // Act & Assert
            Assert.Throws<ArgumentException>(() => _bus.Route(message, new List<string>()));
        }

        [Fact]
        public void Constructor_ShouldThrow_WhenDependencyIsNull()
        {
            Assert.Throws<ArgumentNullException>(() => new Bus(null!, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockConfig.Object, _mockLogger.Object));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, null!, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockConfig.Object, _mockLogger.Object));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, null!, _mockRequestReplyManager.Object, _mockConfig.Object, _mockLogger.Object));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, null!, _mockConfig.Object, _mockLogger.Object));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, null!, _mockLogger.Object));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockConfig.Object, null!));
        }
    }
}
