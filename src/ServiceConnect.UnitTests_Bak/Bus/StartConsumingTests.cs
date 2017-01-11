using System.Collections.Generic;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.UnitTests.Fakes.Handlers;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests.Bus
{
    public class StartConsumingTests
    {
        private readonly Mock<IConfiguration> _mockConfiguration;
        private readonly Mock<IBusContainer> _mockContainer;
        private readonly Mock<IConsumer> _mockConsumer;

        public StartConsumingTests()
        {
            _mockConfiguration = new Mock<IConfiguration>();
            _mockContainer = new Mock<IBusContainer>();
            _mockConsumer = new Mock<IConsumer>();
            _mockConfiguration.Setup(x => x.GetContainer()).Returns(_mockContainer.Object);
            _mockConfiguration.Setup(x => x.GetConsumer()).Returns(_mockConsumer.Object);
        }

        [Fact]
        public void ShouldGetAllHandlerTypesFromContainer()
        {
            // Arrange
            var bus = new MessageBus.Bus(_mockConfiguration.Object);
            _mockContainer.Setup(x => x.GetHandlerTypes()).Returns(new List<HandlerReference>());

            // Act
            bus.StartConsuming();

            // Assert
            _mockContainer.Verify(x => x.GetHandlerTypes(), Times.Once);
            _mockContainer.VerifyAll();
        }

        [Fact]
        public void ShouldCreateAConsumerForEachHandler()
        {
            // Arrange
            var bus = new MessageBus.Bus(_mockConfiguration.Object);

            var handlerReferences = new List<HandlerReference>
            {
                new HandlerReference
                {
                    HandlerType = typeof (FakeHandler1),
                    MessageType = typeof (FakeMessage1)
                },
                new HandlerReference
                {
                    HandlerType = typeof (FakeHandler2),
                    MessageType = typeof (FakeMessage2)
                }
            };

            _mockContainer.Setup(x => x.GetHandlerTypes()).Returns(handlerReferences);

            // Act
            bus.StartConsuming();

            // Assert
            _mockConfiguration.Verify(x => x.GetConsumer(), Times.Exactly(2));
            _mockContainer.VerifyAll();
        }

        [Fact]
        public void ShouldStartConsumingAllMessagesFromTheContainer()
        {
            // Arrange
            var bus = new MessageBus.Bus(_mockConfiguration.Object);

            var handlerReferences = new List<HandlerReference>
            {
                new HandlerReference
                {
                    HandlerType = typeof (FakeHandler1),
                    MessageType = typeof (FakeMessage1)
                },
                new HandlerReference
                {
                    HandlerType = typeof (FakeHandler2),
                    MessageType = typeof (FakeMessage2)
                }
            };

            _mockContainer.Setup(x => x.GetHandlerTypes()).Returns(handlerReferences);

            // Act
            bus.StartConsuming();

            // Assert
            _mockConsumer.Verify(x => x.StartConsuming(It.IsAny<ConsumerEventHandler>(), "RMessageBusUnitTestsFakesMessagesFakeMessage1", ".FakeMessage1"), Times.Once);
            _mockConsumer.Verify(x => x.StartConsuming(It.IsAny<ConsumerEventHandler>(), "RMessageBusUnitTestsFakesMessagesFakeMessage2", ".FakeMessage2"), Times.Once);
            _mockConsumer.VerifyAll();
        }
    }
}