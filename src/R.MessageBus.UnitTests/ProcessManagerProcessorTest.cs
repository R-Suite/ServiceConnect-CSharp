using System;
using System.Collections.Generic;
using System.Text;
using Moq;
using R.MessageBus.Client.RabbitMQ;
using R.MessageBus.Core;
using R.MessageBus.Interfaces;
using R.MessageBus.UnitTests.Fakes;
using R.MessageBus.UnitTests.Fakes.Handlers;
using R.MessageBus.UnitTests.Fakes.Messages;
using R.MessageBus.UnitTests.Fakes.ProcessManagers;
using Xunit;

namespace R.MessageBus.UnitTests
{
    public class ProcessManagerProcessorTest
    {
        private readonly Mock<IBusContainer> _mockContainer;
        private readonly Mock<IProcessManagerFinder> _mockProcessManagerFinder;

        public ProcessManagerProcessorTest()
        {
            _mockContainer = new Mock<IBusContainer>();
            _mockProcessManagerFinder = new Mock<IProcessManagerFinder>();
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

            _mockContainer.Setup(x => x.GetInstance(typeof(FakeProcessManager1))).Returns(new FakeProcessManager1());

            var data = new FakeProcessManagerData
            {
                User = "Tim Watson"
            };
            var mockPersistanceData = new Mock<IPersistanceData<FakeProcessManagerData>>();
            mockPersistanceData.Setup(x => x.Data).Returns(data);
            _mockProcessManagerFinder.Setup(x => x.FindData<FakeProcessManagerData>(It.IsAny<Guid>())).Returns(mockPersistanceData.Object);

            var processManagerProcessor = new ProcessManagerProcessor(_mockProcessManagerFinder.Object, _mockContainer.Object);

            // Act
            processManagerProcessor.ProcessMessage(new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            });

            // Assert
            _mockContainer.Verify(x => x.GetHandlerTypes(typeof (IMessageHandler<FakeMessage1>)), Times.Once);
            _mockContainer.Verify(x => x.GetHandlerTypes(typeof (IStartProcessManager<FakeMessage1>)), Times.Once);
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

            _mockContainer.Setup(x => x.GetInstance(typeof(FakeProcessManager1))).Returns(processManager);

            var data = new FakeProcessManagerData
            {
                User = "Tim Watson"
            };
            var mockPersistanceData = new Mock<IPersistanceData<FakeProcessManagerData>>();
            mockPersistanceData.Setup(x => x.Data).Returns(data);
            _mockProcessManagerFinder.Setup(x => x.FindData<FakeProcessManagerData>(It.IsAny<Guid>())).Returns(mockPersistanceData.Object);

            var processManagerProcessor = new ProcessManagerProcessor(_mockProcessManagerFinder.Object, _mockContainer.Object);

            // Act
            processManagerProcessor.ProcessMessage(new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            });

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
            _mockContainer.Setup(x => x.GetInstance(typeof(FakeProcessManager1))).Returns(processManager);

            var data = new FakeProcessManagerData
            {
                User = "Tim Watson"
            };
            var mockPersistanceData = new Mock<IPersistanceData<FakeProcessManagerData>>();
            mockPersistanceData.Setup(x => x.Data).Returns(data);
            _mockProcessManagerFinder.Setup(x => x.FindData<FakeProcessManagerData>(It.IsAny<Guid>())).Returns(mockPersistanceData.Object);

            var processManagerProcessor = new ProcessManagerProcessor(_mockProcessManagerFinder.Object, _mockContainer.Object);

            // Act
            processManagerProcessor.ProcessMessage(new FakeMessage1(Guid.NewGuid())
            {
                Username = "Tim Watson"
            });

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
            _mockContainer.Setup(x => x.GetInstance(typeof(FakeProcessManager1))).Returns(processManager);

            var data = new FakeProcessManagerData
            {
                User = "Tim Watson"
            };
            var mockPersistanceData = new Mock<IPersistanceData<FakeProcessManagerData>>();
            mockPersistanceData.Setup(x => x.Data).Returns(data);
            _mockProcessManagerFinder.Setup(x => x.FindData<FakeProcessManagerData>(id)).Returns(mockPersistanceData.Object);

            var processManagerProcessor = new ProcessManagerProcessor(_mockProcessManagerFinder.Object, _mockContainer.Object);

            // Act
            processManagerProcessor.ProcessMessage(message);

