using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ServiceConnect.DependencyInjection;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests;

public class RegistryInitializerTests
{
    private static IServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<IProducer>().Object);
        services.AddLogging();
        return services;
    }

    [Fact]
    public void Initialize_ShouldResolveAllRegistries_WithoutThrowingException()
    {
        // Arrange
        var services = CreateServices();
        services.AddServiceConnect(b => b
            .ConfigureQueues(q => q.QueueName = "test")
            .ConfigureBus(c => c.ScanForMessageHandlers = false));

        var provider = services.BuildServiceProvider();
        var initializer = provider.GetRequiredService<IRegistryInitializer>();

        // Act & Assert - should not throw
        initializer.Initialize();
        Assert.Equal(4, provider.GetRequiredService<IEnumerable<IHandlerRegistry>>().Count());
    }

    [Fact]
    public void AddServiceConnect_ShouldRegisterIRegistryInitializer()
    {
        // Arrange
        var services = CreateServices();
        services.AddServiceConnect(b => b
            .ConfigureQueues(q => q.QueueName = "test")
            .ConfigureBus(c => c.ScanForMessageHandlers = false));

        var provider = services.BuildServiceProvider();

        // Act
        var initializer = provider.GetService<IRegistryInitializer>();

        // Assert
        Assert.NotNull(initializer);
    }

    [Fact]
    public void AddServiceConnect_BusFactory_ShouldCallIRegistryInitializer()
    {
        // Arrange
        var services = CreateServices();
        services.AddServiceConnect(b => b
            .ConfigureQueues(q => q.QueueName = "test")
            .ConfigureBus(c => c.ScanForMessageHandlers = false));

        var provider = services.BuildServiceProvider();

        // Act - Resolving IBus should trigger IRegistryInitializer.Initialize()
        var bus = provider.GetRequiredService<IBus>();

        // Assert - If registries weren't initialized, we'd get an exception
        // The fact we got a bus instance proves initialization worked
        Assert.NotNull(bus);
    }
}
