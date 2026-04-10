using Microsoft.Extensions.DependencyInjection;
using Moq;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

public class RoutingSlipTests
{
    [Fact]
    public void Route_SetsRoutingSlipHeaders()
    {
        string? capturedEndpoint = null;
        Dictionary<string, string>? capturedHeaders = null;

        var mockProducer = new Mock<IProducer>();
        mockProducer
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>()))
            .Callback<string, Type, byte[], Dictionary<string, string>>((ep, t, b, h) =>
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
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        var message = new StepMessage(Guid.NewGuid()) { CurrentStep = "Start" };

        bus.Route(message, new List<string> { "Step1", "Step2", "Step3" });

        Assert.Equal("Step1", capturedEndpoint);
        Assert.NotNull(capturedHeaders);
        Assert.True(capturedHeaders.ContainsKey("RoutingSlip"));
        Assert.Equal("Step2,Step3", capturedHeaders["RoutingSlip"]);
    }

    [Fact]
    public void Route_SingleDestination_NoRoutingSlipHeader()
    {
        string? capturedEndpoint = null;
        Dictionary<string, string>? capturedHeaders = null;

        var mockProducer = new Mock<IProducer>();
        mockProducer
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>()))
            .Callback<string, Type, byte[], Dictionary<string, string>>((ep, t, b, h) =>
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
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        var message = new StepMessage(Guid.NewGuid()) { CurrentStep = "Start" };

        bus.Route(message, new List<string> { "OnlyDest" });

        Assert.Equal("OnlyDest", capturedEndpoint);
        Assert.NotNull(capturedHeaders);
        Assert.False(capturedHeaders.ContainsKey("RoutingSlip"));
    }

    [Fact]
    public void Route_EmptyDestinations_Throws()
    {
        var mockProducer = new Mock<IProducer>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProducer>(mockProducer.Object);
        services.AddServiceConnect(builder =>
        {
            builder.ConfigureQueues(q => q.QueueName = "routing-slip-empty-test");
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        var message = new StepMessage(Guid.NewGuid()) { CurrentStep = "Start" };

        Assert.Throws<ArgumentException>(() => bus.Route(message, new List<string>()));
    }
}
