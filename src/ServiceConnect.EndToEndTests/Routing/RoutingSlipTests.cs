using Microsoft.Extensions.DependencyInjection;
using Moq;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

public class RoutingSlipTests
{
    [Fact]
    public async Task RouteAsync_SetsRoutingSlipHeaders()
    {
        string? capturedEndpoint = null;
        IDictionary<string, string>? capturedHeaders = null;

        var mockProducer = new Mock<IProducer>();
        mockProducer
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, Type, byte[], IDictionary<string, string>, CancellationToken>((ep, t, b, h, ct) =>
            {
                capturedEndpoint = ep;
                capturedHeaders = h;
            })
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProducer>(mockProducer.Object);
        services.AddServiceConnect(builder =>
        {
            builder.ConfigureQueues(q => q.QueueName = "routing-slip-test");
            builder.ConfigureBus(c => c.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        var message = new StepMessage(Guid.NewGuid()) { CurrentStep = "Start" };

        await bus.RouteAsync(message, ["Step1", "Step2", "Step3"]);

        Assert.Equal("Step1", capturedEndpoint);
        Assert.NotNull(capturedHeaders);
        Assert.True(capturedHeaders.ContainsKey("RoutingSlip"));
        Assert.Equal("Step2,Step3", capturedHeaders["RoutingSlip"]);
    }

    [Fact]
    public async Task RouteAsync_SingleDestination_NoRoutingSlipHeader()
    {
        string? capturedEndpoint = null;
        IDictionary<string, string>? capturedHeaders = null;

        var mockProducer = new Mock<IProducer>();
        mockProducer
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, Type, byte[], IDictionary<string, string>, CancellationToken>((ep, t, b, h, ct) =>
            {
                capturedEndpoint = ep;
                capturedHeaders = h;
            })
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProducer>(mockProducer.Object);
        services.AddServiceConnect(builder =>
        {
            builder.ConfigureQueues(q => q.QueueName = "routing-slip-single-test");
            builder.ConfigureBus(c => c.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        var message = new StepMessage(Guid.NewGuid()) { CurrentStep = "Start" };

        await bus.RouteAsync(message, ["OnlyDest"]);

        Assert.Equal("OnlyDest", capturedEndpoint);
        Assert.NotNull(capturedHeaders);
        Assert.False(capturedHeaders.ContainsKey("RoutingSlip"));
    }

    [Fact]
    public async Task RouteAsync_EmptyDestinations_Throws()
    {
        var mockProducer = new Mock<IProducer>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProducer>(mockProducer.Object);
        services.AddServiceConnect(builder =>
        {
            builder.ConfigureQueues(q => q.QueueName = "routing-slip-empty-test");
            builder.ConfigureBus(c => c.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        var message = new StepMessage(Guid.NewGuid()) { CurrentStep = "Start" };

        await Assert.ThrowsAsync<ArgumentException>(() => bus.RouteAsync(message, []));
    }
}
