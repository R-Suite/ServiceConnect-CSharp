using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

file sealed class HeaderAddingSendMiddleware : ISendMessageMiddleware
{
    public Task ProcessAsync(Type typeObject, byte[] messageBytes,
        Dictionary<string, string> headers, string? endPoint, SendMessageDelegate next, CancellationToken cancellationToken)
    {
        headers["X-Send-Middleware"] = "applied";
        return next(typeObject, messageBytes, headers, endPoint, cancellationToken);
    }
}

file sealed class HeaderCapturingMiddleware(TaskCompletionSource<IDictionary<string, object>> tcs) : IMessageProcessingMiddleware
{
    private readonly TaskCompletionSource<IDictionary<string, object>> _tcs = tcs;

    public async Task<ConsumeEventResult> ProcessAsync(
        ReadOnlyMemory<byte> messageBytes, Type messageType, object message,
        IDictionary<string, object> headers, Envelope envelope, MessageProcessingDelegate next, CancellationToken cancellationToken)
    {
        _tcs.TrySetResult(headers);
        return await next(messageBytes, messageType, message, headers, envelope, cancellationToken);
    }
}

[Collection(nameof(MessagingCollection))]
public class MiddlewarePipelineE2ETests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task SendMiddleware_AddsHeader_ReceivedByConsumer()
    {
        // Arrange
        var tcs = new TaskCompletionSource<IDictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueName = _fixture.GetUniqueQueueName("mw-pipeline");

        var handlerReferences = new List<HandlerReference>
        {
            new() {
                HandlerType = typeof(NoOpMessageHandler),
                MessageType = typeof(TestMessage)
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerReferences);
        services.AddTransient<IMessageHandler<TestMessage>, NoOpMessageHandler>();
        services.AddSingleton<HeaderAddingSendMiddleware>();
        services.AddSingleton(tcs);
        services.AddSingleton<HeaderCapturingMiddleware>();

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
            builder.ConfigureTransport(t => t.MaxRetries = 0);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
            builder.AddSendMessageMiddleware<HeaderAddingSendMiddleware>();
            builder.AddMessageProcessingMiddleware<HeaderCapturingMiddleware>();
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();


        try
        {
            // Act
            var message = new TestMessage(Guid.NewGuid()) { Content = "middleware-test" };
            await bus.PublishAsync(message);

            // Assert: wait up to 30 seconds for the processing middleware to capture headers
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => tcs.TrySetCanceled());

            var capturedHeaders = await tcs.Task;

            Assert.NotNull(capturedHeaders);
            Assert.True(capturedHeaders.ContainsKey("X-Send-Middleware"),
                "Expected 'X-Send-Middleware' header to be present in captured headers");

            var rawValue = capturedHeaders["X-Send-Middleware"];
            var headerValue = rawValue is byte[] b
                ? System.Text.Encoding.UTF8.GetString(b)
                : rawValue?.ToString();
            Assert.Equal("applied", headerValue);
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

file sealed class NoOpMessageHandler : IMessageHandler<TestMessage>
{
    public IConsumeContext Context { get; set; } = null!;

    public Task HandleAsync(TestMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
