using Microsoft.Extensions.DependencyInjection;
using Moq;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

public class PublishAfterDisposeTests
{
    [Fact]
    public async Task PublishAsync_AfterDispose_Throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProducer>(new Mock<IProducer>().Object);
        services.AddServiceConnect(b => b.ConfigureBus(c => c.ScanForMessageHandlers = false));

        var provider = services.BuildServiceProvider();
        IBus bus;

        try
        {
            bus = provider.GetRequiredService<IBus>();
        }
        finally
        {
            await provider.DisposeAsync();
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await bus.PublishAsync(new TestMessage()));
    }

    private class TestMessage : Message
    {
        public TestMessage() : base(Guid.NewGuid()) { }
    }
}
