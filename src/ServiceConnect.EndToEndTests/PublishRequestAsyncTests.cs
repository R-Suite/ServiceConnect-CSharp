using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(RequestReplyCollection))]
public class PublishRequestAsyncTests
{
    private readonly MessagingFixture _fixture;

    public PublishRequestAsyncTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task PublishRequestAsync_CallbackFiresForEachReply()
    {
        // Arrange
        var responderQueue = _fixture.GetUniqueQueueName("pubreq-responder");
        var requesterQueue = _fixture.GetUniqueQueueName("pubreq-requester");

        // --- Responder bus setup ---
        var responderHandlerReferences = new List<HandlerReference>
        {
            new HandlerReference
            {
                HandlerType = typeof(PubReqReplyHandler),
                MessageType = typeof(TestRequest),
                RoutingKeys = new List<string>()
            }
        };

        var responderServices = new ServiceCollection();
        responderServices.AddLogging();
        responderServices.AddSingleton<IList<HandlerReference>>(responderHandlerReferences);
        responderServices.AddTransient<IMessageHandler<TestRequest>, PubReqReplyHandler>();

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
            builder.ConfigureQueues(q =>
            {
                q.QueueName = requesterQueue;
                q.AddQueueMapping(typeof(TestRequest), responderQueue);
            });
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var requesterProvider = requesterServices.BuildServiceProvider();
        var requesterBus = requesterProvider.GetRequiredService<IBus>();
        await requesterBus.StartConsumingAsync();

        // Give consumers time to set up
        

        try
        {
            // Act
            var replies = new ConcurrentBag<TestResponse>();
            var request = new TestRequest(Guid.NewGuid()) { Question = "publish-request-question" };

            await requesterBus.PublishRequestAsync<TestRequest, TestResponse>(
                request,
                reply => replies.Add(reply),
                new RequestOptions
                {
                    EndPoint = responderQueue,
                    Timeout = 30000,
                    ExpectedReplyCount = 1
                });

            // Assert
            Assert.Single(replies);
            Assert.Equal("publish-request-question", replies.First().Answer);
        }
        finally
        {
            await responderBus.DisposeAsync();
            if (responderProvider is IAsyncDisposable asyncResponderProvider) await asyncResponderProvider.DisposeAsync();
            await requesterBus.DisposeAsync();
            if (requesterProvider is IAsyncDisposable asyncRequesterProvider) await asyncRequesterProvider.DisposeAsync();
        }
    }
}

file class PubReqReplyHandler : IMessageHandler<TestRequest>
{
    public IConsumeContext? Context { get; set; }

    public async Task HandleAsync(TestRequest message)
    {
        await Context!.ReplyAsync(new TestResponse(Guid.NewGuid())
        {
            Answer = message.Question
        });
    }
}
