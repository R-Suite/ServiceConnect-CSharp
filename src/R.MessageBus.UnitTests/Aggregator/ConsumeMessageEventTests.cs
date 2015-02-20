using System;
using System.Collections.Generic;
using System.Text;
using Moq;
using Newtonsoft.Json;
using R.MessageBus.Interfaces;
using R.MessageBus.Settings;
using R.MessageBus.UnitTests.Fakes.Messages;
using Xunit;

namespace R.MessageBus.UnitTests.Aggregator
{
    public class ConsumeMessageEventTests
    {
        private Mock<IConfiguration> _mockConfiguration;
        private Mock<IBusContainer> _mockContainer;
        private Mock<IConsumer> _mockConsumer;
        private Mock<IProducer> _mockProducer;
        private ConsumerEventHandler _fakeEventHandler;

        public ConsumeMessageEventTests()
        {
            _mockConfiguration = new Mock<IConfiguration>();
            _mockContainer = new Mock<IBusContainer>();
            _mockConsumer = new Mock<IConsumer>();
            _mockProducer = new Mock<IProducer>();
            _mockConfiguration.Setup(x => x.GetContainer()).Returns(_mockContainer.Object);
            _mockConfiguration.Setup(x => x.GetConsumer()).Returns(_mockConsumer.Object);
            _mockConfiguration.Setup(x => x.GetProducer()).Returns(_mockProducer.Object);
            _mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings { Queue = new Queue { Name = "R.MessageBus.UnitTests" } });
        }

        public bool AssignEventHandler(ConsumerEventHandler eventHandler)
        {
            _fakeEventHandler = eventHandler;
            return true;
        }

        [Fact]
        public void ShouldSendMessageToAggregatorProcessor()
        {
            // Arrange
            
            var bus = new Bus(_mockConfiguration.Object);

            _mockConsumer.Setup(x => x.StartConsuming(It.Is<ConsumerEventHandler>(y => AssignEventHandler(y)), It.IsAny<string>(), null, null));
            var mockPersistor = new Mock<IAggregatorPersistor>();
            var mockProcessor = new Mock<IAggregatorProcessor>();

            _mockConfiguration.Setup(x => x.GetAggregatorPersistor()).Returns(mockPersistor.Object);
            _mockContainer.Setup(x => x.GetInstance<IAggregatorProcessor>(It.IsAny<Dictionary<string, object>>())).Returns(mockProcessor.Object);
            _mockContainer.Setup(x => x.GetInstance<IMessageHandlerProcessor>(It.IsAny<Dictionary<string, object>>())).Returns(new Mock<IMessageHandlerProcessor>().Object);
            _mockContainer.Setup(x => x.GetInstance<IProcessManagerProcessor>(It.IsAny<Dictionary<string, object>>())).Returns(new Mock<IProcessManagerProcessor>().Object);
            
            var message = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim"
            };

            mockProcessor.Setup(x => x.ProcessMessage<FakeMessage1>(JsonConvert.SerializeObject(message)));
                
            bus.StartConsuming();

            // Act
            _fakeEventHandler(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message)), typeof(FakeMessage1).AssemblyQualifiedName, new Dictionary<string, object>
            {
                { "MessageType", Encoding.UTF8.GetBytes("Send")}
            });

            // Assert
            mockProcessor.Verify(x => x.ProcessMessage<FakeMessage1>(It.Is<string>(y => JsonConvert.DeserializeObject<FakeMessage1>(y).Username == "Tim")), Times.Once);
        }
    }
}