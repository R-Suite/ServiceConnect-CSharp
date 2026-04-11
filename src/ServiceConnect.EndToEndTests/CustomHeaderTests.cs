using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class CustomHeaderTests
{
    private readonly MessagingFixture _fixture;

    public CustomHeaderTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task PublishAsync_CustomHeaders_ReceivedByHandler()
    {
        // Arrange
        var tcs = new TaskCompletionSource<IDictionary<string, object>>();
        var queueName = _fixture.GetUniqueQueueName("customheader");

        var handlerReferences = new List<HandlerReference>
        {
            new HandlerReference
            {
                HandlerType = typeof(HeaderCaptureHandler),
                MessageType = typeof(TestMessage),
                RoutingKeys = new List<string>()
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerReferences);
        services.AddTransient<IMessageHandler<TestMessage>>(_ =>
            new HeaderCaptureHandler(headers => tcs.TrySetResult(headers)));


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
        await Task.Delay(500);

        try
        {
            // Act
            var message = new TestMessage(Guid.NewGuid()) { Content = "custom-header-test" };
            await bus.PublishAsync(message, new PublishOptions
            {
                Headers = new Dictionary<string, string> { ["X-Custom"] = "hello-world" }
            });

            // Assert: wait up to 30 seconds for the handler to be called
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => tcs.TrySetCanceled());

            var receivedHeaders = await tcs.Task;

            Assert.NotNull(receivedHeaders);
            Assert.True(receivedHeaders.ContainsKey("X-Custom"), "Expected 'X-Custom' header to be present");
            var rawValue = receivedHeaders["X-Custom"];
            var headerValue = rawValue is byte[] b ? System.Text.Encoding.UTF8.GetString(b) : rawValue?.ToString();
            Assert.Equal("hello-world", headerValue);
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task SendRequestAsync_CustomHeaders_PropagatedToResponder()
    {
        // Arrange
        var responderQueue = _fixture.GetUniqueQueueName("header-responder");
        var requesterQueue = _fixture.GetUniqueQueueName("header-requester");

        // --- Responder bus setup ---
        var responderHandlerReferences = new List<HandlerReference>
        {
            new HandlerReference
            {
                HandlerType = typeof(HeaderEchoReplyHandler),
                MessageType = typeof(TestRequest),
                RoutingKeys = new List<string>()
            }
        };

        var responderServices = new ServiceCollection();
        responderServices.AddLogging();
        responderServices.AddSingleton<IList<HandlerReference>>(responderHandlerReferences);
        responderServices.AddTransient<IMessageHandler<TestRequest>, HeaderEchoReplyHandler>();

        responderServices.AddServiceConnect(builder =>
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
            builder.ConfigureQueues(q => q.QueueName = responderQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var responderProvider = responderServices.BuildServiceProvider();
        var responderBus = responderProvider.GetRequiredService<IBus>();
        await responderBus.StartConsumingAsync();

        // --- Requester bus setup ---
        var requesterHandlerReferences = new List<HandlerReference>();

        var requesterServices = new ServiceCollection();
        requesterServices.AddLogging();
        requesterServices.AddSingleton<IList<HandlerReference>>(requesterHandlerReferences);

        requesterServices.AddServiceConnect(builder =>
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
            builder.ConfigureQueues(q => q.QueueName = requesterQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var requesterProvider = requesterServices.BuildServiceProvider();
        var requesterBus = requesterProvider.GetRequiredService<IBus>();
        await requesterBus.StartConsumingAsync();

        await Task.Delay(500);

        try
        {
            // Act
            var request = new TestRequest(Guid.NewGuid()) { Question = "Echo my trace id" };
            var response = await requesterBus.SendRequestAsync<TestRequest, TestResponse>(
                request,
                new RequestOptions
                {
                    EndPoint = responderQueue,
                    Timeout = 30000,
                    Headers = new Dictionary<string, string> { ["X-Trace-Id"] = "trace-123" }
                });

            // Assert
            Assert.NotNull(response);
            Assert.Equal("trace-123", response.Answer);
        }
        finally
        {
            responderBus.Dispose();
            (responderProvider as IDisposable)?.Dispose();
            requesterBus.Dispose();
            (requesterProvider as IDisposable)?.Dispose();
        }
    }
}

file class HeaderCaptureHandler : IMessageHandler<TestMessage>
{
    private readonly Action<IDictionary<string, object>> _callback;

    public IConsumeContext? Context { get; set; }

    public HeaderCaptureHandler(Action<IDictionary<string, object>> callback) => _callback = callback;

    public Task HandleAsync(TestMessage message)
    {
        _callback(Context!.Headers);
        return Task.CompletedTask;
    }
}

file class HeaderEchoReplyHandler : IMessageHandler<TestRequest>
{
    public IConsumeContext? Context { get; set; }

    public async Task HandleAsync(TestRequest message)
    {
        var traceId = Context!.Headers.TryGetValue("X-Trace-Id", out var value)
            ? (value is byte[] bytes ? System.Text.Encoding.UTF8.GetString(bytes) : value?.ToString() ?? string.Empty)
            : string.Empty;
        await Context!.ReplyAsync(new TestResponse(Guid.NewGuid()) { Answer = traceId });
    }
}
