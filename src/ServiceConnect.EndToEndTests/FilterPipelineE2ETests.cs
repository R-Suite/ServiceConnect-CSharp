using Microsoft.Extensions.DependencyInjection;
using Moq;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using Xunit;

namespace ServiceConnect.EndToEndTests;

file sealed class BlockingFilter : IFilter
{
    public IBus Bus { get; set; } = null!;
    public bool WasCalled { get; private set; }

    public Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        WasCalled = true;
        return Task.FromResult(false); // block the message
    }
}

file sealed class HeaderAddingFilter : IFilter
{
    public IBus Bus { get; set; } = null!;
    public bool WasCalled { get; private set; }

    public Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        WasCalled = true;
        envelope.Headers["X-Test-Header"] = "added-by-filter";
        return Task.FromResult(true); // allow the message through
    }
}

public class FilterPipelineE2ETests
{
    [Fact]
    public async Task OutgoingFilter_CanBlockMessage()
    {
        var mockProducer = new Mock<IProducer>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProducer>(mockProducer.Object);
        services.AddSingleton<BlockingFilter>();
        services.AddServiceConnect(builder =>
        {
            builder.AddOutgoingFilter<BlockingFilter>();
            builder.ConfigureBus(c => c.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        var filter = provider.GetRequiredService<BlockingFilter>();

        var message = new TestMessage(Guid.NewGuid()) { Content = "blocked message" };

        await bus.PublishAsync(message);

        Assert.True(filter.WasCalled);
        // Producer should NOT have been called since filter blocked
        mockProducer.Verify(p => p.PublishAsync(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>()), Times.Never);
    }

    [Fact]
    public async Task OutgoingFilter_CanModifyHeaders()
    {
        var mockProducer = new Mock<IProducer>();
        mockProducer
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProducer>(mockProducer.Object);
        services.AddSingleton<HeaderAddingFilter>();
        services.AddServiceConnect(builder =>
        {
            builder.AddOutgoingFilter<HeaderAddingFilter>();
            builder.ConfigureBus(c => c.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        var filter = provider.GetRequiredService<HeaderAddingFilter>();

        var message = new TestMessage(Guid.NewGuid()) { Content = "header-modified message" };

        await bus.SendAsync(message, new SendOptions { EndPoint = "test-queue" });

        Assert.True(filter.WasCalled);
        mockProducer.Verify(p => p.SendAsync("test-queue", It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>()), Times.Once);
    }
}
