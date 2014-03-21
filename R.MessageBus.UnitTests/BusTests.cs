using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using R.MessageBus.Client.RabbitMQ;
using R.MessageBus.Interfaces;
using Xunit;

namespace R.MessageBus.UnitTests
{
    public class BusTests
    {
        private readonly Mock<IConfiguration> _mockConfiguration;
        private readonly Mock<IBusContainer> _mockContainer;
        private readonly Mock<IConsumer> _mockConsumer;

        public BusTests()
        {
            _mockConfiguration = new Mock<IConfiguration>();
            _mockContainer = new Mock<IBusContainer>();
            _mockConsumer = new Mock<IConsumer>();
            _mockConfiguration.Setup(x => x.GetContainer()).Returns(_mockContainer.Object);
            _mockConfiguration.Setup(x => x.GetConsumer()).Returns(_mockConsumer.Object);
        }

        [Fact]
        public void StartConsumingShouldGetAllHandlerTypesFromContainer()
        {
            // Arrange
            var bus = new Bus(_mockConfiguration.Object);
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
            var bus = new Bus(_mockConfiguration.Object);

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
            var bus = new Bus(_mockConfiguration.Object);

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
            _mockConsumer.Verify(x => x.StartConsuming(It.IsAny<ConsumerEventHandler>(), "RMessageBusUnitTestsFakeMessage1", ".FakeMessage1"), Times.Once);
            _mockConsumer.Verify(x => x.StartConsuming(It.IsAny<ConsumerEventHandler>(), "RMessageBusUnitTestsFakeMessage2", ".FakeMessage2"), Times.Once);
            _mockConsumer.VerifyAll();
        }

        [Fact]
        public void ConsumeMessageEventGetsTheCorrectHandlerTypesFromContainer()
        {
            // Arrange
            var bus = new Bus(_mockConfiguration.Object);

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
            _mockContainer.Setup(x => x.GetHandlerTypes(typeof(IMessageHandler<FakeMessage1>))).Returns(new List<HandlerReference>());
            
            bus.StartConsuming();

            // Act
            _fakeEventHandler(new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            }.ToByteArray());

            // Assert
            _mockContainer.Verify(x => x.GetHandlerTypes(It.Is<Type>(y => y == typeof(IMessageHandler<FakeMessage1>))), Times.Once());
        }

        [Fact]
        public void ConsumeMessageEventExecutesTheCorrectHandlers()
        {
            // Arrange
            var bus = new Bus(_mockConfiguration.Object);

            var message1HandlerReference = new HandlerReference
            {
                HandlerType = typeof (FakeHandler1),
                MessageType = typeof (FakeMessage1)
            };

            var handlerReferences = new List<HandlerReference>
            {
                message1HandlerReference,
                new HandlerReference
                {
                    HandlerType = typeof (FakeHandler2),
                    MessageType = typeof (FakeMessage2)
                }
            };

            _mockContainer.Setup(x => x.GetHandlerTypes()).Returns(handlerReferences);
            _mockConsumer.Setup(x => x.StartConsuming(It.Is<ConsumerEventHandler>(y => AssignEventHandler(y)), It.IsAny<string>(), It.IsAny<string>()));
            _mockContainer.Setup(x => x.GetHandlerTypes(typeof(IMessageHandler<FakeMessage1>))).Returns(new List<HandlerReference>
            {
                message1HandlerReference
            });
            var fakeHandler = new FakeHandler1();
            _mockContainer.Setup(x => x.GetHandlerInstance(typeof (FakeHandler1))).Returns(fakeHandler);

            bus.StartConsuming();

            // Act
            var message1 = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };
            _fakeEventHandler(message1.ToByteArray());

            var message2 = new FakeMessage2(Guid.NewGuid())
            {
                DisplayName = "Tim Watson"
            };
            _fakeEventHandler(message2.ToByteArray());

            // Assert
            Assert.Equal(message1.CorrelationId, fakeHandler.Command.CorrelationId);
            Assert.Equal(message1.Username, fakeHandler.Command.Username);
            _mockContainer.Verify(x => x.GetHandlerInstance(typeof (FakeHandler2)), Times.Never);
        }

        public bool AssignEventHandler(ConsumerEventHandler eventHandler)
        {
            _fakeEventHandler = eventHandler;
            return true;
        }

        ConsumerEventHandler _fakeEventHandler;
    }

    [Serializable]
    public class FakeMessage1 : Message
    {
        public FakeMessage1(Guid correlationId) : base(correlationId){}
        public string Username { get; set; }
    }

    public class FakeHandler1 : IMessageHandler<FakeMessage1>
    {
        public void Execute(FakeMessage1 command)
        {
            Command = command;
        }

        public FakeMessage1 Command { get; set; }
    }

    [Serializable]
    public class FakeMessage2 : Message
    {
        public FakeMessage2(Guid correlationId) : base(correlationId) { }
        public string DisplayName { get; set; }
    }

    public class FakeHandler2 : IMessageHandler<FakeMessage2>
    {
        public void Execute(FakeMessage2 command) { }
    }
}