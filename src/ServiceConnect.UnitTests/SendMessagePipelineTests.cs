using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests
{
    public class SendMessagePipelineTests
    {
        private readonly Mock<IProducer> _mockProducer;

        public SendMessagePipelineTests()
        {
            _mockProducer = new Mock<IProducer>();
            _mockProducer.Setup(p => p.PublishAsync(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>()))
                .Returns(Task.CompletedTask);
            _mockProducer.Setup(p => p.SendAsync(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>()))
                .Returns(Task.CompletedTask);
            _mockProducer.Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>()))
                .Returns(Task.CompletedTask);
        }

        [Fact]
        public void Constructor_ThrowsWhenProducerIsNull()
        {
            Assert.Throws<ArgumentNullException>(() => new SendMessagePipeline(null!));
        }

        [Fact]
        public async Task ExecutePublishMessagePipelineAsync_CallsProducerPublishAsync()
        {
            var pipeline = new SendMessagePipeline(_mockProducer.Object);
            var type = typeof(string);
            var bytes = new byte[] { 1, 2, 3 };
            var headers = new Dictionary<string, string> { ["key"] = "value" };

            await pipeline.ExecutePublishMessagePipelineAsync(type, bytes, headers);

            _mockProducer.Verify(p => p.PublishAsync(type, bytes, headers), Times.Once);
        }

        [Fact]
        public async Task ExecuteSendMessagePipelineAsync_WithEndPoint_CallsSendAsyncWithEndPoint()
        {
            var pipeline = new SendMessagePipeline(_mockProducer.Object);
            var type = typeof(string);
            var bytes = new byte[] { 1, 2, 3 };
            var headers = new Dictionary<string, string>();
            const string endPoint = "my-queue";

            await pipeline.ExecuteSendMessagePipelineAsync(type, bytes, headers, endPoint);

            _mockProducer.Verify(p => p.SendAsync(endPoint, type, bytes, headers), Times.Once);
            _mockProducer.Verify(p => p.SendAsync(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>()), Times.Never);
        }

        [Fact]
        public async Task ExecuteSendMessagePipelineAsync_WithoutEndPoint_CallsSendAsyncWithoutEndPoint()
        {
            var pipeline = new SendMessagePipeline(_mockProducer.Object);
            var type = typeof(string);
            var bytes = new byte[] { 1, 2, 3 };

            await pipeline.ExecuteSendMessagePipelineAsync(type, bytes);

            _mockProducer.Verify(p => p.SendAsync(type, bytes, null), Times.Once);
            _mockProducer.Verify(p => p.SendAsync(It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>()), Times.Never);
        }

        [Fact]
        public async Task ExecuteSendMessagePipelineAsync_WithEmptyEndPoint_CallsSendAsyncWithoutEndPoint()
        {
            var pipeline = new SendMessagePipeline(_mockProducer.Object);
            var type = typeof(string);
            var bytes = new byte[] { 1, 2, 3 };

            await pipeline.ExecuteSendMessagePipelineAsync(type, bytes, endPoint: string.Empty);

            _mockProducer.Verify(p => p.SendAsync(type, bytes, null), Times.Once);
            _mockProducer.Verify(p => p.SendAsync(It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>()), Times.Never);
        }

        [Fact]
        public void Dispose_DisposesProducer()
        {
            var pipeline = new SendMessagePipeline(_mockProducer.Object);

            pipeline.Dispose();

            _mockProducer.Verify(p => p.Dispose(), Times.Once);
        }

        [Fact]
        public void Dispose_CalledTwice_DisposesProducerOnlyOnce()
        {
            var pipeline = new SendMessagePipeline(_mockProducer.Object);

            pipeline.Dispose();
            pipeline.Dispose();

            _mockProducer.Verify(p => p.Dispose(), Times.Once);
        }
    }
}
