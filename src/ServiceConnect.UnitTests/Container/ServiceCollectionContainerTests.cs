using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Container.ServiceCollection;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.Container
{
    public class ServiceCollectionContainerTests
    {
        public class MyMessage : Message
        {
            public MyMessage(Guid correlationId)
                : base(correlationId)
            {
            }
        }

        public class MyMessageHandler : IMessageHandler<MyMessage>
        {
            public IConsumeContext Context { get; set; }
            public void Execute(MyMessage message)
            {
                throw new NotImplementedException();
            }
        }

        public class MyMessageHandler2 : IMessageHandler<MyMessage>
        {
            public MyMessageHandler2(string name)
            { }
            public IConsumeContext Context { get; set; }
            public void Execute(MyMessage message)
            {
                throw new NotImplementedException();
            }
        }

        [RoutingKey("key1")]
        public class MyMessageHandler3 : IMessageHandler<MyMessage>
        {
            public MyMessageHandler3()
            { }
            public IConsumeContext Context { get; set; }
            public void Execute(MyMessage message)
            {
                throw new NotImplementedException();
            }
        }

        [Fact]
        public void ShouldGetAllHandlerReferences()
        {
            // Arrange
            var busContainer = new ServiceCollectionContainer();
            busContainer.AddHandler(typeof(IMessageHandler<MyMessage>), new MyMessageHandler());

            // Act
            var result = busContainer.GetHandlerTypes();

            // Assert
            Assert.NotNull(result);
            Assert.Equal(1, result.Count());
            Assert.Equal("MyMessage", result.ToList()[0].MessageType.Name);
            Assert.Equal("MyMessageHandler", result.ToList()[0].HandlerType.Name);
        }

        [Fact]
        public void ShouldGetAllHandlerReferencesWithRoutingKey()
        {
            // Arrange
            var busContainer = new ServiceCollectionContainer();
            busContainer.AddHandler(typeof(IMessageHandler<MyMessage>), new MyMessageHandler3());

            // Act
            var result = busContainer.GetHandlerTypes();

            // Assert
            Assert.NotNull(result);
            Assert.Equal(1, result.Count());
            Assert.Equal("MyMessage", result.ToList()[0].MessageType.Name);
            Assert.Equal("MyMessageHandler3", result.ToList()[0].HandlerType.Name);
            Assert.Equal("key1", result.ToList()[0].RoutingKeys[0]);
        }

        [Fact]
        public void ShouldGetAllHandlerReferencesForMessageHandlerType()
        {
            // Arrange
            var busContainer = new ServiceCollectionContainer();
            busContainer.AddHandler(typeof(IMessageHandler<MyMessage>), new MyMessageHandler());

            // Act
            var result = busContainer.GetHandlerTypes(typeof(IMessageHandler<MyMessage>));

            // Assert
            Assert.NotNull(result);
            Assert.Equal(1, result.Count());
            Assert.Equal("MyMessage", result.ToList()[0].MessageType.Name);
            Assert.Equal("MyMessageHandler", result.ToList()[0].HandlerType.Name);
        }

        [Fact]
        public void ShouldGetAllHandlerReferencesForMessageHandlerTypeWithRoutingKey()
        {
            // Arrange
            var busContainer = new ServiceCollectionContainer();
            busContainer.AddHandler(typeof(IMessageHandler<MyMessage>), new MyMessageHandler3());

            // Act
            var result = busContainer.GetHandlerTypes(typeof(IMessageHandler<MyMessage>));

            // Assert
            Assert.NotNull(result);
            Assert.Equal(1, result.Count());
            Assert.Equal("MyMessage", result.ToList()[0].MessageType.Name);
            Assert.Equal("MyMessageHandler3", result.ToList()[0].HandlerType.Name);
            Assert.Equal("key1", result.ToList()[0].RoutingKeys[0]);
        }

        [Fact]
        public void ShouldGetInstanceOfRegisteredType()
        {
            // Arrange
            var busContainer = new ServiceCollectionContainer();
            busContainer.AddHandler(typeof(IMessageHandler<MyMessage>), new MyMessageHandler());


            // Act
            var result = busContainer.GetInstance(typeof(IMessageHandler<MyMessage>));

            // Assert
            Assert.NotNull(result);
            Assert.Equal("MyMessageHandler", result.GetType().Name);
        }

        [Fact]
        public void ShouldGetInstanceOfRegisteredTypeWithCtorParameters()
        {
            // Arrange
            var busContainer = new ServiceCollectionContainer();
            busContainer.AddHandler(typeof(IMessageHandler<MyMessage>), new MyMessageHandler2("TestName"));

            // Act
            var result = busContainer.GetInstance<IMessageHandler<MyMessage>>(new Dictionary<string, object> { { "name", "TestName" } });

            // Assert
            Assert.NotNull(result);
            Assert.Equal("MyMessageHandler2", result.GetType().Name);
        }

        [Fact]
        public void ShouldGetSameInstanceOfSingletonRegisteredType()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddSingleton<IMessageHandler<MyMessage>, MyMessageHandler>();

            var busContainer = new ServiceCollectionContainer();
            busContainer.Initialize(services);

            // Act
            var result1 = busContainer.GetInstance(typeof(IMessageHandler<MyMessage>));
            var result2 = busContainer.GetInstance(typeof(IMessageHandler<MyMessage>));

            // Assert
            Assert.NotNull(result1);
            Assert.NotNull(result2);
            Assert.Equal("MyMessageHandler", result1.GetType().Name);
            Assert.Equal("MyMessageHandler", result2.GetType().Name);
            Assert.Same(result1, result2);
        }

        [Fact]
        public void ShouldGetDifferentInstanceOfTransientRegisteredType()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddTransient<IMessageHandler<MyMessage>, MyMessageHandler>();

            var busContainer = new ServiceCollectionContainer();
            busContainer.Initialize(services);

            // Act
            var result1 = busContainer.GetInstance(typeof(IMessageHandler<MyMessage>));
            var result2 = busContainer.GetInstance(typeof(IMessageHandler<MyMessage>));

            // Assert
            Assert.NotNull(result1);
            Assert.NotNull(result2);
            Assert.Equal("MyMessageHandler", result1.GetType().Name);
            Assert.Equal("MyMessageHandler", result2.GetType().Name);
            Assert.NotSame(result1, result2);
        }
    }
}
