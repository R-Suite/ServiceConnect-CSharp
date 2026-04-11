using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class ExceptionHandlerE2ETests
{
    private readonly MessagingFixture _fixture;

    public ExceptionHandlerE2ETests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task ExceptionHandler_HandlerThrows_CallbackReceivesException()
    {
        // Arrange
        var queueName = _fixture.GetUniqueQueueName("exception-handler");
        var errorQueueName = _fixture.GetUniqueQueueName("exception-handler-eq");
        var capturedExceptions = new ConcurrentBag<Exception>();

        var handlerReferences = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(CallbackHandler<TestMessage>),
                MessageType = typeof(TestMessage),
                RoutingKeys = new List<string>()
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerReferences);
        services.AddTransient<IMessageHandler<TestMessage>>(_ =>
            new CallbackHandler<TestMessage>(_ =>
                throw new InvalidOperationException("test exception")));

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.MaxRetries = 0;
                t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3;
                t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q =>
            {
                q.QueueName = queueName;
                q.ErrorQueueName = errorQueueName;
            });
            builder.ConfigureBus(b =>
            {
                b.ScanForMessageHandlers = false;
                b.ExceptionHandler = ex => capturedExceptions.Add(ex);
            });
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();
        await Task.Delay(500);

        try
        {
            // Act
            var correlationId = Guid.NewGuid();
            var message = new TestMessage(correlationId) { Content = "trigger-exception" };
            await bus.PublishAsync(message);

            // Assert: poll until ExceptionHandler fires (timeout 10s)
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (capturedExceptions.IsEmpty && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            Assert.False(capturedExceptions.IsEmpty, "ExceptionHandler was not invoked within timeout");

            var captured = capturedExceptions.First();
            // The exception may be wrapped in a TargetInvocationException
            var message_text = captured.Message.Contains("test exception")
                ? captured.Message
                : captured.InnerException?.Message ?? captured.Message;

            Assert.Contains("test exception", message_text);
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }
}
