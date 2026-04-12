using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(IsolatedCollection))]
public class RoutingSlipForwardingTests
{
    private readonly MessagingFixture _fixture;

    public RoutingSlipForwardingTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task RoutingSlip_MessageForwardedToNextDestination()
    {
        // Arrange: two bus instances — step1 and step2
        var step1Queue = _fixture.GetUniqueQueueName("rslip-step1");
        var step2Queue = _fixture.GetUniqueQueueName("rslip-step2");

        var step1Called = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var step2Called = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // --- Step 1 bus: handles the message, routing slip should forward to step2 ---
        var step1HandlerRefs = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(Step1Handler),
                MessageType = typeof(StepMessage),
                RoutingKeys = new List<string>()
            }
        };

        var step1Services = new ServiceCollection();
        step1Services.AddLogging();
        step1Services.AddSingleton<IList<HandlerReference>>(step1HandlerRefs);
        step1Services.AddTransient<IMessageHandler<StepMessage>>(_ =>
            new Step1Handler(() => step1Called.TrySetResult(true)));

        step1Services.AddServiceConnect(builder =>
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
            builder.ConfigureQueues(q =>
            {
                q.QueueName = step1Queue;
                q.AddQueueMapping(typeof(StepMessage), step2Queue);
            });
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var step1Provider = step1Services.BuildServiceProvider();
        var step1Bus = step1Provider.GetRequiredService<IBus>();

        // --- Step 2 bus: receives the forwarded message ---
        var step2HandlerRefs = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(Step2Handler),
                MessageType = typeof(StepMessage),
                RoutingKeys = new List<string>()
            }
        };

        var step2Services = new ServiceCollection();
        step2Services.AddLogging();
        step2Services.AddSingleton<IList<HandlerReference>>(step2HandlerRefs);
        step2Services.AddTransient<IMessageHandler<StepMessage>>(_ =>
            new Step2Handler(step => step2Called.TrySetResult(step)));

        step2Services.AddServiceConnect(builder =>
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
            builder.ConfigureQueues(q => q.QueueName = step2Queue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var step2Provider = step2Services.BuildServiceProvider();
        var step2Bus = step2Provider.GetRequiredService<IBus>();

        await step1Bus.StartConsumingAsync();
        await step2Bus.StartConsumingAsync();
        

        try
        {
            // Act: send message to step1 with routing slip pointing to step2
            var message = new StepMessage(Guid.NewGuid()) { CurrentStep = "Origin" };
            await step1Bus.RouteAsync(message, new List<string> { step1Queue, step2Queue });

            // Assert: step1 handler was called
            var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts1.Token.Register(() => step1Called.TrySetCanceled());
            Assert.True(await step1Called.Task, "Step1 handler should have been called");

            // Assert: step2 received the forwarded message
            var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts2.Token.Register(() => step2Called.TrySetCanceled());
            var receivedStep = await step2Called.Task;
            Assert.Equal("Step1", receivedStep);
        }
        finally
        {
            await step1Bus.DisposeAsync();
            await step2Bus.DisposeAsync();
            (step1Provider as IDisposable)?.Dispose();
            (step2Provider as IDisposable)?.Dispose();
        }
    }
}

file class Step1Handler : IMessageHandler<StepMessage>
{
    private readonly Action _onHandled;
    public IConsumeContext? Context { get; set; }

    public Step1Handler(Action onHandled) => _onHandled = onHandled;

    public Task HandleAsync(StepMessage message)
    {
        message.CurrentStep = "Step1";
        _onHandled();
        return Task.CompletedTask;
    }
}

file class Step2Handler : IMessageHandler<StepMessage>
{
    private readonly Action<string> _onHandled;
    public IConsumeContext? Context { get; set; }

    public Step2Handler(Action<string> onHandled) => _onHandled = onHandled;

    public Task HandleAsync(StepMessage message)
    {
        _onHandled(message.CurrentStep);
        return Task.CompletedTask;
    }
}
