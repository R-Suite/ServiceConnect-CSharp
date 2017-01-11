using System;
using System.Collections.Generic;
using System.Linq;
using Ninject;
using ServiceConnect.Container.Ninject;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.Container
{
    public class NinjectBusContainerTests
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
        public void ShouldInitialzeStandardKernelWithInternalTypes()
        {
            // Arrange
            var ninjectContainer = new NinjectContainer();
            var kernel = new StandardKernel();

            // Act
            ninjectContainer.Initialize(kernel);

            // Assert
            Assert.NotNull(kernel.GetBindings(typeof(IMessageHandlerProcessor)));
            Assert.NotNull(kernel.GetBindings(typeof(IAggregatorProcessor)));
            Assert.NotNull(kernel.GetBindings(typeof(IProcessManagerProcessor)));
            Assert.NotNull(kernel.GetBindings(typeof(IStreamProcessor)));
            Assert.NotNull(kernel.GetBindings(typeof(IProcessManagerPropertyMapper)));
        }

        [Fact]
        public void ShouldGetAllHandlerReferences()
        {
            // Arrange
            var kernel = new StandardKernel();
            kernel.Bind<IMessageHandler<MyMessage>>().To(typeof(MyMessageHandler));
            var busContainer = new NinjectContainer();
            busContainer.Initialize(kernel);

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
            var kernel = new StandardKernel();
            kernel.Bind<IMessageHandler<MyMessage>>().To(typeof(MyMessageHandler3));
            var busContainer = new NinjectContainer();
            busContainer.Initialize(kernel);

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
            var kernel = new StandardKernel();
            kernel.Bind<IMessageHandler<MyMessage>>().To(typeof(MyMessageHandler));
            var busContainer = new NinjectContainer();
            busContainer.Initialize(kernel);

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
            var kernel = new StandardKernel();
            kernel.Bind<IMessageHandler<MyMessage>>().To(typeof(MyMessageHandler3));
            var busContainer = new NinjectContainer();
            busContainer.Initialize(kernel);

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
            var kernel = new StandardKernel();
            kernel.Bind<IMessageHandler<MyMessage>>().To(typeof(MyMessageHandler));
            var busContainer = new NinjectContainer();
            busContainer.Initialize(kernel);

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
            var kernel = new StandardKernel();
            kernel.Bind<IMessageHandler<MyMessage>>().To(typeof(MyMessageHandler2));
            var busContainer = new NinjectContainer();
            busContainer.Initialize(kernel);

            // Act
            var result = busContainer.GetInstance<IMessageHandler<MyMessage>>(new Dictionary<string, object> { { "name", "TestName" } });

            // Assert
            Assert.NotNull(result);
            Assert.Equal("MyMessageHandler2", result.GetType().Name);
        }

        [Fact]
        public void ShouldGetTypedInstanceOfRegisteredType()
        {
            // Arrange
            var kernel = new StandardKernel();
            kernel.Bind<IMessageHandler<MyMessage>>().To(typeof(MyMessageHandler));
            var busContainer = new NinjectContainer();
            busContainer.Initialize(kernel);

            // Act
            var result = busContainer.GetInstance<IMessageHandler<MyMessage>>();

            // Assert
            Assert.NotNull(result);
            Assert.Equal("MyMessageHandler", result.GetType().Name);
        }
    }
}
