//Copyright (C) 2015  Timothy Watson, Jakub Pachansky

//This program is free software; you can redistribute it and/or
//modify it under the terms of the GNU General Public License
//as published by the Free Software Foundation; either version 2
//of the License, or (at your option) any later version.

//This program is distributed in the hope that it will be useful,
//but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//GNU General Public License for more details.

//You should have received a copy of the GNU General Public License
//along with this program; if not, write to the Free Software
//Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

using System;
using System.Collections.Generic;
using Moq;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;
using System.Linq;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests
{
    public class BusSetupTests
    {
        [Fact]
        public void ShouldSetupBusWithCorrectCustomDatabaseNameAndConnectionString()
        {
            // Arrange
            IBus bus = ServiceConnect.Bus.Initialize(config =>
            {
                config.SetProcessManagerFinder<FakeProcessManagerFinder>();
                config.SetContainerType<FakeContainer>();
                config.SetProducer<FakePublisher>();
                config.PersistenceStoreDatabaseName = "TestDatabaseName";
                config.PersistenceStoreConnectionString = "TestConnectionString";
                config.AutoStartConsuming = false;
                config.ScanForMesssageHandlers = false;
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
            IBus bus = ServiceConnect.Bus.Initialize(config =>
            {
                config.SetProcessManagerFinder<FakeProcessManagerFinder>();
                config.SetContainerType<FakeContainer>();
                config.SetProducer<FakePublisher>();
                config.AutoStartConsuming = false;
                config.ScanForMesssageHandlers = false;
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
            IBus bus = ServiceConnect.Bus.Initialize(config =>
            {
                config.SetProcessManagerFinder<FakeProcessManagerFinder>();
                config.SetContainerType<FakeContainer>();
                config.SetProducer<FakePublisher>();
                config.ScanForMesssageHandlers = true;
                config.AutoStartConsuming = false;
            });

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.Equal(true, configuration.ScanForMesssageHandlers);
        }

        //[Fact]
        //public void ShouldSetupBusWithCustomContainer()
        //{
        //    // Arrange
        //    IBus bus = Bus.Initialize(config =>
        //    {
        //        config.SetContainerType<FakeContainer>();
        //        config.SetProducer<FakePublisher>();
        //        config.AutoStartConsuming = false;
        //        config.ScanForMesssageHandlers = false;
        //    });

        //    // Act
        //    IConfiguration configuration = bus.Configuration;

        //    // Assert
        //    Assert.Equal(typeof(FakeContainer), configuration.ContainerType);
        //}

        [Fact]
        public void ShouldSetupBusWithCustomConsumer()
        {
            // Arrange
            IBus bus = ServiceConnect.Bus.Initialize(config =>
            {
                config.SetConsumer<FakeConsumer>();
                config.SetProducer<FakePublisher>();
                config.AutoStartConsuming = false;
                config.ScanForMesssageHandlers = false;
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
            IBus bus = ServiceConnect.Bus.Initialize(config =>
            {
                config.SetProducer<FakePublisher>();
                config.SetContainerType<FakeContainer>();
                config.AutoStartConsuming = false;
                config.ScanForMesssageHandlers = false;
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
            IBus bus = ServiceConnect.Bus.Initialize(config =>
            {
                config.SetProcessManagerFinder<FakeProcessManagerFinder>();
                config.SetContainerType<FakeContainer>();
                config.SetProducer<FakePublisher>();
                config.AutoStartConsuming = false;
                config.ScanForMesssageHandlers = false;
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
            mockConfiguration.SetupGet(x => x.TransportSettings).Returns(new TransportSettings {});

            // Act
            new ServiceConnect.Bus(mockConfiguration.Object);

            // Assert
            mockContainer.Verify(x => x.Initialize(), Times.Once);
        }

        [Fact]
        public void ShouldAddMessageMappingsToConfiguration()
        {
            // Arrange
            var bus = ServiceConnect.Bus.Initialize(conf =>
            {
                conf.AddQueueMapping(typeof(FakeMessage1), "MyEndPoint1");
                conf.AddQueueMapping(typeof(FakeMessage2), "MyEndPoint2");
                conf.SetContainerType<FakeContainer>();
                conf.SetProducer<FakePublisher>();
                conf.AutoStartConsuming = false;
                conf.ScanForMesssageHandlers = false;
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
            var bus = ServiceConnect.Bus.Initialize(c =>
            {
                c.SetQueueName("TestQueue");
                c.SetContainerType<FakeContainer>();
                c.SetProducer<FakePublisher>();
                c.AutoStartConsuming = false;
                c.ScanForMesssageHandlers = false;
            });

            // Act
            var config = bus.Configuration;

            // Assert
            Assert.Equal("TestQueue", config.TransportSettings.QueueName);
        }

        [Fact]
        public void ShouldSetupErrorQueueName()
        {
            // Arrange
            var bus = ServiceConnect.Bus.Initialize(c =>
            {
                c.SetErrorQueueName("TestErrorQueue");
                c.SetContainerType<FakeContainer>();
                c.SetProducer<FakePublisher>();
                c.AutoStartConsuming = false;
                c.ScanForMesssageHandlers = false;
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
            var bus = ServiceConnect.Bus.Initialize(c =>
            {
                c.SetAuditQueueName("TestAuditQueue");
                c.SetContainerType<FakeContainer>();
                c.SetProducer<FakePublisher>();
                c.AutoStartConsuming = false;
                c.ScanForMesssageHandlers = false;
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
            var bus = ServiceConnect.Bus.Initialize(c =>
            {
                c.SetHeartbeatQueueName("TestHeartbeatQueue");
                c.SetContainerType<FakeContainer>();
                c.SetProducer<FakePublisher>();
                c.AutoStartConsuming = false;
                c.ScanForMesssageHandlers = false;
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
            var bus = ServiceConnect.Bus.Initialize(c =>
            {
                c.SetAuditingEnabled(true);
                c.SetContainerType<FakeContainer>();
                c.SetProducer<FakePublisher>();
                c.AutoStartConsuming = false;
                c.ScanForMesssageHandlers = false;
            });

            // Act
            var config = bus.Configuration;

            // Assert
            Assert.True(config.TransportSettings.AuditingEnabled);
        }

        [Fact]
        public void ShouldPurgeQueuesOnStartup()
        {
            // Arrange
            var bus = ServiceConnect.Bus.Initialize(c =>
            {
                c.SetContainerType<FakeContainer>();
                c.SetProducer<FakePublisher>();
                c.SetConsumer<FakeConsumer>();
                c.PurgeQueuesOnStart();
            });

            // Act
            var config = bus.Configuration;

            // Assert
            Assert.True(config.TransportSettings.PurgeQueueOnStartup);
        }

        [Fact]
        public void ShouldScanForMessageHandlersByDefault()
        {
            // Arrange
            var bus = ServiceConnect.Bus.Initialize(c =>
            {
                c.SetContainerType<FakeContainer>();
                c.SetProducer<FakePublisher>();
                c.AutoStartConsuming = false;
            });

            // Act
            var config = bus.Configuration;

            // Assert
            Assert.True(config.ScanForMesssageHandlers);
        }

        public class FakeContainer : IBusContainer
        {
            public IEnumerable<HandlerReference> GetHandlerTypes()
            {
                return new List<HandlerReference>();
            }

            public IEnumerable<HandlerReference> GetHandlerTypes(Type messageHandler)
            {
                return new List<HandlerReference>();
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

            public void Initialize(object container)
            {
                throw new NotImplementedException();
            }

            public void AddBus(IBus bus)
            {
            }

            public object GetContainer()
            {
                throw new NotImplementedException();
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

            public void ConsumeMessageType(string messageTypeName)
            {
                throw new NotImplementedException();
            }

            public void Dispose()
            {
                throw new NotImplementedException();
            }

            public string Type { get; private set; }
        }

        public class FakePublisher : IProducer
        {
            public FakePublisher(ITransportSettings transportSettings, IDictionary<string, IList<string>> queueMappings)
            {
            }
            public void Dispose()
            {
                
            }

            public void Publish(Type type, byte[] message, Dictionary<string, string> headers = null)
            {
            }

            public void Publish(Type type, byte[] message, string routingKey, Dictionary<string, string> headers = null)
            {
            }

            public void Send(Type type, byte[] message, Dictionary<string, string> headers = null)
            {
            }

            public void Send(string endPoint, Type type, byte[] message, Dictionary<string, string> headers = null)
            {
            }

            public void Disconnect()
            {
            }

            public string Type { get; private set; }
            public long MaximumMessageSize { get; private set; }
            public void SendBytes(string endPoint, byte[] packet, Dictionary<string, string> headers)
            {
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

            public event TimeoutInsertedDelegate TimeoutInserted;

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

            public void InsertTimeout(TimeoutData timeoutData)
            {
                throw new NotImplementedException();
            }

            public TimeoutsBatch GetTimeoutsBatch()
            {
                throw new NotImplementedException();
            }

            public void RemoveDispatchedTimeout(Guid id)
            {
                throw new NotImplementedException();
            }
        }
    }
}