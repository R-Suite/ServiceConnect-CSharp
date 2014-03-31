using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Moq;
using R.MessageBus.Interfaces;
using R.MessageBus.UnitTests.Fakes.Messages;
using Xunit;

namespace R.MessageBus.UnitTests.Bus
{
    public class BusSetupTests
    {
        [Fact]
        public void ShouldSetupBusWithCorrectCustomDatabaseNameAndConnectionString()
        {
            // Arrange
            IBus bus = MessageBus.Bus.Initialize(config =>
            {
                config.SetProcessManagerFinder<FakeProcessManagerFinder>();
                config.SetContainer<FakeContainer>();
                config.PersistenceStoreDatabaseName = "TestDatabaseName";
                config.PersistenceStoreConnectionString = "TestConnectionString";
            });

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.Equal("TestDatabaseName", configuration.PersistenceStoreDatabaseName);
            Assert.Equal("TestConnectionString", configuration.PersistenceStoreConnectionString);
        }

        [Fact]
        public void ShouldSetupBusWithCorrectDefaultDatabaseNameAndConnectionString()
        {
            // Arrange
            IBus bus = MessageBus.Bus.Initialize(config =>
            {
                config.SetProcessManagerFinder<FakeProcessManagerFinder>();
                config.SetContainer<FakeContainer>();
            });

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.Equal("RMessageBusPersistantStore", configuration.PersistenceStoreDatabaseName);
            Assert.Equal("mongodb://localhost/", configuration.PersistenceStoreConnectionString);
        }

        [Fact]
        public void ShouldSetupBusToScanForAllHandlers()
        {
            // Arrange
            IBus bus = MessageBus.Bus.Initialize(config =>
            {
                config.SetProcessManagerFinder<FakeProcessManagerFinder>();
                config.SetContainer<FakeContainer>();
                config.ScanForMesssageHandlers = true;
            });

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.Equal(true, configuration.ScanForMesssageHandlers);
        }

        [Fact]
        public void ShouldSetupBusWithCustomContainer()
        {
            // Arrange
            IBus bus = MessageBus.Bus.Initialize(config => config.SetContainer<FakeContainer>());

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.Equal(typeof(FakeContainer), configuration.Container);
        }

        [Fact]
        public void ShouldSetupBusWithCustomConsumer()
        {
            // Arrange
            IBus bus = MessageBus.Bus.Initialize(config => config.SetConsumer<FakeConsumer>());

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.Equal(typeof(FakeConsumer), configuration.ConsumerType);
        }

        [Fact]
        public void ShouldSetupBusWithCustomPublisher()
        {
            // Arrange
            IBus bus = MessageBus.Bus.Initialize(config => config.SetProducer<FakePublisher>());

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.Equal(typeof(FakePublisher), configuration.ProducerType);
        }

        [Fact]
        public void ShouldSetupBusWithCustomProcessManagerFinder()
        {
            // Arrange
            IBus bus = MessageBus.Bus.Initialize(config => config.SetProcessManagerFinder<FakeProcessManagerFinder>());

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.Equal(typeof(FakeProcessManagerFinder), configuration.GetProcessManagerFinder().GetType());
        }

        [Fact]
        public void SouldInitializeTheContainer()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockContainer = new Mock<IBusContainer>();
            mockContainer.Setup(x => x.Initialize());
            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);

            // Act
            new MessageBus.Bus(mockConfiguration.Object);

            // Assert
            mockContainer.Verify(x => x.Initialize(), Times.Once);
        }

        [Fact]
        public void ShouldAddMessageMappingsToConfiguration()
        {
            // Arrange
            var bus = MessageBus.Bus.Initialize(conf =>
            {
                conf.AddEndPointMapping(typeof(FakeMessage1), "MyEndPoint1");
                conf.AddEndPointMapping(typeof(FakeMessage2), "MyEndPoint2");
            });

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.True(configuration.EndPointMappings.Any(x => x.Key == typeof(FakeMessage1).FullName && x.Value == "MyEndPoint1"));
            Assert.True(configuration.EndPointMappings.Any(x => x.Key == typeof(FakeMessage2).FullName && x.Value == "MyEndPoint2"));
        }

        [Fact]
        public void SouldInjectItselfIntoTheContainer()
        {
            // Arrange
            var mockConfiguration = new Mock<IConfiguration>();
            var mockContainer = new Mock<IBusContainer>();
            mockContainer.Setup(x => x.Initialize());
            mockConfiguration.Setup(x => x.GetContainer()).Returns(mockContainer.Object);

            // Act
            var bus = new MessageBus.Bus(mockConfiguration.Object);

            // Assert
            mockContainer.Verify(x => x.AddBus(bus), Times.Once);
        }
        
        public class FakeContainer : IBusContainer
        {
            public IEnumerable<HandlerReference> GetHandlerTypes()
            {
                throw new NotImplementedException();
            }

            public IEnumerable<HandlerReference> GetHandlerTypes(Type messageHandler)
            {
                throw new NotImplementedException();
            }

            public object GetInstance(Type handlerType)
            {
                throw new NotImplementedException();
            }

            public T GetInstance<T>(IDictionary<string, object> arguments)
            {
                throw new NotImplementedException();
            }

            public T GetInstance<T>()
            {
                throw new NotImplementedException();
            }

            public void ScanForHandlers()
            {}

            public void Initialize()
            {
                Initialized = true;
            }

            public void AddBus(IBus bus)
            {
            }

            public bool Initialized { get; set; }
        }

        public class FakeConsumer : IConsumer
        {
            public FakeConsumer(ITransportSettings transportSettings)
            {
            }

            public void StartConsuming(ConsumerEventHandler messageReceived, string routingKey, string queueName = null)
            {
                throw new NotImplementedException();
            }

            public void StopConsuming()
            {
                throw new NotImplementedException();
            }

            public void Dispose()
            {
                throw new NotImplementedException();
            }
        }

        public class FakePublisher : IProducer
        {
            public FakePublisher(ITransportSettings transportSettings)
            {
            }

            public void Publish<T>(T message) where T : Message
            {
                throw new NotImplementedException();
            }

            public void Send<T>(T message) where T : Message
            {
                throw new NotImplementedException();
            }

            public void Send<T>(string endPoint, T message) where T : Message
            {
                throw new NotImplementedException();
            }

            public void Disconnect()
            {
                throw new NotImplementedException();
            }

            public void Dispose()
            {
                throw new NotImplementedException();
            }
        }

        public class FakeProcessManagerFinder : IProcessManagerFinder
        {
            public FakeProcessManagerFinder(string connectionString, string databaseName)
            {}

            public IPersistanceData<T> FindData<T>(Guid id) where T : class, IProcessManagerData
            {
                throw new NotImplementedException();
            }

            public void InsertData(IProcessManagerData data)
            {
                throw new NotImplementedException();
            }

            public void UpdateData<T>(IPersistanceData<T> data) where T : class, IProcessManagerData
            {
                throw new NotImplementedException();
            }

            public void DeleteData<T>(IPersistanceData<T> data) where T : class, IProcessManagerData
            {
                throw new NotImplementedException();
            }
        }
    }
}