using System;
using System.Collections.Generic;
using System.Text;
using Moq;
using R.MessageBus.Client.RabbitMQ;
using R.MessageBus.Interfaces;
using R.MessageBus.UnitTests.Fakes;
using R.MessageBus.UnitTests.Fakes.Handlers;
using R.MessageBus.UnitTests.Fakes.Messages;
using R.MessageBus.UnitTests.Fakes.ProcessManagers;
using Xunit;

namespace R.MessageBus.UnitTests.Bus.ProcessManager
{
    public class ProcessManagerTests
    {
        private readonly IJsonMessageSerializer _serializer = new JsonMessageSerializer();
        private ConsumerEventHandler _fakeEventHandler;
        private readonly Mock<IConfiguration> _mockConfiguration;
        private readonly Mock<IBusContainer> _mockContainer;
        private readonly Mock<IConsumer> _mockConsumer;
        private readonly MessageBus.Bus _bus;
        private readonly Mock<IProcessManagerFinder> _mockProcessManagerFinder;

        public ProcessManagerTests()
        {
            _mockConfiguration = new Mock<IConfiguration>();
            _mockContainer = new Mock<IBusContainer>();
            _mockConsumer = new Mock<IConsumer>();
            _mockProcessManagerFinder = new Mock<IProcessManagerFinder>();
            _mockConfiguration.Setup(x => x.GetContainer()).Returns(_mockContainer.Object);
            _mockConfiguration.Setup(x => x.GetConsumer()).Returns(_mockConsumer.Object);
            _mockConfiguration.Setup(x => x.GetProcessManagerFinder()).Returns(_mockProcessManagerFinder.Object);

            _bus = new MessageBus.Bus(_mockConfiguration.Object);

            var handlerReferences = new List<HandlerReference>
            {
                new HandlerReference
                {
                    HandlerType = typeof (FakeProcessManager1),
                    MessageType = typeof (FakeMessage1)
                },
                new HandlerReference
                {
                    HandlerType = typeof (FakeHandler2),
                    MessageType = typeof (FakeMessage2)
                },
                new HandlerReference
                {
                    HandlerType = typeof(FakeProcessManager1),
                    MessageType = typeof(FakeMessage1)
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
            _mockContainer.Setup(x => x.GetHandlerTypes(typeof(IMessageHandler<FakeMessage1>))).Returns(new List<HandlerReference>
            {
                new HandlerReference
                {
                    HandlerType = typeof(FakeProcessManager1),
                    MessageType = typeof(FakeMessage1)
                }
            });
            _mockContainer.Setup(x => x.GetHandlerTypes(typeof(IStartProcessManager<FakeMessage1>))).Returns(new List<HandlerReference>
            {
                new HandlerReference
                {
                    HandlerType = typeof(FakeProcessManager1),
                    MessageType = typeof(FakeMessage1)
                }
            });

            _bus.StartConsuming();

            _mockContainer.Setup(x => x.GetHandlerInstance(typeof(FakeProcessManager1))).Returns(new FakeProcessManager1());

            // Act
            _fakeEventHandler(Encoding.UTF8.GetBytes(_serializer.Serialize(new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            })));

            // Assert
            _mockContainer.Verify(x => x.GetHandlerTypes(typeof (IMessageHandler<FakeMessage1>)), Times.Once);
            _mockContainer.Verify(x => x.GetHandlerTypes(typeof(IStartProcessManager<FakeMessage1>)), Times.Once);
        }

        [Fact]
        public void ShouldStartNewProcessManager()
        {
            // Arrange
            _mockContainer.Setup(x => x.GetHandlerTypes(typeof(IMessageHandler<FakeMessage1>))).Returns(new List<HandlerReference>
            {
                new HandlerReference
                {
                    HandlerType = typeof(FakeProcessManager1),
                    MessageType = typeof(FakeMessage1)
                }
            });
            _mockContainer.Setup(x => x.GetHandlerTypes(typeof(IStartProcessManager<FakeMessage1>))).Returns(new List<HandlerReference>
            {
                new HandlerReference
                {
                    HandlerType = typeof(FakeProcessManager1),
                    MessageType = typeof(FakeMessage1)
                }
            });

            var processManager = new FakeProcessManager1();

            _mockContainer.Setup(x => x.GetHandlerInstance(typeof(FakeProcessManager1))).Returns(processManager);

            _bus.StartConsuming();

            // Act
            _fakeEventHandler(Encoding.UTF8.GetBytes(_serializer.Serialize(new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            })));

            // Assert
            // Data.User is set by the ProcessManagers Execute method
            Assert.Equal("Tim Watson", processManager.Data.User);
        }

