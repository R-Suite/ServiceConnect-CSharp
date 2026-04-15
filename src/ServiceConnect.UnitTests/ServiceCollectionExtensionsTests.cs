using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System.Reflection;
using ServiceConnect;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests;

public class ServiceCollectionExtensionsTests
{
    private static IServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        // SendMessagePipeline depends on IProducer
        services.AddSingleton(new Mock<IProducer>().Object);
        // Bus depends on ILogger<Bus>
        services.AddLogging();
        return services;
    }

    [Fact]
    public void AddServiceConnect_RegistersIBus()
    {
        var services = CreateServices();

        services.AddServiceConnect(b => b
            .ConfigureQueues(q => q.QueueName = "test")
            .ConfigureBus(c => c.ScanForMessageHandlers = false));

        var provider = services.BuildServiceProvider();
        var bus = provider.GetService<IBus>();

        Assert.NotNull(bus);
    }

    [Fact]
    public void AddServiceConnect_InvokesBuilderCallback()
    {
        var services = CreateServices();
        var callbackInvoked = false;

        services.AddServiceConnect(b =>
        {
            callbackInvoked = true;
            b.ConfigureQueues(q => q.QueueName = "callback-queue");
            b.ConfigureBus(c => c.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var queueConfig = provider.GetRequiredService<ServiceConnect.Interfaces.Configuration.IQueueConfiguration>();

        Assert.True(callbackInvoked);
        Assert.Equal("callback-queue", queueConfig.QueueName);
    }

    [Fact]
    public void AddServiceConnect_RegistersCoreServices()
    {
        var services = CreateServices();

        services.AddServiceConnect(b => b
            .ConfigureQueues(q => q.QueueName = "test")
            .ConfigureBus(c => c.ScanForMessageHandlers = false));

        var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IMessageSerializer>());
        Assert.NotNull(provider.GetService<IFilterPipeline>());
        Assert.NotNull(provider.GetService<IRequestReplyManager>());
        Assert.NotNull(provider.GetService<ISendMessagePipeline>());
    }

    [Fact]
    public void ServiceCollectionExtensions_DefinesRegistrationHelpers()
    {
        string[] expectedHelpers =
        [
            "RegisterConfiguration",
            "RegisterCoreServices",
            "RegisterProcessors",
            "RegisterHandlers",
            "RegisterBus"
        ];

        foreach (var helper in expectedHelpers)
        {
            var method = typeof(ServiceCollectionExtensions).GetMethod(
                helper,
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.NotNull(method);
            Assert.Equal(typeof(void), method!.ReturnType);
        }
    }

    [Fact]
    public void AddServiceConnect_ThrowsWhenSendMiddlewareIsNotSingleton()
    {
        var services = CreateServices();
        services.AddTransient<TestSendMiddleware>();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddServiceConnect(b => b
                .ConfigureQueues(q => q.QueueName = "test")
                .ConfigureBus(c => c.ScanForMessageHandlers = false)
                .AddSendMessageMiddleware<TestSendMiddleware>()));

        Assert.Contains(nameof(TestSendMiddleware), exception.Message);
        Assert.Contains("singleton", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}

file sealed class TestSendMiddleware : ISendMessageMiddleware
{
    public Task Process(
        Type messageType,
        byte[] messageBytes,
        Dictionary<string, string> headers,
        string? endPoint,
        SendMessageDelegate next,
        CancellationToken cancellationToken = default) =>
        next(messageType, messageBytes, headers, endPoint, cancellationToken);
}
