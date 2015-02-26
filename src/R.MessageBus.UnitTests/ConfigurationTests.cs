using System;
using System.Collections.Generic;
using System.Linq;
using R.MessageBus.Client.RabbitMQ;
using R.MessageBus.Container;
using R.MessageBus.Interfaces;
using R.MessageBus.Persistance.SqlServer;
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
            Assert.Equal(typeof(SqlServerProcessManagerFinder), configuration.ProcessManagerFinder);
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
            Assert.NotNull(configuration.TransportSettings.ClientSettings);
            Assert.Equal("localhost", configuration.TransportSettings.Host);
            Assert.Equal(3, configuration.TransportSettings.MaxRetries);
            Assert.Equal(3000, configuration.TransportSettings.RetryDelay);
            Assert.Null(configuration.TransportSettings.Username);
            Assert.Null(configuration.TransportSettings.Password);
            Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().ProcessName, configuration.TransportSettings.QueueName);
            Assert.False(configuration.TransportSettings.AuditingEnabled);
            Assert.Equal("errors", configuration.TransportSettings.ErrorQueueName);
            Assert.Equal("audit", configuration.TransportSettings.AuditQueueName);
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
        public void ShouldSetHost()
        {
            // Arrange
            var configuration = new Configuration();
            configuration.SetHost("Host");

            // Act
            var result = configuration.TransportSettings.Host;

            // Assert
            Assert.Equal("Host", result);
        }

        [Fact]
        public void ShouldSetupErrorQueueName()
        {
            // Arrange
            var configuration = new Configuration();
            configuration.SetErrorQueueName("TestErrorQueueName");

            // Act
            var result = configuration.GetErrorQueueName();

            // Assert
            Assert.Equal("TestErrorQueueName", result);
        }

        [Fact]
        public void ShouldSetupAuditQueueName()
        {
            // Arrange
            var configuration = new Configuration();
            configuration.SetAuditQueueName("TestAuditQueueName");

            // Act
            var result = configuration.GetAuditQueueName();

            // Assert
            Assert.Equal("TestAuditQueueName", result);
        }

        [Fact]
        public void ShouldSetupAuditingEnabled()
        {
            // Arrange
            var configuration = new Configuration();
            configuration.SetAuditingEnabled(true);

            // Act
            var result = configuration.TransportSettings.AuditingEnabled;

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void ShouldAddMappingToEndPointMappings()
        {
            // Arrange
            var configuration = new Configuration();

            // Act
            configuration.AddQueueMapping(typeof(FakeMessage1), "MyEndPoint");

            // Assert
            Assert.True(configuration.QueueMappings.Any(x => x.Key == typeof(FakeMessage1).FullName && x.Value.Contains("MyEndPoint")));
        }

        [Fact]
        public void ShouldSetExceptionHandler()
        {
            // Arrange
            var configuration = new Configuration();
            Action<Exception> action = exception => { };

            // Act
            configuration.SetExceptionHandler(action);

            // Assert
            Assert.Equal(action, configuration.ExceptionHandler);
        }

        [Fact]
        public void ShouldSetPurgeQueuesOnStart()
        {
            // Arrange
            var configuration = new Configuration();

            // Act
            configuration.PurgeQueuesOnStart();

            // Assert
            Assert.Equal(true, configuration.TransportSettings.PurgeQueueOnStartup);
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
    }
}