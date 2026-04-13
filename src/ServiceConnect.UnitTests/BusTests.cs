using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services.Processors;
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
        private readonly Mock<IQueueConfiguration> _mockQueueConfig;
        private readonly Mock<IMessageDispatcher> _mockDispatcher;
        private readonly IList<HandlerReference> _handlerReferences;
        private readonly ProcessManagerHandlerRegistry _processManagerRegistry;
        private readonly MessageHandlerRegistry _messageHandlerRegistry;
        private readonly StreamHandlerRegistry _streamHandlerRegistry;
        private readonly AggregatorRegistry _aggregatorRegistry;
        private readonly Bus _bus;

        public BusTests()
        {
            _mockSerializer = new Mock<IMessageSerializer>();
            _mockFilterPipeline = new Mock<IFilterPipeline>();
            _mockSendPipeline = new Mock<ISendMessagePipeline>();
            _mockRequestReplyManager = new Mock<IRequestReplyManager>();
            _mockConfig = new Mock<IBusConfiguration>();
            _mockLogger = new Mock<ILogger<Bus>>();
            _mockQueueConfig = new Mock<IQueueConfiguration>();
            _mockQueueConfig.Setup(x => x.QueueName).Returns("test-queue");

            // Default: filters pass through (false = not stopped)
            _mockFilterPipeline.Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
            _mockSerializer.Setup(x => x.Serialize(It.IsAny<FakeMessage1>())).Returns(new byte[] { 1, 2, 3 });

            _mockDispatcher = new Mock<IMessageDispatcher>();
            _handlerReferences = new List<HandlerReference>();
            _processManagerRegistry = new ProcessManagerHandlerRegistry(
                new List<HandlerReference>(),
                NullLogger<ProcessManagerHandlerRegistry>.Instance);
            _messageHandlerRegistry = new MessageHandlerRegistry(
                new List<HandlerReference>(),
                NullLogger<MessageHandlerRegistry>.Instance);
            _streamHandlerRegistry = new StreamHandlerRegistry(
                new List<HandlerReference>(),
                NullLogger<StreamHandlerRegistry>.Instance);
            _aggregatorRegistry = new AggregatorRegistry(
                new List<HandlerReference>(),
                new ServiceCollection().BuildServiceProvider(),
                NullLogger<AggregatorRegistry>.Instance);

            _bus = new Bus(
                _mockSerializer.Object,
                _mockFilterPipeline.Object,
                _mockSendPipeline.Object,
                _mockRequestReplyManager.Object,
                _mockLogger.Object,
                _mockQueueConfig.Object,
                _mockDispatcher.Object,
                _handlerReferences,
                _processManagerRegistry,
                _messageHandlerRegistry,
                _streamHandlerRegistry,
                _aggregatorRegistry);
        }

        [Fact]
        public void IsConnected_ShouldBeFalse_WhenNotConsuming()
        {
            Assert.False(_bus.IsConnected);
        }

        [Fact]
        public async Task StartConsumingAsync_ShouldThrow_WhenNoConsumerRegistered()
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => _bus.StartConsumingAsync());
        }

        [Fact]
        public async Task StartConsumingAsync_ShouldSetIsConnectedToTrue_WhenConsumerRegistered()
        {
            // Arrange
            var mockConsumer = new Mock<IConsumer>();
            mockConsumer.Setup(x => x.StartConsumingAsync(It.IsAny<string>(), It.IsAny<IList<string>>(), It.IsAny<ConsumerEventHandler>()))
                .Returns(Task.CompletedTask);

            var bus = new Bus(
                _mockSerializer.Object,
                _mockFilterPipeline.Object,
                _mockSendPipeline.Object,
                _mockRequestReplyManager.Object,
                _mockLogger.Object,
                _mockQueueConfig.Object,
                _mockDispatcher.Object,
                _handlerReferences,
                _processManagerRegistry,
                _messageHandlerRegistry,
                _streamHandlerRegistry,
                _aggregatorRegistry,
                mockConsumer.Object);

            // Act
            await bus.StartConsumingAsync();

            // Assert
            Assert.True(bus.IsConnected);
        }

        [Fact]
        public async Task StopConsumingAsync_ShouldSetIsConnectedToFalse()
        {
            // StopConsuming can be called even without starting (no consumer needed)
            await _bus.StopConsumingAsync();
            Assert.False(_bus.IsConnected);
        }

        [Fact]
        public async Task DisposeAsync_ShouldSetIsConnectedToFalse()
        {
            await _bus.DisposeAsync();
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
            _mockFilterPipeline.Verify(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()), Times.Once);
            _mockSendPipeline.Verify(x => x.ExecutePublishMessagePipelineAsync(
                typeof(FakeMessage1), messageBytes, It.IsAny<Dictionary<string, string>>(), null), Times.Once);
        }

        [Fact]
        public async Task PublishAsync_ShouldNotPublish_WhenFilterBlocksMessage()
        {
            // Arrange
            _mockFilterPipeline.Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
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
            _mockFilterPipeline.Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };

            // Act
            await _bus.SendAsync(message);

            // Assert
            _mockSendPipeline.Verify(x => x.ExecuteSendMessagePipelineAsync(
                It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<string>()),
                Times.Never);
        }

        [Fact]
        public async Task RouteAsync_ShouldSendToFirstDestination_WithRoutingSlipForRemaining()
        {
            // Arrange
            var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
            var destinations = new List<string> { "Dest1", "Dest2", "Dest3" };

            _mockSendPipeline.Setup(x => x.ExecuteSendMessagePipelineAsync(
                It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), "Dest1"))
                .Returns(Task.CompletedTask);

            // Act
            await _bus.RouteAsync(message, destinations);

            // Assert
            _mockSendPipeline.Verify(x => x.ExecuteSendMessagePipelineAsync(
                typeof(FakeMessage1),
                It.IsAny<byte[]>(),
                It.Is<Dictionary<string, string>>(h => h.ContainsKey("RoutingSlip") && h["RoutingSlip"] == "Dest2,Dest3"),
                "Dest1"), Times.Once);
        }

        [Fact]
        public async Task RouteAsync_ShouldThrow_WhenNoDestinations()
        {
            // Arrange
            var message = new FakeMessage1(Guid.NewGuid());

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => _bus.RouteAsync(message, new List<string>()));
        }

        [Fact]
        public void Constructor_ShouldThrow_WhenDependencyIsNull()
        {
            Assert.Throws<ArgumentNullException>(() => new Bus(null!, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, _processManagerRegistry, _messageHandlerRegistry, _streamHandlerRegistry, _aggregatorRegistry));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, null!, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, _processManagerRegistry, _messageHandlerRegistry, _streamHandlerRegistry, _aggregatorRegistry));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, null!, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, _processManagerRegistry, _messageHandlerRegistry, _streamHandlerRegistry, _aggregatorRegistry));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, null!, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, _processManagerRegistry, _messageHandlerRegistry, _streamHandlerRegistry, _aggregatorRegistry));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, null!, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, _processManagerRegistry, _messageHandlerRegistry, _streamHandlerRegistry, _aggregatorRegistry));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, null!, _mockDispatcher.Object, _handlerReferences, _processManagerRegistry, _messageHandlerRegistry, _streamHandlerRegistry, _aggregatorRegistry));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, (IMessageDispatcher)null!, _handlerReferences, _processManagerRegistry, _messageHandlerRegistry, _streamHandlerRegistry, _aggregatorRegistry));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, null!, _processManagerRegistry, _messageHandlerRegistry, _streamHandlerRegistry, _aggregatorRegistry));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, null!, _messageHandlerRegistry, _streamHandlerRegistry, _aggregatorRegistry));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, _processManagerRegistry, null!, _streamHandlerRegistry, _aggregatorRegistry));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, _processManagerRegistry, _messageHandlerRegistry, null!, _aggregatorRegistry));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, _processManagerRegistry, _messageHandlerRegistry, _streamHandlerRegistry, null!));
        }

        // --- Helper ---

        private Bus CreateBusWithConsumer(IConsumer consumer) =>
            new Bus(
                _mockSerializer.Object,
                _mockFilterPipeline.Object,
                _mockSendPipeline.Object,
                _mockRequestReplyManager.Object,
                _mockLogger.Object,
                _mockQueueConfig.Object,
                _mockDispatcher.Object,
                _handlerReferences,
                _processManagerRegistry,
                _messageHandlerRegistry,
                _streamHandlerRegistry,
                _aggregatorRegistry,
                consumer);

        // --- Lifecycle serialization tests (R-034) ---

        [Fact]
        public async Task StartConsumingAsync_ConcurrentWithStop_SerializesState()
        {
            var consumerStarted = new TaskCompletionSource();
            var releaseStart = new TaskCompletionSource();
            var mockConsumer = new Mock<IConsumer>();
            mockConsumer.Setup(c => c.StartConsumingAsync(It.IsAny<string>(), It.IsAny<IList<string>>(), It.IsAny<ConsumerEventHandler>()))
                .Returns(async () =>
                {
                    consumerStarted.SetResult();
                    await releaseStart.Task;
                });

            await using var bus = CreateBusWithConsumer(mockConsumer.Object);

            var startTask = bus.StartConsumingAsync();
            await consumerStarted.Task;
            var stopTask = bus.StopConsumingAsync();

            // Stop must not complete before Start releases the semaphore
            await Task.Delay(50);
            Assert.False(stopTask.IsCompleted);

            releaseStart.SetResult();
            await startTask;
            await stopTask;

            Assert.False(bus.IsConnected);
        }

        [Fact]
        public async Task StartConsumingAsync_PreCancelledToken_ThrowsOCE()
        {
            var mockConsumer = new Mock<IConsumer>();
            await using var bus = CreateBusWithConsumer(mockConsumer.Object);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => bus.StartConsumingAsync(cts.Token));
            mockConsumer.Verify(c => c.StartConsumingAsync(
                It.IsAny<string>(), It.IsAny<IList<string>>(), It.IsAny<ConsumerEventHandler>()),
                Times.Never);
        }

        [Fact]
        public async Task StopConsumingAsync_WhileStartInFlight_WaitsForStartToComplete()
        {
            var consumerStarted = new TaskCompletionSource();
            var releaseStart = new TaskCompletionSource();
            var startCompleted = new TaskCompletionSource();

            var mockConsumer = new Mock<IConsumer>();
            mockConsumer.Setup(c => c.StartConsumingAsync(It.IsAny<string>(), It.IsAny<IList<string>>(), It.IsAny<ConsumerEventHandler>()))
                .Returns(async () =>
                {
                    consumerStarted.SetResult();
                    await releaseStart.Task;
                });

            await using var bus = CreateBusWithConsumer(mockConsumer.Object);

            var startTask = Task.Run(async () =>
            {
                await bus.StartConsumingAsync();
                startCompleted.SetResult();
            });

            await consumerStarted.Task;

            // Fire Stop while Start is blocked inside the consumer call
            var stopTask = bus.StopConsumingAsync();

            // Stop is behind Start on the semaphore -- it cannot complete first
            var firstCompleted = await Task.WhenAny(stopTask, startCompleted.Task, Task.Delay(100));
            Assert.NotSame(stopTask, firstCompleted);

            // Release Start; both tasks complete cleanly
            releaseStart.SetResult();
            await startTask;
            await stopTask;
        }
    }
}
