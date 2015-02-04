using System;
using System.Collections.Generic;
using Moq;
using R.MessageBus.Interfaces;
using R.MessageBus.Settings;
using System.Linq;
using R.MessageBus.UnitTests.Fakes.Messages;
using Xunit;

namespace R.MessageBus.UnitTests
{
    public class BusSetupTests
    {
        [Fact]
        public void ShouldSetupBusWithCorrectCustomDatabaseNameAndConnectionString()
        {
            // Arrange
            IBus bus = Bus.Initialize(config =>
            {
                config.SetProcessManagerFinder<FakeProcessManagerFinder>();
                config.SetContainer<FakeContainer>();
                config.SetProducer<FakePublisher>();
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
            IBus bus = Bus.Initialize(config =>
            {
                config.SetProcessManagerFinder<FakeProcessManagerFinder>();
                config.SetContainer<FakeContainer>();
                config.SetProducer<FakePublisher>();
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
            IBus bus = Bus.Initialize(config =>
            {
                config.SetProcessManagerFinder<FakeProcessManagerFinder>();
                config.SetContainer<FakeContainer>();
                config.SetProducer<FakePublisher>();
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
            IBus bus = Bus.Initialize(config =>
            {
                config.SetContainer<FakeContainer>();
                config.SetProducer<FakePublisher>();
            });

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.Equal(typeof(FakeContainer), configuration.Container);
        }

        [Fact]
        public void ShouldSetupBusWithCustomConsumer()
        {
            // Arrange
            IBus bus = Bus.Initialize(config =>
            {
                config.SetConsumer<FakeConsumer>();
                config.SetProducer<FakePublisher>();
            });

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.Equal(typeof(FakeConsumer), configuration.ConsumerType);
        }

        [Fact]
        public void ShouldSetupBusWithCustomPublisher()
        {
            // Arrange
            IBus bus = Bus.Initialize(config =>
            {
                config.SetProducer<FakePublisher>();
                config.SetContainer<FakeContainer>();
            });

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.Equal(typeof(FakePublisher), configuration.ProducerType);
        }

        [Fact]
        public void ShouldSetupBusWithCustomProcessManagerFinder()
        {
            // Arrange
            IBus bus = Bus.Initialize(config =>
            {
                config.SetProcessManagerFinder<FakeProcessManagerFinder>();
                config.SetContainer<FakeContainer>();
                config.SetProducer<FakePublisher>(); 
            });

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
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings { Queue = new Queue() });

            // Act
            new Bus(mockConfiguration.Object);

            // Assert
            mockContainer.Verify(x => x.Initialize(), Times.Once);
        }

        [Fact]
        public void ShouldAddMessageMappingsToConfiguration()
        {
            // Arrange
            var bus = Bus.Initialize(conf =>
            {
                conf.AddQueueMapping(typeof(FakeMessage1), "MyEndPoint1");
                conf.AddQueueMapping(typeof(FakeMessage2), "MyEndPoint2");
                conf.SetContainer<FakeContainer>();
                conf.SetProducer<FakePublisher>(); 
            });

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.True(configuration.QueueMappings.Any(x => x.Key == typeof(FakeMessage1).FullName && x.Value.Contains("MyEndPoint1")));
            Assert.True(configuration.QueueMappings.Any(x => x.Key == typeof(FakeMessage2).FullName && x.Value.Contains("MyEndPoint2")));
        }

        [Fact]
        public void ShouldSetupQueueName()
        {
            // Arrange
            var bus = Bus.Initialize(c =>
            {
                c.SetQueueName("TestQueue");
                c.SetContainer<FakeContainer>();
                c.SetProducer<FakePublisher>();
            });

            // Act
            var config = bus.Configuration;

            // Assert
            Assert.Equal("TestQueue", config.TransportSettings.Queue.Name);
        }

        [Fact]
        public void ShouldSetupErrorQueueName()
        {
            // Arrange
            var bus = Bus.Initialize(c =>
            {
                c.SetErrorQueueName("TestErrorQueue");
                c.SetContainer<FakeContainer>();
                c.SetProducer<FakePublisher>();
            });

            // Act
            var config = bus.Configuration;

            // Assert
            Assert.Equal("TestErrorQueue", config.TransportSettings.ErrorQueueName);
        }

        [Fact]
        public void ShouldSetupAuditQueueName()
        {
            // Arrange
            var bus = Bus.Initialize(c =>
            {
                c.SetAuditQueueName("TestAuditQueue");
                c.SetContainer<FakeContainer>();
                c.SetProducer<FakePublisher>(); 
            });

            // Act
            var config = bus.Configuration;

            // Assert
            Assert.Equal("TestAuditQueue", config.TransportSettings.AuditQueueName);
        }

        [Fact]
        public void ShouldSetupHeartbeatQueueName()
        {
            // Arrange
            var bus = Bus.Initialize(c =>
            {
                c.SetHeartbeatQueueName("TestHeartbeatQueue");
                c.SetContainer<FakeContainer>();
                c.SetProducer<FakePublisher>();
            });

            // Act
            var config = bus.Configuration;

            // Assert
            Assert.Equal("TestHeartbeatQueue", config.TransportSettings.HeartbeatQueueName);
        }

        [Fact]
        public void ShouldSetupAuditingEnabled()
        {
            // Arrange
            var bus = Bus.Initialize(c =>
            {
                c.SetAuditingEnabled(true);
                c.SetContainer<FakeContainer>();
                c.SetProducer<FakePublisher>();
            });

            // Act
            var config = bus.Configuration;

            // Assert
            Assert.True(config.TransportSettings.AuditingEnabled);
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

            public void AddHandler<T>(Type handlerType, T handler)
            {
               
            }

            public bool Initialized { get; set; }
        }

        public class FakeConsumer : IConsumer
        {
            public FakeConsumer(ITransportSettings transportSettings)
            {
            }

            public void StartConsuming(ConsumerEventHandler messageReceived, string routingKey, string queueName = null, bool? exclusive = null, bool? autoDelete = null)
            {
                throw new NotImplementedException();
            }

            public void StartConsuming(ConsumerEventHandler messageReceived, string queueName, bool? exclusive = null,
                bool? autoDelete = null)
            {
            }

            public void StopConsuming()
            {
                throw new NotImplementedException();
            }

            public void Dispose()
            {
                throw new NotImplementedException();
            }

            public void ConsumeMessageType(string messageTypeName)
            {
            }

            public string Type { get; private set; }
        }

        public class FakePublisher : IProducer
        {
            public FakePublisher(ITransportSettings transportSettings, IDictionary<string, IList<string>> queueMappings)
            {
            }

            public void Publish<T>(T message) where T : Message
            {
                
            }

            public void Send<T>(T message) where T : Message
            {
               
            }

            public void Send<T>(string endPoint, T message) where T : Message
            {
               
            }

            public void Publish<T>(T message, Dictionary<string, string> headers = null) where T : Message
            {
               
            }

            public void Send<T>(T message, Dictionary<string, string> headers = null) where T : Message
            {
               
            }

            public void Send<T>(string endPoint, T message, Dictionary<string, string> headers = null) where T : Message
            {
                
            }

            public void Disconnect()
            {
                
            }

            public void Dispose()
            {
            }

            public string Type { get; private set; }

            public long MaximumMessageSize
            {
                get { throw new NotImplementedException(); }
                set { throw new NotImplementedException(); }
            }

            public void SendBytes(string endPoint, byte[] packet, Dictionary<string, string> headers)
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

            public IPersistanceData<T> FindData<T>(IProcessManagerPropertyMapper mapper, Message message) where T : class, IProcessManagerData
            {
                throw new NotImplementedException();
            }

            public void InsertData(IProcessManagerData data)
            {
                
            }

            public void UpdateData<T>(IPersistanceData<T> data) where T : class, IProcessManagerData
            {
               
            }

            public void DeleteData<T>(IPersistanceData<T> data) where T : class, IProcessManagerData
            {
               
            }
        }

    }
}