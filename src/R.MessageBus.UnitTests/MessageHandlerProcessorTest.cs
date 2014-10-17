using System;
using System.Collections.Generic;
using Moq;
using R.MessageBus.Core;
using R.MessageBus.Interfaces;
using R.MessageBus.UnitTests.Fakes.Handlers;
using R.MessageBus.UnitTests.Fakes.Messages;
using Xunit;

namespace R.MessageBus.UnitTests
{
    public class MessageHandlerProcessorTest
    {
        private readonly Mock<IBusContainer> _mockContainer;

        public MessageHandlerProcessorTest()
        {
            _mockContainer = new Mock<IBusContainer>();
        }

        [Fact]
        public void ProcessMessageShouldGetTheCorrectHandlerTypesFromContainer()
        {
            // Arrange
            var serializer = new JsonMessageSerializer();
            var messageProcessor = new MessageHandlerProcessor(_mockContainer.Object, serializer);

            // Act
            messageProcessor.ProcessMessage<FakeMessage1>(serializer.Serialize(new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            }), null);

            // Assert
            _mockContainer.Verify(x => x.GetHandlerTypes(It.Is<Type>(y => y == typeof(IMessageHandler<FakeMessage1>))), Times.Once());
        }
       
        [Fact]
        public void ShouldExecuteTheCorrectHandlers()
        {
            // Arrange
            var serializer = new JsonMessageSerializer();
            var messageProcessor = new MessageHandlerProcessor(_mockContainer.Object, serializer);

            var message1HandlerReference = new HandlerReference
            {
                HandlerType = typeof (FakeHandler1),
                MessageType = typeof (FakeMessage1)
            };


            _mockContainer.Setup(x => x.GetHandlerTypes(typeof(IMessageHandler<FakeMessage1>))).Returns(new List<HandlerReference>
            {
                message1HandlerReference
            });

            var fakeHandler = new FakeHandler1();
            _mockContainer.Setup(x => x.GetInstance(typeof (FakeHandler1))).Returns(fakeHandler);

            // Act
            var message1 = new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            };
            messageProcessor.ProcessMessage<FakeMessage1>(serializer.Serialize(message1), null);

            var message2 = new FakeMessage2(Guid.NewGuid())
            {
                DisplayName = "Tim Watson"
            };

            messageProcessor.ProcessMessage<FakeMessage2>(serializer.Serialize(message2), null);

            // Assert
            Assert.Equal(message1.CorrelationId, fakeHandler.Command.CorrelationId);
            Assert.Equal(message1.Username, fakeHandler.Command.Username);
            _mockContainer.Verify(x => x.GetInstance(typeof (FakeHandler2)), Times.Never);
        }
    }
}