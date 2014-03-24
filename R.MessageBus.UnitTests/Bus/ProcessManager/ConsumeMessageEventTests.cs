using System.Collections.Generic;
using Moq;
using R.MessageBus.Interfaces;
using R.MessageBus.UnitTests.Fakes.Handlers;
using R.MessageBus.UnitTests.Fakes.Messages;
using Xunit;

namespace R.MessageBus.UnitTests.Bus.ProcessManager
{
    public class ProcessManagerTests
    {
        private ConsumerEventHandler _fakeEventHandler;
        private readonly Mock<IConfiguration> _mockConfiguration;
        private readonly Mock<IBusContainer> _mockContainer;
        private readonly Mock<IConsumer> _mockConsumer;
        private MessageBus.Bus _bus;

        public ProcessManagerTests()
        {
            _mockConfiguration = new Mock<IConfiguration>();
            _mockContainer = new Mock<IBusContainer>();
            _mockConsumer = new Mock<IConsumer>();
            _mockConfiguration.Setup(x => x.GetContainer()).Returns(_mockContainer.Object);
            _mockConfiguration.Setup(x => x.GetConsumer()).Returns(_mockConsumer.Object);

            _bus = new MessageBus.Bus(_mockConfiguration.Object);

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
        }

        public bool AssignEventHandler(ConsumerEventHandler eventHandler)
        {
            _fakeEventHandler = eventHandler;
            return true;
        }

        [Fact]
        public void ShouldGetCorrectProcessManagerReferencesFromContainer()
        {
            // Arrange
            
            _mockContainer.Setup(x => x.GetHandlerTypes(typeof(IMessageHandler<FakeMessage1>))).Returns(new List<HandlerReference>());

            //_bus.StartConsuming();
            // Act

            // Assert
        }


        [Fact]
        public void ShouldStartNewProcessManager()
        {
            
        }

        [Fact]
        public void ShouldPersistNewProcessManager()
        {
        }

        [Fact]
        public void ShouldFindExistingProcessManagerInstance()
        {
        }

        [Fact]
        public void ShouldStartProcessManagerWithExistingData()
        {
        }

        [Fact]
        public void ShouldUpdateProcessManagerData()
        {
        }

        [Fact]
        public void ShouldRemoveProcessManagerDataIfProcessManagerIsComplete()
        {
        }
    }
}