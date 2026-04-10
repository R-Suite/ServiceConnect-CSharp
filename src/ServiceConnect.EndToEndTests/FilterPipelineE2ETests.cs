using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

file sealed class BlockingFilter : IFilter
{
    public IBus Bus { get; set; } = null!;
    public bool WasCalled { get; private set; }

    public bool Process(Envelope envelope)
    {
        WasCalled = true;
        return false; // block the message
    }
}

file sealed class HeaderAddingFilter : IFilter
{
    public IBus Bus { get; set; } = null!;
    public bool WasCalled { get; private set; }

    public bool Process(Envelope envelope)
    {
        WasCalled = true;
        envelope.Headers["X-Test-Header"] = "added-by-filter";
        return true; // allow the message through
    }
}

[Collection(nameof(MessagingCollection))]
public class FilterPipelineE2ETests
{
    private readonly MessagingFixture _fixture;

    public FilterPipelineE2ETests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    private (IBus bus, IServiceProvider provider) CreateBusWithFilter<TFilter>(string queueName)
        where TFilter : class, IFilter
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProducer, Producer>();
        services.AddSingleton<TFilter>();
        services.AddServiceConnect(builder =>
        {
            builder.ConfigureTransport(t =>
            {
                t.Host = $"{_fixture.RabbitMqHostname}:{_fixture.RabbitMqPort}";
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.AddOutgoingFilter<TFilter>();
        });

        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IBus>(), provider);
    }

    [Fact]
    public async Task OutgoingFilter_CanBlockMessage()
    {
        var queueName = _fixture.GetUniqueQueueName("filter");
        var (bus, provider) = CreateBusWithFilter<BlockingFilter>(queueName);
        var filter = provider.GetRequiredService<BlockingFilter>();

        var message = new TestMessage(Guid.NewGuid()) { Content = "blocked message" };

        var exception = await Record.ExceptionAsync(() => bus.PublishAsync(message));

        Assert.Null(exception);
        Assert.True(filter.WasCalled);
    }

    [Fact]
    public async Task OutgoingFilter_CanModifyHeaders()
    {
        var queueName = _fixture.GetUniqueQueueName("filter");
        var (bus, provider) = CreateBusWithFilter<HeaderAddingFilter>(queueName);
        var filter = provider.GetRequiredService<HeaderAddingFilter>();

        var message = new TestMessage(Guid.NewGuid()) { Content = "header-modified message" };

        var exception = await Record.ExceptionAsync(() => bus.SendAsync(message, new SendOptions { EndPoint = queueName }));

        Assert.Null(exception);
        Assert.True(filter.WasCalled);
    }
}
