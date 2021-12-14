using Moq;
using Newtonsoft.Json;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;
using ServiceConnect.UnitTests.Aggregator;
using ServiceConnect.UnitTests.Fakes.Messages;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace ServiceConnect.UnitTests
{
    public class SendMessagePipelineTests
    {
        private readonly Mock<IConfiguration> _mockConfiguration;
        private readonly Mock<IBusContainer> _mockContainer;
        private readonly Mock<IProducer> _mockProducer;

        public SendMessagePipelineTests()
        {
            _mockConfiguration = new Mock<IConfiguration>();
            _mockContainer = new Mock<IBusContainer>();
            _mockProducer = new Mock<IProducer>();

            _mockConfiguration.Setup(x => x.GetContainer()).Returns(_mockContainer.Object);
            _mockConfiguration.Setup(x => x.GetProducer()).Returns(_mockProducer.Object);

            _mockContainer.Setup(x => x.GetInstance(typeof(Middleware1))).Returns(new Middleware1());
            _mockContainer.Setup(x => x.GetInstance(typeof(Middleware2))).Returns(new Middleware2());

            _middleware1BeforeExecuted = false;
            _middleware1AfterExecuted = false;
            _middleware2BeforeExecuted = false;
            _middleware2AfterExecuted = false;
        }

        [Fact]
        public void ShouldExecuteMiddlewareWhenSendingMessageWithEndpoint()
        {
            _mockConfiguration.SetupGet(x => x.SendMessageMiddleware).Returns(new List<Type> {
                typeof(Middleware1),
                typeof(Middleware2)
            });

            var pipeline = new SendMessagePipeline(_mockConfiguration.Object);
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new MiddlewareMessage(Guid.NewGuid())));
            var headers = new Dictionary<string, string>();
            pipeline.ExecuteSendMessagePipeline(typeof(MiddlewareMessage), bytes, headers, "Test");

            Assert.True(_middleware1BeforeExecuted);
            Assert.True(_middleware1AfterExecuted);
            Assert.True(_middleware2BeforeExecuted);
            Assert.True(_middleware2AfterExecuted);

            _mockProducer.Verify(x => x.Send("Test", typeof(MiddlewareMessage), bytes, headers), Times.Once);
        }

        [Fact]
        public void ShouldExecuteMiddlewareWhenSendingMessageWithoutEndpoint()
        {
            _mockConfiguration.SetupGet(x => x.SendMessageMiddleware).Returns(new List<Type> {
                typeof(Middleware1),
                typeof(Middleware2)
            });

            var pipeline = new SendMessagePipeline(_mockConfiguration.Object);
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new MiddlewareMessage(Guid.NewGuid())));
            pipeline.ExecuteSendMessagePipeline(typeof(MiddlewareMessage), bytes);

            Assert.True(_middleware1BeforeExecuted);
            Assert.True(_middleware1AfterExecuted);
            Assert.True(_middleware2BeforeExecuted);
            Assert.True(_middleware2AfterExecuted);

            _mockProducer.Verify(x => x.Send(typeof(MiddlewareMessage), bytes, null), Times.Once);
        }

        [Fact]
        public void ShouldExecuteMiddlewareWhenPublishingMessage()
        {
            _mockConfiguration.SetupGet(x => x.SendMessageMiddleware).Returns(new List<Type> {
                typeof(Middleware1),
                typeof(Middleware2)
            });

            var pipeline = new SendMessagePipeline(_mockConfiguration.Object);
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new MiddlewareMessage(Guid.NewGuid())));
            pipeline.ExecutePublishMessagePipeline(typeof(MiddlewareMessage), bytes);

            Assert.True(_middleware1BeforeExecuted);
            Assert.True(_middleware1AfterExecuted);
            Assert.True(_middleware2BeforeExecuted);
            Assert.True(_middleware2AfterExecuted);

            _mockProducer.Verify(x => x.Publish(typeof(MiddlewareMessage), bytes, null), Times.Once);
        }

        [Fact]
        public void ShouldSendMessageWhenNoMiddlewareDefined()
        {
            _mockConfiguration.SetupGet(x => x.SendMessageMiddleware).Returns(new List<Type>());

            var pipeline = new SendMessagePipeline(_mockConfiguration.Object);
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new MiddlewareMessage(Guid.NewGuid())));
            pipeline.ExecutePublishMessagePipeline(typeof(MiddlewareMessage), bytes);

            Assert.False(_middleware1BeforeExecuted);
            Assert.False(_middleware1AfterExecuted);
            Assert.False(_middleware2BeforeExecuted);
            Assert.False(_middleware2AfterExecuted);

            _mockProducer.Verify(x => x.Publish(typeof(MiddlewareMessage), bytes, null), Times.Once);
        }        

        private static bool _middleware1BeforeExecuted = false;
        private static bool _middleware1AfterExecuted = false;
        private static bool _middleware2BeforeExecuted = false;
        private static bool _middleware2AfterExecuted = false;


        public class Middleware1 : ISendMessageMiddleware
        {
            public SendMessageDelegate Next { get; set; }

            public void Process(Type typeObject, byte[] messageBytes, Dictionary<string, string> headers = null, string endPoint = null)
            {
                _middleware1BeforeExecuted = true;
                Next(typeObject, messageBytes, headers, endPoint); 
                _middleware1AfterExecuted = true;
            }
        }

        public class Middleware2 : ISendMessageMiddleware
        {
            public SendMessageDelegate Next { get; set; }

            public void Process(Type typeObject, byte[] messageBytes, Dictionary<string, string> headers = null, string endPoint = null)
            {
                _middleware2BeforeExecuted = true;
                Next(typeObject, messageBytes, headers, endPoint);
                _middleware2AfterExecuted = true;
            }
        }


        public class MiddlewareMessage : Message
        {
            public MiddlewareMessage(Guid correlationId) : base(correlationId)
            {
            }
        }
    }
}
