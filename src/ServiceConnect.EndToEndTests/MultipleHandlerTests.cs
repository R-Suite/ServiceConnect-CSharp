using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class MultipleHandlerTests
{
    private readonly MessagingFixture _fixture;

    public MultipleHandlerTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Publish_TwoHandlersForSameType_BothExecute()
    {
        // Arrange
        var bag = new ConcurrentBag<string>();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueName = _fixture.GetUniqueQueueName("multihandler");

        var handlerReferences = new List<HandlerReference>
        {
            new HandlerReference
            {
                HandlerType = typeof(TaggedHandlerA),
                MessageType = typeof(TestMessage),
                RoutingKeys = new List<string>()
            },
            new HandlerReference
            {
                HandlerType = typeof(TaggedHandlerB),
                MessageType = typeof(TestMessage),
                RoutingKeys = new List<string>()
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();

        services.AddSingleton<IList<HandlerReference>>(handlerReferences);
        services.AddSingleton(bag);
        services.AddSingleton(tcs);
        services.AddTransient<IMessageHandler<TestMessage>, TaggedHandlerA>();
        services.AddTransient<IMessageHandler<TestMessage>, TaggedHandlerB>();

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3;
                t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();
        

        try
        {
            // Act
            var correlationId = Guid.NewGuid();
            var sent = new TestMessage(correlationId) { Content = "hello multiple handlers" };
            await bus.PublishAsync(sent);

            // Assert: wait up to 30 seconds for both handlers to execute
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => tcs.TrySetCanceled());

            await tcs.Task;

            Assert.Contains("HandlerA", bag);
            Assert.Contains("HandlerB", bag);
        }
        finally
        {
            await bus.DisposeAsync();
            (provider as IDisposable)?.Dispose();
        }
    }
}

file class TaggedHandlerA : IMessageHandler<TestMessage>
{
    private readonly ConcurrentBag<string> _bag;
    private readonly TaskCompletionSource<bool> _tcs;

    public IConsumeContext? Context { get; set; }

    public TaggedHandlerA(ConcurrentBag<string> bag, TaskCompletionSource<bool> tcs)
    {
        _bag = bag;
        _tcs = tcs;
    }

    public Task HandleAsync(TestMessage message)
    {
        _bag.Add("HandlerA");
        if (_bag.Count >= 2)
            _tcs.TrySetResult(true);
        return Task.CompletedTask;
    }
}

file class TaggedHandlerB : IMessageHandler<TestMessage>
{
    private readonly ConcurrentBag<string> _bag;
    private readonly TaskCompletionSource<bool> _tcs;

    public IConsumeContext? Context { get; set; }

    public TaggedHandlerB(ConcurrentBag<string> bag, TaskCompletionSource<bool> tcs)
    {
        _bag = bag;
        _tcs = tcs;
    }

    public Task HandleAsync(TestMessage message)
    {
        _bag.Add("HandlerB");
        if (_bag.Count >= 2)
            _tcs.TrySetResult(true);
        return Task.CompletedTask;
    }
}
