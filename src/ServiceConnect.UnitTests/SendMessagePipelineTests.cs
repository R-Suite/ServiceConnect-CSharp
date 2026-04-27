using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests
{
    public class SendMessagePipelineTests
    {
        private readonly Mock<IProducer> _mockProducer;
        private readonly Mock<IPipelineConfiguration> _mockPipelineConfig;
        private readonly ServiceProvider _serviceProvider;

        public SendMessagePipelineTests()
        {
            _mockProducer = new Mock<IProducer>();
            _mockProducer.Setup(p => p.PublishAsync(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<IDictionary<string, string>>()))
                .Returns(Task.CompletedTask);
            _mockProducer.Setup(p => p.SendAsync(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<IDictionary<string, string>>()))
                .Returns(Task.CompletedTask);
            _mockProducer.Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<IDictionary<string, string>>()))
                .Returns(Task.CompletedTask);

            _mockPipelineConfig = new Mock<IPipelineConfiguration>();
            _mockPipelineConfig.Setup(p => p.SendMessageMiddleware).Returns([]);
            _serviceProvider = new ServiceCollection().BuildServiceProvider();
        }

        private SendMessagePipeline CreatePipeline()
        {
            return new SendMessagePipeline(_mockProducer.Object, _mockPipelineConfig.Object, _serviceProvider);
        }

        private static SendContext MakePublishContext(Type type, byte[] bytes, IDictionary<string, string>? headers = null) => new()
        {
            Message = new TestSendPipelineMessage(),
            MessageType = type,
            MessageBytes = bytes,
            Headers = headers ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Operation = SendOperation.Publish,
        };

        private static SendContext MakeSendContext(Type type, byte[] bytes, IDictionary<string, string>? headers = null, string? endPoint = null) => new()
        {
            Message = new TestSendPipelineMessage(),
            MessageType = type,
            MessageBytes = bytes,
            Headers = headers ?? new Dictionary<string, string>(StringComparer.Ordinal),
            EndPoint = endPoint,
            Operation = SendOperation.Send,
        };

        [Fact]
        public void Constructor_ThrowsWhenProducerIsNull()
        {
            Assert.Throws<ArgumentNullException>(() => new SendMessagePipeline(null!, _mockPipelineConfig.Object, _serviceProvider));
        }

        [Fact]
        public async Task ExecutePublishMessagePipelineAsync_CallsProducerPublishAsync()
        {
            var pipeline = CreatePipeline();
            var type = typeof(string);
            var bytes = new byte[] { 1, 2, 3 };
            var headers = new Dictionary<string, string> { ["key"] = "value" };

            await pipeline.ExecutePublishMessagePipelineAsync(MakePublishContext(type, bytes, headers));

            _mockProducer.Verify(p => p.PublishAsync(type, bytes, headers), Times.Once);
        }

        [Fact]
        public async Task ExecuteSendMessagePipelineAsync_WithEndPoint_CallsSendAsyncWithEndPoint()
        {
            var pipeline = CreatePipeline();
            var type = typeof(string);
            var bytes = new byte[] { 1, 2, 3 };
            var headers = new Dictionary<string, string>();
            const string endPoint = "my-queue";

            await pipeline.ExecuteSendMessagePipelineAsync(MakeSendContext(type, bytes, headers, endPoint));

            _mockProducer.Verify(p => p.SendAsync(endPoint, type, bytes, headers), Times.Once);
            _mockProducer.Verify(p => p.SendAsync(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<IDictionary<string, string>>()), Times.Never);
        }

        [Fact]
        public async Task ExecuteSendMessagePipelineAsync_WithoutEndPoint_CallsSendAsyncWithoutEndPoint()
        {
            var pipeline = CreatePipeline();
            var type = typeof(string);
            var bytes = new byte[] { 1, 2, 3 };

            await pipeline.ExecuteSendMessagePipelineAsync(MakeSendContext(type, bytes));

            _mockProducer.Verify(p => p.SendAsync(type, bytes, It.IsAny<IDictionary<string, string>>()), Times.Once);
            _mockProducer.Verify(p => p.SendAsync(It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<IDictionary<string, string>>()), Times.Never);
        }

        [Fact]
        public async Task ExecuteSendMessagePipelineAsync_WithEmptyEndPoint_CallsSendAsyncWithoutEndPoint()
        {
            var pipeline = CreatePipeline();
            var type = typeof(string);
            var bytes = new byte[] { 1, 2, 3 };

            await pipeline.ExecuteSendMessagePipelineAsync(MakeSendContext(type, bytes, endPoint: string.Empty));

            _mockProducer.Verify(p => p.SendAsync(type, bytes, It.IsAny<IDictionary<string, string>>()), Times.Once);
            _mockProducer.Verify(p => p.SendAsync(It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<IDictionary<string, string>>()), Times.Never);
        }

        [Fact]
        public async Task DisposeAsync_DoesNotDisposeProducer_ProducerLifetimeManagedByContainer()
        {
            var pipeline = CreatePipeline();

            await pipeline.DisposeAsync();

            _mockProducer.Verify(p => p.DisposeAsync(), Times.Never);
        }

        [Fact]
        public async Task DisposeAsync_CalledTwice_IsIdempotent()
        {
            var pipeline = CreatePipeline();

            await pipeline.DisposeAsync();
            await pipeline.DisposeAsync();

            _mockProducer.Verify(p => p.DisposeAsync(), Times.Never);
        }

        // Outgoing-filter short-circuit via ISendMessageMiddleware
        [Fact]
        public async Task ExecuteSendMessagePipelineAsync_WhenMiddlewareShortCircuits_ProducerSendIsNeverCalled()
        {
            // A middleware that does NOT call next short-circuits the pipeline.
            // IProducer.SendAsync / PublishAsync must never be invoked.
            var services = new ServiceCollection();
            services.AddTransient<BlockingSendMiddleware>();
            var sp = services.BuildServiceProvider();

            var mockConfig = new Mock<IPipelineConfiguration>();
            mockConfig.Setup(c => c.SendMessageMiddleware)
                .Returns([typeof(BlockingSendMiddleware)]);

            var pipeline = new SendMessagePipeline(_mockProducer.Object, mockConfig.Object, sp);

            await pipeline.ExecuteSendMessagePipelineAsync(MakeSendContext(typeof(string), [1, 2, 3]));

            _mockProducer.Verify(
                p => p.SendAsync(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<IDictionary<string, string>>()),
                Times.Never);
            _mockProducer.Verify(
                p => p.SendAsync(It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<IDictionary<string, string>>()),
                Times.Never);
            _mockProducer.Verify(
                p => p.PublishAsync(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<IDictionary<string, string>>()),
                Times.Never);
        }
    }
}

file sealed class TestSendPipelineMessage : Message
{
    public TestSendPipelineMessage() : base(Guid.NewGuid()) { }
}

file sealed class BlockingSendMiddleware : ISendMessageMiddleware
{
    // Intentionally does NOT call next — short-circuits the pipeline.
    public Task ProcessAsync(SendContext context, SendMessageDelegate next, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
