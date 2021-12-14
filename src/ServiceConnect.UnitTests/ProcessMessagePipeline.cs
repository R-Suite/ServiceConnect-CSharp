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
    public class ProcessMessagePipelineTests
    {
        private readonly Mock<IConfiguration> _mockConfiguration;
        private readonly Mock<IBusContainer> _mockContainer;
        private readonly Mock<IMessageHandlerProcessor> _mockMessageHandlerProcessor;
        private readonly Mock<IProcessManagerProcessor> _mockProcessManagerProcessor;
        private readonly ConsumeContext _consumeContext;

        public ProcessMessagePipelineTests()
        {
            _mockConfiguration = new Mock<IConfiguration>();
            _mockContainer = new Mock<IBusContainer>();

            _mockMessageHandlerProcessor = new Mock<IMessageHandlerProcessor>();
            _mockProcessManagerProcessor = new Mock<IProcessManagerProcessor>();
            _mockMessageHandlerProcessor = new Mock<IMessageHandlerProcessor>();
            _mockConfiguration.Setup(x => x.GetContainer()).Returns(_mockContainer.Object);
            _mockContainer.Setup(x => x.GetInstance<IMessageHandlerProcessor>(It.IsAny<IDictionary<string, object>>())).Returns(_mockMessageHandlerProcessor.Object);
            _mockContainer.Setup(x => x.GetInstance<IProcessManagerProcessor>(It.IsAny<IDictionary<string, object>>())).Returns(_mockProcessManagerProcessor.Object);
            _mockContainer.Setup(x => x.GetInstance(typeof(Middleware1))).Returns(new Middleware1());
            _mockContainer.Setup(x => x.GetInstance(typeof(Middleware2))).Returns(new Middleware2());
            _middleware1BeforeExecuted = false;
            _middleware1AfterExecuted = false;
            _middleware2BeforeExecuted = false;
            _middleware2AfterExecuted = false;
            _consumeContext = new ConsumeContext
            {
                Bus = new Mock<IBus>().Object,
                Headers = new Dictionary<string, object>()
            };
            _mockProcessManagerProcessor.Setup(x => x.ProcessMessage<MiddlewareMessage>(It.IsAny<string>(), It.IsAny<IConsumeContext>())).Returns(Task.Run(() => { }));
            _mockMessageHandlerProcessor.Setup(x => x.ProcessMessage<MiddlewareMessage>(It.IsAny<string>(), It.IsAny<IConsumeContext>())).Returns(Task.Run(() => { }));
        }

        [Fact]
        public async Task ShouldExecuteMiddlewareWhenHandlingMessage()
        {
            _mockConfiguration.SetupGet(x => x.MessageProcessingMiddleware).Returns(new List<Type> {
                typeof(Middleware1),
                typeof(Middleware2)
            });

            var pipeline = new ProcessMessagePipeline(_mockConfiguration.Object, new BusState());
            await pipeline.ExecutePipeline(_consumeContext, typeof(MiddlewareMessage), new Envelope
            {
                Headers = new Dictionary<string, object>(),
                Body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new MiddlewareMessage(Guid.NewGuid())))
            });

            Assert.True(_middleware1BeforeExecuted);
            Assert.True(_middleware1AfterExecuted);
            Assert.True(_middleware2BeforeExecuted);

            Assert.True(_middleware2AfterExecuted);
            _mockProcessManagerProcessor.Verify(x => x.ProcessMessage<MiddlewareMessage>(It.IsAny<string>(), It.Is<IConsumeContext>(x => x == _consumeContext)), Times.Once);
            _mockMessageHandlerProcessor.Verify(x => x.ProcessMessage<MiddlewareMessage>(It.IsAny<string>(), It.Is<IConsumeContext>(x => x == _consumeContext)), Times.Once);
        }

        [Fact]
        public async Task ShouldExecuteHandlerWhenNoMiddlewareDefined()
        {
            _mockConfiguration.SetupGet(x => x.MessageProcessingMiddleware).Returns(new List<Type>());

            var pipeline = new ProcessMessagePipeline(_mockConfiguration.Object, new BusState());
            await pipeline.ExecutePipeline(_consumeContext, typeof(MiddlewareMessage), new Envelope
            {
                Headers = new Dictionary<string, object>(),
                Body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new MiddlewareMessage(Guid.NewGuid())))
            });


            Assert.False(_middleware1BeforeExecuted);
            Assert.False(_middleware1AfterExecuted);
            Assert.False(_middleware2BeforeExecuted);
            Assert.False(_middleware2AfterExecuted);

            _mockProcessManagerProcessor.Verify(x => x.ProcessMessage<MiddlewareMessage>(It.IsAny<string>(), It.Is<IConsumeContext>(x => x == _consumeContext)), Times.Once);
            _mockMessageHandlerProcessor.Verify(x => x.ProcessMessage<MiddlewareMessage>(It.IsAny<string>(), It.Is<IConsumeContext>(x => x == _consumeContext)), Times.Once);

        }

        [Fact]
        public async Task ShouldSendMessageToAggregatorProcessor()
        {
            _mockConfiguration.SetupGet(x => x.MessageProcessingMiddleware).Returns(new List<Type>());
                                                
            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim"
            };

            var processor = new Mock<IAggregatorProcessor>();
            processor.Setup(x => x.ProcessMessage<FakeMessage1>(JsonConvert.SerializeObject(message)));

            var busState = new BusState();
            busState.AggregatorProcessors[typeof(FakeMessage1)] = processor.Object;

            var pipeline = new ProcessMessagePipeline(_mockConfiguration.Object, busState);
            await pipeline.ExecutePipeline(_consumeContext, typeof(FakeMessage1), new Envelope
            {
                Headers = new Dictionary<string, object>(),
                Body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message))
            });

            processor.Verify(x => x.ProcessMessage<FakeMessage1>(It.Is<string>(y => JsonConvert.DeserializeObject<FakeMessage1>(y).Username == "Tim")), Times.Once);
        }


        private static bool _middleware1BeforeExecuted = false;
        private static bool _middleware1AfterExecuted = false;
        private static bool _middleware2BeforeExecuted = false;
        private static bool _middleware2AfterExecuted = false;

        public class Middleware1 : IProcessMessageMiddleware
        {
            public ProcessMessageDelegate Next { get; set; }
            public async Task Process(IConsumeContext context, Type typeObject, Envelope envelope)
            {
                _middleware1BeforeExecuted = true;
                await Next(context, typeObject, envelope);
                _middleware1AfterExecuted = true;
            }
        }

        public class Middleware2 : IProcessMessageMiddleware
        {
            public ProcessMessageDelegate Next { get; set; }

            public async Task Process(IConsumeContext context, Type typeObject, Envelope envelope)
            {
                _middleware2BeforeExecuted = true;
                await Next(context, typeObject, envelope);
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
