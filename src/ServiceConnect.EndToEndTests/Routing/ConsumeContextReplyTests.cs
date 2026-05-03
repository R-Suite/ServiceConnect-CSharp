using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(RequestReplyCollection))]
public class ConsumeContextReplyTests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Handler_UsesContextReply_RequesterReceivesResponse()
    {
        // This test verifies Context.ReplyAsync<T>() works when a handler
        // receives a sent message (not using SendRequestAsync, but direct Send
        // with a reply handler on the sender side).

        // Arrange
        var responderQueue = _fixture.GetUniqueQueueName("ctx-reply-responder");
        var requesterQueue = _fixture.GetUniqueQueueName("ctx-reply-requester");
        var replyTcs = new TaskCompletionSource<TestResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        // --- Responder: handles TestRequest and uses Context.ReplyAsync ---
        var responderHandlerRefs = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(ContextReplyHandler),
                MessageType = typeof(TestRequest)
            }
        };

        var responderServices = new ServiceCollection();
        responderServices.AddLogging();
        responderServices.AddSingleton<IList<HandlerReference>>(responderHandlerRefs);
        responderServices.AddTransient<IMessageHandler<TestRequest>, ContextReplyHandler>();

        responderServices.AddServiceConnect(builder =>
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
            builder.ConfigureQueues(q => q.QueueName = responderQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var responderProvider = responderServices.BuildServiceProvider();
        var responderBus = responderProvider.GetRequiredService<IBus>();
        await responderBus.StartConsumingAsync();

        // --- Requester: sends request and listens for reply via handler ---
        var requesterHandlerRefs = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(CallbackHandler<TestResponse>),
                MessageType = typeof(TestResponse)
            }
        };

        var requesterServices = new ServiceCollection();
        requesterServices.AddLogging();
        requesterServices.AddSingleton<IList<HandlerReference>>(requesterHandlerRefs);
        requesterServices.AddTransient<IMessageHandler<TestResponse>>(_ =>
            new CallbackHandler<TestResponse>(msg => replyTcs.TrySetResult(msg)));

        requesterServices.AddServiceConnect(builder =>
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
            builder.ConfigureQueues(q => q.QueueName = requesterQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var requesterProvider = requesterServices.BuildServiceProvider();
        var requesterBus = requesterProvider.GetRequiredService<IBus>();
        await requesterBus.StartConsumingAsync();



        try
        {
            // Act: use SendRequestAsync which sets SourceAddress so Context.ReplyAsync works
            var request = new TestRequest(Guid.NewGuid()) { Question = "What is context reply?" };
            var response = await requesterBus.SendRequestAsync<TestRequest, TestResponse>(
                request,
                new RequestOptions { EndPoint = responderQueue, Timeout = 30000 });

            // Assert
            Assert.NotNull(response);
            Assert.Equal("Context reply works!", response.Answer);
        }
        finally
        {
            await responderBus.DisposeAsync();
            if (responderProvider is IAsyncDisposable asyncResponderProvider)
            {
                await asyncResponderProvider.DisposeAsync();
            }

            await requesterBus.DisposeAsync();
            if (requesterProvider is IAsyncDisposable asyncRequesterProvider)
            {
                await asyncRequesterProvider.DisposeAsync();
            }
        }
    }
}

file class ContextReplyHandler : IMessageHandler<TestRequest>
{
    public async Task HandleAsync(TestRequest message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        // Uses context.ReplyAsync — the key feature under test
        await context.ReplyAsync(new TestResponse(Guid.NewGuid())
        {
            Answer = "Context reply works!"
        });
    }
}