        [Fact]
        public void ShouldPersistNewProcessManager()
        {
            // Arrange
            _mockContainer.Setup(x => x.GetHandlerTypes(typeof(IMessageHandler<FakeMessage1>))).Returns(new List<HandlerReference>
            {
                new HandlerReference
                {
                    HandlerType = typeof(FakeProcessManager1),
                    MessageType = typeof(FakeMessage1)
                }
            });
            _mockContainer.Setup(x => x.GetHandlerTypes(typeof(IStartProcessManager<FakeMessage1>))).Returns(new List<HandlerReference>
            {
                new HandlerReference
                {
                    HandlerType = typeof(FakeProcessManager1),
                    MessageType = typeof(FakeMessage1)
                }
            });

            var processManager = new FakeProcessManager1();
            _mockContainer.Setup(x => x.GetHandlerInstance(typeof(FakeProcessManager1))).Returns(processManager);

            _bus.StartConsuming();

            // Act
            _fakeEventHandler(Encoding.UTF8.GetBytes(_serializer.Serialize(new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            })));

            // Assert
            _mockProcessManagerFinder.Verify(x => x.InsertData(It.Is<FakeProcessManagerData>(y => y.User == "Tim Watson")), Times.Once);
        }

        [Fact]
        public void ShouldFindExistingProcessManagerInstance()
        {
            // Arrange
            var id = Guid.NewGuid();
            var message = new FakeMessage2(id);

            _mockContainer.Setup(x => x.GetHandlerTypes(typeof(IMessageHandler<FakeMessage2>))).Returns(new List<HandlerReference>
            {
                new HandlerReference
                {
                    HandlerType = typeof(FakeProcessManager1),
                    MessageType = typeof(FakeMessage2)
                }
            });

            var processManager = new FakeProcessManager1();
            _mockContainer.Setup(x => x.GetHandlerInstance(typeof(FakeProcessManager1))).Returns(processManager);

            var data = new FakeProcessManagerData
            {
                User = "Tim Watson"
            };
            var mockPersistanceData = new Mock<IPersistanceData<FakeProcessManagerData>>();
            mockPersistanceData.Setup(x => x.Data).Returns(data);
            _mockProcessManagerFinder.Setup(x => x.FindData<FakeProcessManagerData>(id)).Returns(mockPersistanceData.Object);

            _bus.StartConsuming();

            // Act
            _fakeEventHandler(Encoding.UTF8.GetBytes(_serializer.Serialize(message)));

            // Assert
            _mockContainer.Verify(x => x.GetHandlerInstance(typeof (FakeProcessManager1)), Times.Once);
            _mockProcessManagerFinder.Verify(x => x.FindData<FakeProcessManagerData>(id), Times.Once);
        }

        [Fact]
        public void ShouldStartProcessManagerWithExistingData()
        {
            // Arrange
            var message = new FakeMessage2(Guid.NewGuid());
            var data = new FakeProcessManagerData();
            _mockContainer.Setup(x => x.GetHandlerTypes(typeof(IMessageHandler<FakeMessage1>))).Returns(new List<HandlerReference>
            {
                new HandlerReference
                {
                    HandlerType = typeof(FakeProcessManager1),
                    MessageType = typeof(FakeMessage2)
                }
            });

            var processManager = new FakeProcessManager1();
            _mockContainer.Setup(x => x.GetHandlerInstance(typeof(FakeProcessManager1))).Returns(processManager);

            _bus.StartConsuming();

            // Act
            _fakeEventHandler(Encoding.UTF8.GetBytes(_serializer.Serialize(new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            })));

            // Assert
            _mockContainer.Verify(x => x.GetHandlerInstance(typeof(FakeProcessManager1)), Times.Once);
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