            // Assert
            _mockContainer.Verify(x => x.GetInstance(typeof (FakeProcessManager1)), Times.Once);
            _mockProcessManagerFinder.Verify(x => x.FindData<FakeProcessManagerData>(id), Times.Once);
        }

        [Fact]
        public void ShouldStartProcessManagerWithExistingData()
        {
            // Arrange
            var id = Guid.NewGuid();
            var message = new FakeMessage2(id)
            {
                Email = "abc@123.com"
            };

            var data = new FakeProcessManagerData
            {
                User = "Tim Watson"
            };

            var mockPersistanceData = new Mock<IPersistanceData<FakeProcessManagerData>>();
            mockPersistanceData.Setup(x => x.Data).Returns(data);
            _mockContainer.Setup(x => x.GetHandlerTypes(typeof(IMessageHandler<FakeMessage2>))).Returns(new List<HandlerReference>
            {
                new HandlerReference
                {
                    HandlerType = typeof(FakeProcessManager1),
                    MessageType = typeof(FakeMessage2)
                }
            });

            var processManager = new FakeProcessManager1();
            _mockContainer.Setup(x => x.GetInstance(typeof(FakeProcessManager1))).Returns(processManager);
            _mockProcessManagerFinder.Setup(x => x.FindData<FakeProcessManagerData>(id)).Returns(mockPersistanceData.Object);

            var processManagerProcessor = new ProcessManagerProcessor(_mockProcessManagerFinder.Object, _mockContainer.Object);

            // Act
            processManagerProcessor.ProcessMessage(message);

            // Assert
            Assert.Equal("Tim Watson", processManager.Data.User); // Can only be this if Data was set on process manager
            Assert.Equal("abc@123.com", processManager.Data.Email); // Can only be this if execute was called
        }

        [Fact]
        public void ShouldUpdateProcessManagerData()
        {
            // Arrange
            var id = Guid.NewGuid();
            var message = new FakeMessage2(id)
            {
                Email = "abc@123.com"
            };

            var data = new FakeProcessManagerData
            {
                User = "Tim Watson"
            };

            var mockPersistanceData = new Mock<IPersistanceData<FakeProcessManagerData>>();
            mockPersistanceData.Setup(x => x.Data).Returns(data);
            _mockContainer.Setup(x => x.GetHandlerTypes(typeof(IMessageHandler<FakeMessage2>))).Returns(new List<HandlerReference>
            {
                new HandlerReference
                {
                    HandlerType = typeof(FakeProcessManager1),
                    MessageType = typeof(FakeMessage2)
                }
            });

            var processManager = new FakeProcessManager1();
            _mockContainer.Setup(x => x.GetInstance(typeof(FakeProcessManager1))).Returns(processManager);
            _mockProcessManagerFinder.Setup(x => x.FindData<FakeProcessManagerData>(id)).Returns(mockPersistanceData.Object);

            _mockProcessManagerFinder.Setup(x => x.UpdateData(It.IsAny<FakePersistanceData>()));

            var processManagerProcessor = new ProcessManagerProcessor(_mockProcessManagerFinder.Object, _mockContainer.Object);

            // Act
            processManagerProcessor.ProcessMessage(message);

            // Assert
            _mockProcessManagerFinder.Verify(x => x.UpdateData(It.Is<IPersistanceData<FakeProcessManagerData>>(y => y.Data.Email == "abc@123.com" && y.Data.User == "Tim Watson")), Times.Once);
        }

        [Fact]
        public void ShouldRemoveProcessManagerDataIfProcessManagerIsComplete()
        {
            // Arrange
            var id = Guid.NewGuid();
            var message = new FakeMessage2(id)
            {
                Email = "abc@123.com"
            };

            var data = new FakeProcessManagerData
            {
                User = "Tim Watson"
            };

            var mockPersistanceData = new Mock<IPersistanceData<FakeProcessManagerData>>();
            mockPersistanceData.Setup(x => x.Data).Returns(data);
            _mockContainer.Setup(x => x.GetHandlerTypes(typeof(IMessageHandler<FakeMessage2>))).Returns(new List<HandlerReference>
            {
                new HandlerReference
                {
                    HandlerType = typeof(FakeProcessManager1),
                    MessageType = typeof(FakeMessage2)
                }
            });

            var processManager = new FakeProcessManager1
            {
                Complete = true
            };
            _mockContainer.Setup(x => x.GetInstance(typeof(FakeProcessManager1))).Returns(processManager);
            _mockProcessManagerFinder.Setup(x => x.FindData<FakeProcessManagerData>(id)).Returns(mockPersistanceData.Object);

            _mockProcessManagerFinder.Setup(x => x.UpdateData(It.IsAny<FakePersistanceData>()));

            var processManagerProcessor = new ProcessManagerProcessor(_mockProcessManagerFinder.Object, _mockContainer.Object);

            // Act
            processManagerProcessor.ProcessMessage(message);

            // Assert
            _mockProcessManagerFinder.Verify(x => x.DeleteData(It.Is<IPersistanceData<FakeProcessManagerData>>(y => y.Data.Email == "abc@123.com" && y.Data.User == "Tim Watson")), Times.Once);
        }
    }
}