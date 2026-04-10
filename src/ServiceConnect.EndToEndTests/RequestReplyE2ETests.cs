using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class RequestReplyE2ETests
{
    private readonly MessagingFixture _fixture;

    public RequestReplyE2ETests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task SendRequest_ResponderReplies_RequesterGetsResponse()
    {
        // Arrange
        var responderQueue = _fixture.GetUniqueQueueName("responder");
        var requesterQueue = _fixture.GetUniqueQueueName("requester");

        // --- Responder bus setup ---
        var responderHandlerReferences = new List<HandlerReference>
        {
            new HandlerReference
            {
                HandlerType = typeof(ReplyHandler),
                MessageType = typeof(TestRequest),
                RoutingKeys = new List<string>()
            }
        };

        var responderServices = new ServiceCollection();
        responderServices.AddLogging();
        responderServices.AddSingleton<IList<HandlerReference>>(responderHandlerReferences);
        responderServices.AddTransient<IMessageHandler<TestRequest>, ReplyHandler>();

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

        // Give consumers time to set up
        await Task.Delay(500);

        try
        {
            // Act
            var request = new TestRequest(Guid.NewGuid()) { Question = "What is 2 + 2?" };
            var response = await requesterBus.SendRequestAsync<TestRequest, TestResponse>(
                request,
                new RequestOptions { EndPoint = responderQueue, Timeout = 30000 });

            // Assert
            Assert.NotNull(response);
            Assert.Equal("The answer is 4", response.Answer);
        }
        finally
        {
            responderBus.Dispose();
            requesterBus.Dispose();
        }
    }
}

file class ReplyHandler : IMessageHandler<TestRequest>
{
    public IConsumeContext? Context { get; set; }

    public Task HandleAsync(TestRequest message)
    {
        Context!.Reply(new TestResponse(Guid.NewGuid()) { Answer = "The answer is 4" });
        return Task.CompletedTask;
    }
}
