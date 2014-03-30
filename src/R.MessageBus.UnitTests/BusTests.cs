using System;
using System.Collections.Generic;
using System.Text;
using Moq;
using R.MessageBus.Client.RabbitMQ;
using R.MessageBus.Interfaces;
using R.MessageBus.UnitTests.Fakes.Handlers;
using R.MessageBus.UnitTests.Fakes.Messages;
using Xunit;

namespace R.MessageBus.UnitTests
{
    public class BusTests
    {
        private readonly IJsonMessageSerializer _serializer = new JsonMessageSerializer();
        private readonly Mock<IConfiguration> _mockConfiguration;
        private readonly Mock<IBusContainer> _mockContainer;
        private readonly Mock<IConsumer> _mockConsumer;
        private ConsumerEventHandler _fakeEventHandler;

        public BusTests()
        {
            _mockConfiguration = new Mock<IConfiguration>();
            _mockContainer = new Mock<IBusContainer>();
            _mockConsumer = new Mock<IConsumer>();
            _mockConfiguration.Setup(x => x.GetContainer()).Returns(_mockContainer.Object);
            _mockConfiguration.Setup(x => x.GetConsumer()).Returns(_mockConsumer.Object);
        }

        public bool AssignEventHandler(ConsumerEventHandler eventHandler)
        {
            _fakeEventHandler = eventHandler;
            return true;
        }

        [Fact]
        public void StartConsumingShouldGetAllHandlerTypesFromContainer()
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
        public void StartConsumingShouldCreateAConsumerForEachHandler()
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
        public void StartConsumingShouldStartConsumingAllMessagesFromTheContainer()
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
            _mockConsumer.Verify(x => x.StartConsuming(It.IsAny<ConsumerEventHandler>(), "RMessageBusUnitTestsFakesMessagesFakeMessage1", ".RMessageBusUnitTestsFakesMessagesFakeMessage1"), Times.Once);
            _mockConsumer.Verify(x => x.StartConsuming(It.IsAny<ConsumerEventHandler>(), "RMessageBusUnitTestsFakesMessagesFakeMessage2", ".RMessageBusUnitTestsFakesMessagesFakeMessage2"), Times.Once);
            _mockConsumer.VerifyAll();
        }

        [Fact]
        public void ConsumeMessageEventShouldProcessMessagesOnMessageHandler()
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
            _mockConsumer.Setup(x => x.StartConsuming(It.Is<ConsumerEventHandler>(y => AssignEventHandler(y)), It.IsAny<string>(), It.IsAny<string>()));
            var mockMessageHandlerProcessor = new Mock<IMessageHandlerProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IMessageHandlerProcessor>(It.Is<Dictionary<string, object>>(y => y["container"] == _mockContainer.Object))).Returns(mockMessageHandlerProcessor.Object);
            mockMessageHandlerProcessor.Setup(x => x.ProcessMessage(It.IsAny<FakeMessage1>()));
            var mockProcessManagerProcessor = new Mock<IProcessManagerProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IProcessManagerProcessor>(It.IsAny<Dictionary<string, object>>())).Returns(mockProcessManagerProcessor.Object);
            mockProcessManagerProcessor.Setup(x => x.ProcessMessage(It.IsAny<FakeMessage1>()));

            bus.StartConsuming();

            // Act
            _fakeEventHandler(Encoding.UTF8.GetBytes(_serializer.Serialize(new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            })));

            // Assert
            mockMessageHandlerProcessor.Verify(x => x.ProcessMessage(It.Is<FakeMessage1>(y => y.Username == "Tim Watson")), Times.Once);
        }

        [Fact]
        public void ConsumeMessageEventShouldProcessMessagesOnProcessManagers()
        {
            // Arrange
            var mockProcessManagerFinder = new Mock<IProcessManagerFinder>();
            _mockConfiguration.Setup(x => x.GetProcessManagerFinder()).Returns(mockProcessManagerFinder.Object);

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
            _mockConsumer.Setup(x => x.StartConsuming(It.Is<ConsumerEventHandler>(y => AssignEventHandler(y)), It.IsAny<string>(), It.IsAny<string>()));
            var mockProcessManagerProcessor = new Mock<IProcessManagerProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IProcessManagerProcessor>(It.Is<Dictionary<string, object>>(y => y["container"] == _mockContainer.Object &&
                                                                                                                     y["processManagerFinder"] == mockProcessManagerFinder.Object))).Returns(mockProcessManagerProcessor.Object);
            mockProcessManagerProcessor.Setup(x => x.ProcessMessage(It.IsAny<FakeMessage1>()));
            var mockMessageHandlerProcessor = new Mock<IMessageHandlerProcessor>();
            _mockContainer.Setup(x => x.GetInstance<IMessageHandlerProcessor>(It.IsAny<Dictionary<string, object>>())).Returns(mockMessageHandlerProcessor.Object);
            mockMessageHandlerProcessor.Setup(x => x.ProcessMessage(It.IsAny<FakeMessage1>()));

            bus.StartConsuming();

            // Act
            _fakeEventHandler(Encoding.UTF8.GetBytes(_serializer.Serialize(new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            })));

            // Assert
            mockProcessManagerProcessor.Verify(x => x.ProcessMessage(It.Is<FakeMessage1>(y => y.Username == "Tim Watson")), Times.Once); 
        }
    }
}