using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.DependencyInjection;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class ExceptionHandlerE2ETests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

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
                MessageType = typeof(TestMessage)
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
                t.SetClientSetting("Port", _fixture.RabbitMqPort);
                t.SetClientSetting("RetryCount", 3);
                t.SetClientSetting("RetrySeconds", 1);
                t.SslEnabled = false; // Testcontainers RabbitMQ runs plaintext
            });
            builder.ConfigureQueues(q =>
            {
                q.QueueName = queueName;
                q.ErrorQueueName = errorQueueName;
            });
            builder.ConfigureBus(b =>
            {
                b.ScanForMessageHandlers = false;
                b.ExceptionHandler = (ex, _) => { capturedExceptions.Add(ex); return ValueTask.CompletedTask; };
            });
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();


        try
        {
            // Act
            var correlationId = Guid.NewGuid();
            var message = new TestMessage(correlationId) { Content = "trigger-exception" };
            await bus.PublishAsync(message);

            // Assert: wait for ExceptionHandler to fire
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (capturedExceptions.IsEmpty && !cts.Token.IsCancellationRequested)
            {
                await Task.Delay(100, cts.Token);
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
            await bus.DisposeAsync();
            if (provider is IAsyncDisposable asyncProvider)
            {
                await asyncProvider.DisposeAsync();
            }
        }
    }
}
