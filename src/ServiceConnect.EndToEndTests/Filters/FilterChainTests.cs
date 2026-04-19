using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

file sealed class OrderTrackingFilterA : IFilter
{

    public Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        FilterChainTests.ExecutionOrder.Enqueue("FilterA");
        return Task.FromResult(true); // continue processing (true = keep going)
    }
}

file sealed class OrderTrackingFilterB : IFilter
{

    public Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        FilterChainTests.ExecutionOrder.Enqueue("FilterB");
        return Task.FromResult(true); // continue processing (true = keep going)
    }
}

file sealed class ChainBlockingFilter : IFilter
{

    public Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        FilterChainTests.ExecutionOrder.Enqueue("BlockingFilter");
        return Task.FromResult(false); // block — stop pipeline (false = don't continue)
    }
}

file sealed class ChainSecondFilter : IFilter
{

    public Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        FilterChainTests.ExecutionOrder.Enqueue("SecondFilter");
        return Task.FromResult(true); // continue processing
    }
}

[Collection(nameof(MessagingCollection))]
public class FilterChainTests
{
    internal static readonly ConcurrentQueue<string> ExecutionOrder = new();

    private readonly MessagingFixture _fixture;

    public FilterChainTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task MultipleBeforeConsumingFilters_AllRunInOrder()
    {
        // Arrange
        ExecutionOrder.Clear();
        var handlerTcs = new TaskCompletionSource<TestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueName = _fixture.GetUniqueQueueName("filter-chain-order");

        var handlerReferences = new List<HandlerReference>
        {
            new() { HandlerType = typeof(CallbackHandler<TestMessage>), MessageType = typeof(TestMessage) }
        };

        var services = new ServiceCollection();
        services.AddLogging();

        services.AddSingleton<IList<HandlerReference>>(handlerReferences);
        services.AddTransient<IMessageHandler<TestMessage>>(_ =>
            new CallbackHandler<TestMessage>(msg => handlerTcs.TrySetResult(msg)));

        services.AddSingleton<OrderTrackingFilterA>();
        services.AddSingleton<OrderTrackingFilterB>();

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.SetClientSetting("Port", _fixture.RabbitMqPort);
                t.SetClientSetting("RetryCount", 3);
                t.SetClientSetting("RetrySeconds", 1);
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
            builder.AddBeforeConsumingFilter<OrderTrackingFilterA>();
            builder.AddBeforeConsumingFilter<OrderTrackingFilterB>();
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();
        

        try
        {
            // Act
            var message = new TestMessage(Guid.NewGuid()) { Content = "filter-chain-order-test" };
            await bus.PublishAsync(message);

            // Assert: handler fires (both filters allowed through)
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => handlerTcs.TrySetCanceled());

            await handlerTcs.Task; // throws if cancelled

            var order = ExecutionOrder.ToArray();
            Assert.Equal(2, order.Length);
            Assert.Equal("FilterA", order[0]);
            Assert.Equal("FilterB", order[1]);
        }
        finally
        {
            await bus.DisposeAsync();
            if (provider is IAsyncDisposable asyncProvider) await asyncProvider.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task FirstFilterBlocks_SecondFilterNotCalled_HandlerNotInvoked()
    {
        // Arrange
        ExecutionOrder.Clear();
        var handlerTcs = new TaskCompletionSource<TestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueName = _fixture.GetUniqueQueueName("filter-chain-block");

        var handlerReferences = new List<HandlerReference>
        {
            new() { HandlerType = typeof(CallbackHandler<TestMessage>), MessageType = typeof(TestMessage) }
        };

        var services = new ServiceCollection();
        services.AddLogging();

        services.AddSingleton<IList<HandlerReference>>(handlerReferences);
        services.AddTransient<IMessageHandler<TestMessage>>(_ =>
            new CallbackHandler<TestMessage>(msg => handlerTcs.TrySetResult(msg)));

        services.AddSingleton<ChainBlockingFilter>();
        services.AddSingleton<ChainSecondFilter>();

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.SetClientSetting("Port", _fixture.RabbitMqPort);
                t.SetClientSetting("RetryCount", 3);
                t.SetClientSetting("RetrySeconds", 1);
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
            builder.AddBeforeConsumingFilter<ChainBlockingFilter>();
            builder.AddBeforeConsumingFilter<ChainSecondFilter>();
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();
        

        try
        {
            // Act
            var message = new TestMessage(Guid.NewGuid()) { Content = "filter-chain-block-test" };
            await bus.PublishAsync(message);

            // Assert: handler NOT invoked (first filter blocked)
            var handlerWasCalled = await Task.WhenAny(handlerTcs.Task, Task.Delay(TimeSpan.FromSeconds(5))) == handlerTcs.Task;
            Assert.False(handlerWasCalled, "Handler should not have been invoked because the first filter blocked the message.");

            // First filter ran, second filter and handler were not called
            var order = ExecutionOrder.ToArray();
            Assert.Contains("BlockingFilter", order);
            Assert.DoesNotContain("SecondFilter", order);
        }
        finally
        {
            await bus.DisposeAsync();
            if (provider is IAsyncDisposable asyncProvider) await asyncProvider.DisposeAsync();
        }
    }
}
