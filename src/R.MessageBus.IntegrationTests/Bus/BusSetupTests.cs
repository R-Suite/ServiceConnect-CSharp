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

using System.Threading;
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
            bus.Dispose();

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
            bus.Dispose();

            // Assert
            Assert.Equal("TestDatabase", configuration.PersistenceStoreDatabaseName);
            Assert.Equal(2, configuration.TransportSettings.MaxRetries);
            Assert.Equal(2000, configuration.TransportSettings.RetryDelay);
            Assert.Equal("TestQueue1", configuration.TransportSettings.QueueName);
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
            bus.Dispose();

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
            bus.Dispose();

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
            bus.Dispose();

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
            bus.Dispose();

            // Assert
            Assert.False(configuration.TransportSettings.AuditingEnabled);
        }
    }
}
