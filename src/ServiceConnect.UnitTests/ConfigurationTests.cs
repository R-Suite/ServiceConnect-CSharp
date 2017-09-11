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
using System.Linq;
using System.Reflection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistance.SqlServer;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests
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
            //Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().ProcessName, configuration.TransportSettings.QueueName);
            Assert.Equal(Assembly.GetEntryAssembly().GetName().Name, configuration.TransportSettings.QueueName);
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

        [Fact]
        public void ShouldGetContainerOfSpecifiedType()
        {
            // Arrange
            var configuration = new Configuration();
            configuration.SetContainerType<FakeContainer>();

            // Act
            var result = configuration.GetContainer();

            // Assert
            Assert.IsType<FakeContainer>(result);
        }

        public class FakeContainer : IBusContainer
        {
            public IEnumerable<HandlerReference> GetHandlerTypes()
            {
                throw new NotImplementedException();
            }

            public IEnumerable<HandlerReference> GetHandlerTypes(params Type[] messageHandler)
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

            public void Initialize(object container)
            {
                throw new NotImplementedException();
            }

            public void AddBus(IBus bus)
            {
                throw new NotImplementedException();
            }

            public object GetContainer()
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
            public FakeConsumer()
            {}

            public void Dispose()
            {
                throw new NotImplementedException();
            }

            public void StartConsuming(string queueName, IList<string> messageTypes, ConsumerEventHandler eventHandler, IConfiguration config)
            {
                throw new NotImplementedException();
            }
        }
    }
}