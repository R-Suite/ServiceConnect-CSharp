using R.MessageBus.Client.RabbitMQ;
using R.MessageBus.Container;
using R.MessageBus.Interfaces;
using R.MessageBus.Persistance.SqlServer;
using Xunit;

namespace R.MessageBus.IntegrationTests.Bus
{
    public class BusSetupTests
    {
        [Fact]
        public void ShouldSetupBusWithDefaultConfiguration()
        {
            // Arrange
            IBus bus = MessageBus.Bus.Initialize();

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.Equal(typeof(Consumer), configuration.ConsumerType);
            Assert.Equal(typeof(Producer), configuration.ProducerType);
            Assert.Equal(typeof(StructuremapContainer), configuration.Container);
            Assert.Equal(typeof(SqlServerProcessManagerFinder), configuration.ProcessManagerFinder);
        }

        [Fact]
        public void ShouldSetupBusWithCustomConfigurationFile()
        {
            // Arrange
            IBus bus = MessageBus.Bus.Initialize(config => config.LoadSettings(@"Bus/TestConfiguration.xml", "TestEndPoint2"));

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.Equal("TestDatabase", configuration.PersistenceStoreDatabaseName);
            Assert.Equal(2, configuration.TransportSettings.MaxRetries);
            Assert.Equal(2000, configuration.TransportSettings.RetryDelay);
            Assert.Equal("TestQueue1", configuration.TransportSettings.Queue.Name);
            Assert.Equal("TestQueueRoutingKey1", configuration.TransportSettings.Queue.RoutingKey);
            Assert.True(configuration.TransportSettings.Queue.Durable);
            Assert.True(configuration.TransportSettings.Queue.AutoDelete);
            Assert.True(configuration.TransportSettings.Queue.Exclusive);
            Assert.True(configuration.TransportSettings.AuditingEnabled);
            Assert.Equal("TestAuditQueue", configuration.TransportSettings.AuditQueueName);
            Assert.Equal("TestErrorQueue", configuration.TransportSettings.ErrorQueueName);
        }

        [Fact]
        public void ShouldSetupBusWithCustomDatabaseNameOverridingConfigFileSetting()
        {
            // Arrange
            IBus bus = MessageBus.Bus.Initialize(config =>
            {
                config.LoadSettings(@"Bus/TestConfiguration.xml", "TestEndPoint2");
                config.PersistenceStoreDatabaseName = "NewDatabase";
            });

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.Equal("NewDatabase", configuration.PersistenceStoreDatabaseName);
        }

        [Fact]
        public void ShouldSetupBusWithCustomErrorQueueNameOverridingConfigFileSetting()
        {
            // Arrange
            IBus bus = MessageBus.Bus.Initialize(config =>
            {
                config.LoadSettings(@"Bus/TestConfiguration.xml", "TestEndPoint2");
                config.SetErrorQueueName("NewErrorQueue");
            });

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.Equal("NewErrorQueue", configuration.TransportSettings.ErrorQueueName);
        }

        [Fact]
        public void ShouldSetupBusWithCustomAuditQueueNameOverridingConfigFileSetting()
        {
            // Arrange
            IBus bus = MessageBus.Bus.Initialize(config =>
            {
                config.LoadSettings(@"Bus/TestConfiguration.xml", "TestEndPoint2");
                config.SetAuditQueueName("NewAuditQueue");
            });

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.Equal("NewAuditQueue", configuration.TransportSettings.AuditQueueName);
        }

        [Fact]
        public void ShouldSetupBusAuditingDisabledOverridingConfigFileSetting()
        {
            // Arrange
            IBus bus = MessageBus.Bus.Initialize(config =>
            {
                config.LoadSettings(@"Bus/TestConfiguration.xml", "TestEndPoint2");
                config.SetAuditingEnabled(false);
            });

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.False(configuration.TransportSettings.AuditingEnabled);
        }
    }
}
