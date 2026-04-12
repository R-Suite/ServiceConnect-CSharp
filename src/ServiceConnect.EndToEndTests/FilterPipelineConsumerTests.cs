using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

file sealed class ConsumerBlockingFilter : IFilter
{
    public IBus Bus { get; set; } = null!;

    public bool Process(Envelope envelope)
    {
        return false; // block the message — handler must not be invoked
    }
}

file sealed class AfterConsumingSignalFilter : IFilter
{
    private readonly TaskCompletionSource _tcs;

    public AfterConsumingSignalFilter(TaskCompletionSource tcs) => _tcs = tcs;

    public IBus Bus { get; set; } = null!;

    public bool Process(Envelope envelope)
    {
        _tcs.TrySetResult();
        return true;
    }
}

[Collection(nameof(MessagingCollection))]
public class FilterPipelineConsumerTests
{
    private readonly MessagingFixture _fixture;

    public FilterPipelineConsumerTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task BeforeConsumingFilter_Blocks_HandlerNotInvoked()
    {
        // Arrange
        var handlerInvokedTcs = new TaskCompletionSource<TestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueName = _fixture.GetUniqueQueueName("filter-blocking");

        var handlerReferences = new List<HandlerReference>
        {
            new() { HandlerType = typeof(CallbackHandler<TestMessage>), MessageType = typeof(TestMessage), RoutingKeys = new List<string>() }
        };

        var services = new ServiceCollection();
        services.AddLogging();

        services.AddSingleton<IList<HandlerReference>>(handlerReferences);
        services.AddTransient<IMessageHandler<TestMessage>>(_ =>
            new CallbackHandler<TestMessage>(msg => handlerInvokedTcs.TrySetResult(msg)));

        services.AddSingleton<ConsumerBlockingFilter>();

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
            builder.AddBeforeConsumingFilter<ConsumerBlockingFilter>();
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();
        

        try
        {
            // Act
            var message = new TestMessage(Guid.NewGuid()) { Content = "should-be-blocked" };
            await bus.PublishAsync(message);

            // Wait 5 seconds — the handler should NOT fire
            var handlerWasCalled = await Task.WhenAny(handlerInvokedTcs.Task, Task.Delay(TimeSpan.FromSeconds(5))) == handlerInvokedTcs.Task;

            // Assert
            Assert.False(handlerWasCalled, "Handler should not have been invoked because the before-consuming filter blocked the message.");
        }
        finally
        {
            await bus.DisposeAsync();
            (provider as IDisposable)?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task AfterConsumingFilter_RunsAfterHandler()
    {
        // Arrange
        var filterSignalTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueName = _fixture.GetUniqueQueueName("filter-after");

        var handlerReferences = new List<HandlerReference>
        {
            new() { HandlerType = typeof(CallbackHandler<TestMessage>), MessageType = typeof(TestMessage), RoutingKeys = new List<string>() }
        };

        var services = new ServiceCollection();
        services.AddLogging();

        services.AddSingleton<IList<HandlerReference>>(handlerReferences);

        // No-op handler
        services.AddTransient<IMessageHandler<TestMessage>>(_ =>
            new CallbackHandler<TestMessage>(_ => { }));

        // Register the after-consuming filter with the shared TCS
        services.AddSingleton<AfterConsumingSignalFilter>(new AfterConsumingSignalFilter(filterSignalTcs));

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
            builder.AddAfterConsumingFilter<AfterConsumingSignalFilter>();
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();
        

        try
        {
            // Act
            var message = new TestMessage(Guid.NewGuid()) { Content = "after-filter-test" };
            await bus.PublishAsync(message);

            // Assert: filter signals within 30 seconds
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => filterSignalTcs.TrySetCanceled());

            await filterSignalTcs.Task; // throws if cancelled
        }
        finally
        {
            await bus.DisposeAsync();
            (provider as IDisposable)?.Dispose();
        }
    }
}
