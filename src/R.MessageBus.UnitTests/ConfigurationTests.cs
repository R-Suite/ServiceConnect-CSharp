using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using R.MessageBus.Client.RabbitMQ;
using R.MessageBus.Container;
using R.MessageBus.Interfaces;
using R.MessageBus.Persistance.MongoDb;
using R.MessageBus.UnitTests.Fakes.Messages;
using Xunit;

namespace R.MessageBus.UnitTests
{
    public class ConfigurationTests
    {
        [Fact]
        public void ShouldSetDefaultConfigurationWhenInstantiatingConfiguration()
        {
            // Act
            var configuration = new Configuration();

            // Assert
            Assert.Equal(typeof(Consumer), configuration.ConsumerType);
            Assert.Equal(typeof(StructuremapContainer), configuration.Container);
            Assert.Equal(typeof(MongoDbProcessManagerFinder), configuration.ProcessManagerFinder);
            Assert.Equal("RMessageBusPersistantStore", configuration.PersistenceStoreDatabaseName);
            Assert.Equal("mongodb://localhost/", configuration.PersistenceStoreConnectionString);
        }

        [Fact]
        public void ShouldSetDefaultTransportSettingsWhenInstantiatingConfiguration()
        {
            // Act
            var configuration = new Configuration();

            // Assert
            Assert.NotNull(configuration.TransportSettings);
            Assert.Equal("localhost", configuration.TransportSettings.Host);
            Assert.Equal(3, configuration.TransportSettings.MaxRetries);
            Assert.Equal(3000, configuration.TransportSettings.RetryDelay);
            Assert.Null(configuration.TransportSettings.Username);
            Assert.Null(configuration.TransportSettings.Password);
            Assert.False(configuration.TransportSettings.NoAck);
            Assert.NotNull(configuration.TransportSettings.Queue);
            Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().ProcessName, configuration.TransportSettings.Queue.Name);
            Assert.Null(configuration.TransportSettings.Queue.RoutingKey);
            Assert.Null(configuration.TransportSettings.Queue.Arguments);
            Assert.False(configuration.TransportSettings.Queue.AutoDelete);
            Assert.False(configuration.TransportSettings.Queue.Exclusive);
            Assert.False(configuration.TransportSettings.Queue.IsReadOnly);
            Assert.True(configuration.TransportSettings.Queue.Durable);
        }

        [Fact]
        public void ShouldCreateInstanceOfConsumer()
        {
            // Arrange
            var configuration = new Configuration();
            configuration.SetConsumer<FakeConsumer>();

            // Act
            IConsumer consumer = configuration.GetConsumer();

            // Assert
            Assert.Equal(typeof(FakeConsumer), consumer.GetType());
        }
        
        [Fact]
        public void ShouldCreateInstanceOfContainer()
        {
            // Arrange
            var configuration = new Configuration();
            configuration.SetContainer<FakeContainer>();

            // Act
            IBusContainer container = configuration.GetContainer();

            // Assert
            Assert.Equal(typeof(FakeContainer), container.GetType());
        }

        [Fact]
        public void ShouldSetupQueueName()
        {
            // Arrange
            var configuration = new Configuration();
            configuration.SetQueueName("TestQueueName");

            // Act
            var result = configuration.GetQueueName();

            // Assert
            Assert.Equal("TestQueueName", result);
        }

        [Fact]
        public void ShouldAddMappingToEndPointMappings()
        {
            // Arrange
            var configuration = new Configuration();

            // Act
            configuration.AddQueueMapping(typeof(FakeMessage1), "MyEndPoint");

            // Assert
            Assert.True(configuration.QueueMappings.Any(x => x.Key == typeof(FakeMessage1).FullName && x.Value == "MyEndPoint"));
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
            {
                throw new NotImplementedException();
            }

            public void Initialize()
            {
            }

            public void AddBus(IBus bus)
            {
                throw new NotImplementedException();
            }

            public void AddHandler<T>(Type handlerType, T handler)
            {
                throw new NotImplementedException();
            }
        }

        public class FakeConsumer : IConsumer
        {
            public FakeConsumer(ITransportSettings transportSettings)
            {}

            public void StartConsuming(ConsumerEventHandler messageReceived, string routingKey, string queueName = null, bool? exclusive = null)
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
    }
}