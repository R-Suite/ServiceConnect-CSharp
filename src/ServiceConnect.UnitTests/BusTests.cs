using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;
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
        private readonly Mock<IPipelineConfiguration> _mockPipelineConfig;
        private readonly Mock<ILogger<Bus>> _mockLogger;
        private readonly Mock<IQueueConfiguration> _mockQueueConfig;
        private readonly Mock<IMessageDispatcher> _mockDispatcher;
        private readonly IList<HandlerReference> _handlerReferences;
        private readonly Bus _bus;

        public BusTests()
        {
            _mockSerializer = new Mock<IMessageSerializer>();
            _mockFilterPipeline = new Mock<IFilterPipeline>();
            _mockSendPipeline = new Mock<ISendMessagePipeline>();
            _mockRequestReplyManager = new Mock<IRequestReplyManager>();
            _mockConfig = new Mock<IBusConfiguration>();
            _mockPipelineConfig = new Mock<IPipelineConfiguration>();
            // Default: no outgoing filters registered — Bus takes the fast path
            _mockPipelineConfig.Setup(x => x.OutgoingFilters).Returns(new List<Type>());
            _mockLogger = new Mock<ILogger<Bus>>();
            _mockQueueConfig = new Mock<IQueueConfiguration>();
            _mockQueueConfig.Setup(x => x.QueueName).Returns("test-queue");

            // Default: filters pass through (false = not stopped)
            _mockFilterPipeline.Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
            _mockSerializer.Setup(x => x.Serialize(It.IsAny<FakeMessage1>())).Returns(new byte[] { 1, 2, 3 });

            _mockDispatcher = new Mock<IMessageDispatcher>();
            _handlerReferences = new List<HandlerReference>();

            _bus = new Bus(
                _mockSerializer.Object,
                _mockFilterPipeline.Object,
                _mockSendPipeline.Object,
                _mockRequestReplyManager.Object,
                _mockLogger.Object,
                _mockQueueConfig.Object,
                _mockDispatcher.Object,
                _handlerReferences,
                _mockPipelineConfig.Object);
        }

        [Fact]
        public void IsConsuming_ShouldBeFalse_WhenNotConsuming()
        {
            Assert.False(_bus.IsConsuming);
        }

        [Fact]
        public async Task StartConsumingAsync_ShouldThrow_WhenNoConsumerRegistered()
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => _bus.StartConsumingAsync());
        }

        [Fact]
        public async Task StartConsumingAsync_ShouldSetIsConsumingToTrue_WhenConsumerRegistered()
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
                _mockPipelineConfig.Object,
                mockConsumer.Object);

            // Act
            await bus.StartConsumingAsync();

            // Assert
            Assert.True(bus.IsConsuming);
        }

        [Fact]
        public async Task StopConsumingAsync_ShouldSetIsConsumingToFalse()
        {
            // StopConsuming can be called even without starting (no consumer needed)
            await _bus.StopConsumingAsync();
            Assert.False(_bus.IsConsuming);
        }

        [Fact]
        public async Task DisposeAsync_ShouldSetIsConsumingToFalse()
        {
            await _bus.DisposeAsync();
            Assert.False(_bus.IsConsuming);
        }

        [Fact]
        public async Task DisposeAsync_CompletesWhenConsumerDisposeStalls()
        {
            var releaseDispose = new TaskCompletionSource();
            var mockConsumer = new Mock<IConsumer>();
            mockConsumer
                .Setup(x => x.StartConsumingAsync(It.IsAny<string>(), It.IsAny<IList<string>>(), It.IsAny<ConsumerEventHandler>()))
                .Returns(Task.CompletedTask);
            mockConsumer
                .Setup(x => x.DisposeAsync())
                .Returns(new ValueTask(releaseDispose.Task));

            var bus = new Bus(
                _mockSerializer.Object,
                _mockFilterPipeline.Object,
                _mockSendPipeline.Object,
                _mockRequestReplyManager.Object,
                _mockLogger.Object,
                _mockQueueConfig.Object,
                _mockDispatcher.Object,
                _handlerReferences,
                _mockPipelineConfig.Object,
                mockConsumer.Object,
                null,
                TimeSpan.FromMilliseconds(50));

            await bus.StartConsumingAsync();

            var disposeTask = bus.DisposeAsync().AsTask();
            await Task.WhenAny(disposeTask, Task.Delay(500));

            Assert.True(disposeTask.IsCompleted);
        }

        [Fact]
        public async Task PublishAsync_ShouldSerializeAndPublish()
        {
            // Arrange — no outgoing filters (fast path; filter pipeline is not called)
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
            _mockFilterPipeline.Verify(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockSendPipeline.Verify(x => x.ExecutePublishMessagePipelineAsync(
                typeof(FakeMessage1), messageBytes, It.IsAny<Dictionary<string, string>>(), null), Times.Once);
        }

        [Fact]
        public async Task PublishAsync_WithOutgoingFilters_ShouldExecuteFilterPipeline()
        {
            // Arrange — bus created with outgoing filters registered; filter pipeline must be invoked
            var pipelineConfigWithFilter = new Mock<IPipelineConfiguration>();
            pipelineConfigWithFilter.Setup(x => x.OutgoingFilters).Returns(new List<Type> { typeof(object) });
            var busWithFilters = new Bus(
                _mockSerializer.Object,
                _mockFilterPipeline.Object,
                _mockSendPipeline.Object,
                _mockRequestReplyManager.Object,
                _mockLogger.Object,
                _mockQueueConfig.Object,
                _mockDispatcher.Object,
                _handlerReferences,
                pipelineConfigWithFilter.Object);

            var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
            var messageBytes = new byte[] { 1, 2, 3 };
            _mockSerializer.Setup(x => x.Serialize(message)).Returns(messageBytes);
            _mockSendPipeline.Setup(x => x.ExecutePublishMessagePipelineAsync(
                typeof(FakeMessage1), messageBytes, It.IsAny<Dictionary<string, string>>(), null))
                .Returns(Task.CompletedTask);

            // Act
            await busWithFilters.PublishAsync(message);

            // Assert
            _mockFilterPipeline.Verify(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()), Times.Once);
            _mockSendPipeline.Verify(x => x.ExecutePublishMessagePipelineAsync(
                typeof(FakeMessage1), messageBytes, It.IsAny<Dictionary<string, string>>(), null), Times.Once);
        }

        [Fact]
        public async Task PublishAsync_ShouldNotPublish_WhenFilterBlocksMessage()
        {
            // Arrange — must have outgoing filters registered so the filter pipeline is invoked
            _mockFilterPipeline.Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            var pipelineConfigWithFilter = new Mock<IPipelineConfiguration>();
            pipelineConfigWithFilter.Setup(x => x.OutgoingFilters).Returns(new List<Type> { typeof(object) });
            var busWithFilters = new Bus(
                _mockSerializer.Object,
                _mockFilterPipeline.Object,
                _mockSendPipeline.Object,
                _mockRequestReplyManager.Object,
                _mockLogger.Object,
                _mockQueueConfig.Object,
                _mockDispatcher.Object,
                _handlerReferences,
                pipelineConfigWithFilter.Object);
            var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
            _mockSerializer.Setup(x => x.Serialize(message)).Returns(new byte[] { 1, 2, 3 });

            // Act
            await busWithFilters.PublishAsync(message);

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
        public async Task PublishAsync_FastPath_StampsCorrelationIdHeader()
        {
            var correlationId = Guid.NewGuid();
            var message = new FakeMessage1(correlationId) { Username = "Tim" };

            await _bus.PublishAsync(message);

            _mockSendPipeline.Verify(x => x.ExecutePublishMessagePipelineAsync(
                typeof(FakeMessage1),
                It.IsAny<byte[]>(),
                It.Is<Dictionary<string, string>>(h =>
                    h.ContainsKey(HeaderKeys.CorrelationId) &&
                    h[HeaderKeys.CorrelationId] == correlationId.ToString()),
                null), Times.Once);
        }

        [Fact]
        public async Task PublishAsync_FilterPath_StampsCorrelationIdHeader()
        {
            var correlationId = Guid.NewGuid();
            var pipelineConfigWithFilter = new Mock<IPipelineConfiguration>();
            pipelineConfigWithFilter.Setup(x => x.OutgoingFilters).Returns(new List<Type> { typeof(object) });
            var busWithFilters = new Bus(
                _mockSerializer.Object,
                _mockFilterPipeline.Object,
                _mockSendPipeline.Object,
                _mockRequestReplyManager.Object,
                _mockLogger.Object,
                _mockQueueConfig.Object,
                _mockDispatcher.Object,
                _handlerReferences,
                pipelineConfigWithFilter.Object);
            var message = new FakeMessage1(correlationId) { Username = "Tim" };

            await busWithFilters.PublishAsync(message);

            _mockSendPipeline.Verify(x => x.ExecutePublishMessagePipelineAsync(
                typeof(FakeMessage1),
                It.IsAny<byte[]>(),
                It.Is<Dictionary<string, string>>(h =>
                    h.ContainsKey(HeaderKeys.CorrelationId) &&
                    h[HeaderKeys.CorrelationId] == correlationId.ToString()),
                null), Times.Once);
        }

        [Fact]
        public async Task SendAsync_StampsCorrelationIdHeader()
        {
            var correlationId = Guid.NewGuid();
            var message = new FakeMessage1(correlationId) { Username = "Tim" };

            await _bus.SendAsync(message);

            _mockSendPipeline.Verify(x => x.ExecuteSendMessagePipelineAsync(
                typeof(FakeMessage1),
                It.IsAny<byte[]>(),
                It.Is<Dictionary<string, string>>(h =>
                    h.ContainsKey(HeaderKeys.CorrelationId) &&
                    h[HeaderKeys.CorrelationId] == correlationId.ToString()),
                null), Times.Once);
        }

        [Fact]
        public async Task SendRequestAsync_StampsCorrelationIdHeader()
        {
            var correlationId = Guid.NewGuid();
            var message = new FakeMessage1(correlationId) { Username = "Tim" };
            _mockRequestReplyManager.Setup(x => x.SendRequestAsync<FakeMessage1, FakeMessage1>(
                    It.IsAny<byte[]>(),
                    It.IsAny<Dictionary<string, string>>(),
                    It.IsAny<RequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(message);

            await _bus.SendRequestAsync<FakeMessage1, FakeMessage1>(message);

            _mockRequestReplyManager.Verify(x => x.SendRequestAsync<FakeMessage1, FakeMessage1>(
                It.IsAny<byte[]>(),
                It.Is<Dictionary<string, string>>(h =>
                    h.ContainsKey(HeaderKeys.CorrelationId) &&
                    h[HeaderKeys.CorrelationId] == correlationId.ToString()),
                It.IsAny<RequestOptions>(),
                It.IsAny<CancellationToken>()), Times.Once);
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
            // Arrange — must have outgoing filters registered so the filter pipeline is invoked
            _mockFilterPipeline.Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            var pipelineConfigWithFilter = new Mock<IPipelineConfiguration>();
            pipelineConfigWithFilter.Setup(x => x.OutgoingFilters).Returns(new List<Type> { typeof(object) });
            var busWithFilters = new Bus(
                _mockSerializer.Object,
                _mockFilterPipeline.Object,
                _mockSendPipeline.Object,
                _mockRequestReplyManager.Object,
                _mockLogger.Object,
                _mockQueueConfig.Object,
                _mockDispatcher.Object,
                _handlerReferences,
                pipelineConfigWithFilter.Object);
            var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
            _mockSerializer.Setup(x => x.Serialize(message)).Returns(new byte[] { 1, 2, 3 });

            // Act
            await busWithFilters.SendAsync(message);

            // Assert
            _mockSendPipeline.Verify(x => x.ExecuteSendMessagePipelineAsync(
                It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<string>()),
                Times.Never);
        }

        [Fact]
        public async Task PublishRequestAsync_DelegatesToRequestReplyManagerPublishMethod()
        {
            var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
            var messageBytes = new byte[] { 1, 2, 3 };
            var options = new RequestOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["CustomHeader"] = "CustomValue"
                }
            };

            _mockSerializer.Setup(x => x.Serialize(message)).Returns(messageBytes);
            _mockRequestReplyManager.Setup(x => x.PublishRequestAsync<FakeMessage1, FakeMessage1>(
                    messageBytes,
                    It.Is<Dictionary<string, string>>(h => h.ContainsKey("CustomHeader") && h["CustomHeader"] == "CustomValue"),
                    options,
                    It.IsAny<Action<FakeMessage1>>(),
                    CancellationToken.None))
                .Returns(Task.CompletedTask);

            await _bus.PublishRequestAsync<FakeMessage1, FakeMessage1>(message, _ => { }, options);

            _mockRequestReplyManager.Verify(x => x.PublishRequestAsync<FakeMessage1, FakeMessage1>(
                    messageBytes,
                    It.Is<Dictionary<string, string>>(h => h.ContainsKey("CustomHeader") && h["CustomHeader"] == "CustomValue"),
                    options,
                    It.IsAny<Action<FakeMessage1>>(),
                    CancellationToken.None),
                Times.Once);
            _mockRequestReplyManager.Verify(x => x.SendRequestMultiAsync<FakeMessage1, FakeMessage1>(
                    It.IsAny<byte[]>(),
                    It.IsAny<Dictionary<string, string>>(),
                    It.IsAny<RequestOptions>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task PublishRequestAsync_WithEndPoint_ThrowsArgumentException()
        {
            var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
            var options = new RequestOptions { EndPoint = "MyEndPoint" };

            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => _bus.PublishRequestAsync<FakeMessage1, FakeMessage1>(message, _ => { }, options));

            Assert.Equal("options", ex.ParamName);
        }

        [Fact]
        public async Task PublishRequestAsync_WithEmptyEndPoint_DelegatesToRequestReplyManagerPublishMethod()
        {
            var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
            var messageBytes = new byte[] { 1, 2, 3 };
            var options = new RequestOptions { EndPoint = string.Empty };

            _mockSerializer.Setup(x => x.Serialize(message)).Returns(messageBytes);
            _mockRequestReplyManager.Setup(x => x.PublishRequestAsync<FakeMessage1, FakeMessage1>(
                    messageBytes,
                    It.IsAny<Dictionary<string, string>>(),
                    options,
                    It.IsAny<Action<FakeMessage1>>(),
                    CancellationToken.None))
                .Returns(Task.CompletedTask);

            await _bus.PublishRequestAsync<FakeMessage1, FakeMessage1>(message, _ => { }, options);

            _mockRequestReplyManager.Verify(x => x.PublishRequestAsync<FakeMessage1, FakeMessage1>(
                    messageBytes,
                    It.IsAny<Dictionary<string, string>>(),
                    options,
                    It.IsAny<Action<FakeMessage1>>(),
                    CancellationToken.None),
                Times.Once);
        }

        [Fact]
        public async Task PublishRequestAsync_WithEndPoints_ThrowsArgumentException()
        {
            var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
            var options = new RequestOptions { EndPoints = new List<string> { "EP1", "EP2" } };

            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => _bus.PublishRequestAsync<FakeMessage1, FakeMessage1>(message, _ => { }, options));

            Assert.Equal("options", ex.ParamName);
        }

        [Fact]
        public async Task PublishRequestAsync_WhenFilterBlocksMessage_ThrowsInvalidOperationException()
        {
            _mockFilterPipeline
                .Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var pipelineConfigWithFilter = new Mock<IPipelineConfiguration>();
            pipelineConfigWithFilter.Setup(x => x.OutgoingFilters).Returns(new List<Type> { typeof(object) });

            var busWithFilters = new Bus(
                _mockSerializer.Object,
                _mockFilterPipeline.Object,
                _mockSendPipeline.Object,
                _mockRequestReplyManager.Object,
                _mockLogger.Object,
                _mockQueueConfig.Object,
                _mockDispatcher.Object,
                _handlerReferences,
                pipelineConfigWithFilter.Object);

            var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => busWithFilters.PublishRequestAsync<FakeMessage1, FakeMessage1>(message, _ => { }));
        }

        [Fact]
        public async Task RequestTimeoutAsync_WithAmbientConsumeHeaders_PreservesCustomHeadersOnly()
        {
            TimeoutData? captured = null;
            var timeoutStore = new Mock<ITimeoutStore>();
            timeoutStore
                .Setup(x => x.InsertTimeoutAsync(It.IsAny<TimeoutData>(), It.IsAny<CancellationToken>()))
                .Callback<TimeoutData, CancellationToken>((data, _) => captured = data)
                .Returns(Task.CompletedTask);

            var incomingHeaders = new Dictionary<string, object>
            {
                ["Custom"] = "value",
                [HeaderKeys.RetryCount] = 3,
                [HeaderKeys.MessageId] = "managed-message-id",
                [HeaderKeys.SourceAddress] = "reply-queue"
            };

            var accessor = CreateConsumeContextAccessorOrFail();
            await using var bus = CreateBusWithTimeoutStoreAndAccessorOrFail(timeoutStore.Object, accessor);
            using var scope = PushConsumeContextOrFail(accessor, incomingHeaders);

            await bus.RequestTimeoutAsync(Guid.NewGuid(), TimeSpan.FromMinutes(1));

            Assert.NotNull(captured);
            Assert.Equal("test-queue", captured!.Destination);
            Assert.Equal("value", captured.Headers["Custom"]);
            Assert.Equal(3, captured.Headers[HeaderKeys.RetryCount]);
            Assert.False(captured.Headers.ContainsKey(HeaderKeys.MessageId));
            Assert.False(captured.Headers.ContainsKey(HeaderKeys.SourceAddress));
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
            var p = _mockPipelineConfig.Object;
            Assert.Throws<ArgumentNullException>(() => new Bus(null!, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, p));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, null!, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, p));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, null!, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, p));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, null!, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, p));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, null!, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, p));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, null!, _mockDispatcher.Object, _handlerReferences, p));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, (IMessageDispatcher)null!, _handlerReferences, p));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, null!, p));
            Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, null!));
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
                _mockPipelineConfig.Object,
                consumer);

        // Lifecycle serialization tests.

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

            Assert.False(bus.IsConsuming);
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

        private object CreateConsumeContextAccessorOrFail()
        {
            var accessorType = typeof(Bus).Assembly.GetType("ServiceConnect.Services.ConsumeContextAccessor");
            Assert.NotNull(accessorType);

            var accessor = Activator.CreateInstance(accessorType!);
            Assert.NotNull(accessor);
            return accessor!;
        }

        private static IDisposable PushConsumeContextOrFail(object accessor, IReadOnlyDictionary<string, object> headers)
        {
            var pushMethod = accessor.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SingleOrDefault(m => m.Name == "Push" && m.GetParameters().Length == 1);

            Assert.NotNull(pushMethod);

            var scope = pushMethod!.Invoke(accessor, [headers]);
            Assert.IsAssignableFrom<IDisposable>(scope);
            return (IDisposable)scope!;
        }

        private Bus CreateBusWithTimeoutStoreAndAccessorOrFail(ITimeoutStore timeoutStore, object accessor)
        {
            var constructor = typeof(Bus)
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SingleOrDefault(ctor => ctor.GetParameters().Any(p => p.ParameterType == accessor.GetType()));

            Assert.NotNull(constructor);

            var args = constructor!.GetParameters().Select(parameter => parameter.Name switch
            {
                "serializer" => _mockSerializer.Object,
                "filterPipeline" => _mockFilterPipeline.Object,
                "sendPipeline" => _mockSendPipeline.Object,
                "requestReplyManager" => _mockRequestReplyManager.Object,
                "logger" => _mockLogger.Object,
                "queueConfig" => _mockQueueConfig.Object,
                "dispatcher" => _mockDispatcher.Object,
                "handlerReferences" => _handlerReferences,
                "pipelineConfig" => _mockPipelineConfig.Object,
                "consumer" => null,
                "producer" => null,
                "disposeTimeout" => null,
                "timeoutStore" => timeoutStore,
                "consumeContextAccessor" => accessor,
                _ => throw new InvalidOperationException($"Unexpected Bus constructor parameter '{parameter.Name}'.")
            }).ToArray();

            return (Bus)constructor.Invoke(args);
        }
    }
}
