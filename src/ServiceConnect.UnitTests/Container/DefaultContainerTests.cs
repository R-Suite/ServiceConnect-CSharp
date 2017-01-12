using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace ServiceConnect.UnitTests.Container
{
    public class DefaultContainerTests
    {
        public interface IMyInterface<T>
        {
            string Name { get; set; }
            void Test();
        }

        public class MyImplementation : IMyInterface<string>
        {
            public string Name { get; set; }
            public void Test()
            {}
        }

        public class MyImplementationWithCtor : IMyInterface<string>
        {
            public MyImplementationWithCtor(string name)
            {
                Name = name;
            }
            public string Name { get; set; }
            public void Test()
            { }
        }

        public class MyGenericImplementation<T> : IMyInterface<T>
        {
            public string Name { get; set; }
            public void Test()
            {}
        }

        [Fact]
        public void ShouldResolveExplicitelyRegisteredType()
        {
            // Arrange
            var services = new ServiceConnect.Container.Default.Container();
            services.RegisterFor(typeof (MyImplementation), typeof (IMyInterface<string>));

            // Act
            var result = services.Resolve<IMyInterface<string>>();

            // Assert
            Assert.NotNull(result);
            Assert.Equal("MyImplementation", result.GetType().Name);
        }

        [Fact]
        public void ShouldResolveImplicitelyRegisteredType()
        {
            // Arrange
            var services = new ServiceConnect.Container.Default.Container();
            services.RegisterForAll(typeof(MyImplementation));

            // Act
            var result = services.Resolve<IMyInterface<string>>();

            // Assert
            Assert.NotNull(result);
            Assert.Equal("MyImplementation", result.GetType().Name);
        }

        [Fact]
        public void ShouldResolveImplicitelyRegisteredGenericTypes()
        {
            // Arrange
            var services = new ServiceConnect.Container.Default.Container();
            services.RegisterForAll(typeof(MyGenericImplementation<>));

            // Act
            var result1 = services.Resolve<IMyInterface<int>>();
            var result2 = services.Resolve<IMyInterface<string>>();

            // Assert
            Assert.NotNull(result1);
            Assert.True(result1.GetType().GetTypeInfo().IsGenericType);
            Assert.Equal(1, result1.GetType().GetGenericArguments().Count());
            Assert.Equal("Int32", result1.GetType().GetGenericArguments()[0].Name);

            Assert.NotNull(result2);
            Assert.True(result2.GetType().GetTypeInfo().IsGenericType);
            Assert.Equal(1, result2.GetType().GetTypeInfo().GetGenericArguments().Count());
            Assert.Equal("String", result2.GetType().GetGenericArguments()[0].Name);
        }

        [Fact]
        public void ShouldResolveRegisteredInstances()
        {
            // Arrange
            IMyInterface<string> myInstance = new MyGenericImplementation<string>();
            myInstance.Name = "test";
            var services = new ServiceConnect.Container.Default.Container();
            services.RegisterFor(myInstance, typeof(IMyInterface<string>));

            // Act
            var result = services.Resolve(typeof(IMyInterface<string>));

            // Assert
            Assert.NotNull(result);
            Assert.Equal("test", ((IMyInterface<string>)result).Name);
        }

        [Fact]
        public void ShouldResolveRegisteredTypeWithCtorParams()
        {
            // Arrange
            var services = new ServiceConnect.Container.Default.Container();
            services.RegisterFor(typeof(MyImplementationWithCtor), typeof(IMyInterface<string>));

            // Act
            var result = services.Resolve(typeof(IMyInterface<string>), new Dictionary<string, object> { {"name", "testName"} });

            // Assert
            Assert.NotNull(result);
            Assert.Equal("testName", ((IMyInterface<string>)result).Name);
        }
    }
}
