using System;
using System.Collections.Generic;
using R.MessageBus.Client.RabbitMQ;
using R.MessageBus.Container;
using R.MessageBus.Interfaces;
using R.MessageBus.Persistance.MongoDb;
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
            Assert.Equal("host=localhost", configuration.PersistenceStoreConnectionString);
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

        //[Fact]
        //public void ShouldPassConfigurationPathAndEndpointToConsumer()
        //{
        //    // Arrange
        //    var configuration = new Configuration {EndPoint = "MyEndpoint", ConfigurationPath = "MyConfig"};
        //    configuration.SetConsumer<FakeConsumer>();

        //    // Act
        //    var consumer = (FakeConsumer)configuration.GetConsumer();

        //    // Assert
        //    Assert.Equal("MyEndpoint", consumer.EndPoint);
        //    Assert.Equal("MyConfig", consumer.ConfigPath);
        //}

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

            public object GetHandlerInstance(Type handlerType)
            {
                throw new NotImplementedException();
            }

            public void ScanForHandlers()
            {
                throw new NotImplementedException();
            }
        }

        public class FakeConsumer : IConsumer
        {
            private readonly ITransportSettings _transportSettings;

            public FakeConsumer(ITransportSettings transportSettings)
            {
                _transportSettings = transportSettings;
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
    }
}