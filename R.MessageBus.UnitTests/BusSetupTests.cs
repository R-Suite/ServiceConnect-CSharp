using System;
using System.Collections.Generic;
using R.MessageBus.Client.RabbitMQ;
using R.MessageBus.Container;
using R.MessageBus.Interfaces;
using Xunit;

namespace R.MessageBus.UnitTests
{
    public class BusSetupTests
    {
        [Fact]
        public void ShouldSetupBusWithDefaultConfiguration()
        {
            // Arrange
            IBus bus = Bus.Initialize();

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.Equal(typeof(Consumer), configuration.ConsumerType);
            Assert.Equal(AppDomain.CurrentDomain.SetupInformation.ConfigurationFile, configuration.ConfigurationPath);
            Assert.Equal(typeof(StructuremapContainer), configuration.Container);
            Assert.Equal(null, configuration.EndPoint);
        }

        [Fact]
        public void ShouldSetupBusWithCustomConsumer()
        {
            // Arrange
            IBus bus = Bus.Initialize(config => config.SetConsumer<FakeConsumer>());

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.Equal(typeof(FakeConsumer), configuration.ConsumerType);
        }

        [Fact]
        public void ShouldSetupBusWithCustomEndPoint()
        {
            // Arrange
            IBus bus = Bus.Initialize(config =>
            {
                config.EndPoint = "MyEndpoint";
            });

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.Equal("MyEndpoint", configuration.EndPoint);
        }

        [Fact]
        public void ShouldSetupBusWithCustomConfigurationPath()
        {
            // Arrange
            IBus bus = Bus.Initialize(config =>
            {
                config.ConfigurationPath = "MyConfigurationPath";
            });

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.Equal("MyConfigurationPath", configuration.ConfigurationPath);
        }

        [Fact]
        public void ShouldSetupBusToScanForAllHandlers()
        {
            // Arrange
            IBus bus = Bus.Initialize(config =>
            {
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
            IBus bus = Bus.Initialize(config => config.SetContainer<FakeContainer>());

            // Act
            IConfiguration configuration = bus.Configuration;

            // Assert
            Assert.Equal(typeof(FakeContainer), configuration.Container);
        }

        [Fact]
        public void ShouldBuildCustomConsumerWithCorrectEndpointAndConfigPath()
        {
            // Arrange
            IBus bus = Bus.Initialize(config =>
            {
                config.EndPoint = "MyEndpoint";
                config.ConfigurationPath = "MyConfigurationPath";
                config.SetConsumer<FakeConsumer>();
                config.SetContainer<FakeContainer>();
            });
            IConfiguration configuration = bus.Configuration;

            // Act
            IConsumer consumer = configuration.GetConsumer();

            // Assert
            Assert.Equal(typeof(FakeConsumer), consumer.GetType());
            var fakeConsumer = (FakeConsumer) consumer;
            Assert.Equal("MyEndpoint", fakeConsumer.EndPoint);
            Assert.Equal("MyConfigurationPath", fakeConsumer.ConfigPath);
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
            public string ConfigPath { get; private set; }
            public string EndPoint { get; private set; }

            public FakeConsumer(string endPoint, string configPath)
            {
                ConfigPath = configPath;
                EndPoint = endPoint;
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