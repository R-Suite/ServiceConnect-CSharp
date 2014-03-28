using System;
using System.Collections.Generic;
using System.Text;
using Moq;
using R.MessageBus.Client.RabbitMQ;
using R.MessageBus.Interfaces;
using R.MessageBus.UnitTests.Fakes.Handlers;
using R.MessageBus.UnitTests.Fakes.Messages;
using Xunit;

namespace R.MessageBus.UnitTests.Bus.Handler
{
    public class ConsumeMessageEventTests
    {
        private readonly IJsonMessageSerializer _serializer = new JsonMessageSerializer();
        private ConsumerEventHandler _fakeEventHandler;
        private readonly Mock<IConfiguration> _mockConfiguration;
        private readonly Mock<IBusContainer> _mockContainer;
        private readonly Mock<IConsumer> _mockConsumer;

        public ConsumeMessageEventTests()
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
        public void ShouldGetTheCorrectHandlerTypesFromContainer()
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
            _mockContainer.Setup(x => x.GetHandlerTypes(typeof(IMessageHandler<FakeMessage1>))).Returns(new List<HandlerReference>());
            
            bus.StartConsuming();

            // Act
            _fakeEventHandler(Encoding.UTF8.GetBytes(_serializer.Serialize(new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            })));

            // Assert
            _mockContainer.Verify(x => x.GetHandlerTypes(It.Is<Type>(y => y == typeof(IMessageHandler<FakeMessage1>))), Times.Once());
        }
       
        [Fact]
        public void ShouldExecuteTheCorrectHandlers()
        {
            // Arrange
            var bus = new MessageBus.Bus(_mockConfiguration.Object);

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
            _mockContainer.Setup(x => x.GetInstance(typeof (FakeHandler1))).Returns(fakeHandler);

            bus.StartConsuming();

            // Act
            var message1 = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };
            _fakeEventHandler(Encoding.UTF8.GetBytes(_serializer.Serialize(message1)));

            var message2 = new FakeMessage2(Guid.NewGuid())
            {
                DisplayName = "Tim Watson"
            };

            _fakeEventHandler(Encoding.UTF8.GetBytes(_serializer.Serialize(message2)));

            // Assert
            Assert.Equal(message1.CorrelationId, fakeHandler.Command.CorrelationId);
            Assert.Equal(message1.Username, fakeHandler.Command.Username);
            _mockContainer.Verify(x => x.GetInstance(typeof (FakeHandler2)), Times.Never);
        }
    }
}