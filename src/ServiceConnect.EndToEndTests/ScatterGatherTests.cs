using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(RequestReplyCollection))]
public class ScatterGatherTests
{
    private readonly MessagingFixture _fixture;

    public ScatterGatherTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task SendRequestMultiAsync_TwoResponders_BothRepliesReceived()
    {
        // Arrange
        var responder1Queue = _fixture.GetUniqueQueueName("scatter-responder1");
        var responder2Queue = _fixture.GetUniqueQueueName("scatter-responder2");
        var requesterQueue = _fixture.GetUniqueQueueName("scatter-requester");

        // --- Responder 1 bus setup ---
        var responder1HandlerReferences = new List<HandlerReference>
        {
            new HandlerReference
            {
                HandlerType = typeof(ScatterReplyHandler),
                MessageType = typeof(TestRequest),
                RoutingKeys = new List<string>()
            }
        };

        var responder1Services = new ServiceCollection();
        responder1Services.AddLogging();
        responder1Services.AddSingleton<IList<HandlerReference>>(responder1HandlerReferences);
        responder1Services.AddTransient<IMessageHandler<TestRequest>>(_ => new ScatterReplyHandler("Resp1"));

        responder1Services.AddServiceConnect(builder =>
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
            builder.ConfigureQueues(q => q.QueueName = responder1Queue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var responder1Provider = responder1Services.BuildServiceProvider();
        var responder1Bus = responder1Provider.GetRequiredService<IBus>();
        await responder1Bus.StartConsumingAsync();

        // --- Responder 2 bus setup ---
        var responder2HandlerReferences = new List<HandlerReference>
        {
            new HandlerReference
            {
                HandlerType = typeof(ScatterReplyHandler),
                MessageType = typeof(TestRequest),
                RoutingKeys = new List<string>()
            }
        };

        var responder2Services = new ServiceCollection();
        responder2Services.AddLogging();
        responder2Services.AddSingleton<IList<HandlerReference>>(responder2HandlerReferences);
        responder2Services.AddTransient<IMessageHandler<TestRequest>>(_ => new ScatterReplyHandler("Resp2"));

        responder2Services.AddServiceConnect(builder =>
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
            builder.ConfigureQueues(q => q.QueueName = responder2Queue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var responder2Provider = responder2Services.BuildServiceProvider();
        var responder2Bus = responder2Provider.GetRequiredService<IBus>();
        await responder2Bus.StartConsumingAsync();

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
        

        try
        {
            // Act
            var request = new TestRequest(Guid.NewGuid()) { Question = "scatter-question" };
            var replies = await requesterBus.SendRequestMultiAsync<TestRequest, TestResponse>(
                request,
                new RequestOptions
                {
                    EndPoints = new List<string> { responder1Queue, responder2Queue },
                    ExpectedReplyCount = 2,
                    Timeout = 30000
                });

            // Assert
            Assert.NotNull(replies);
            Assert.Equal(2, replies.Count);

            var answers = replies.Select(r => r.Answer).ToList();
            Assert.Contains(answers, a => a == "Resp1: scatter-question");
            Assert.Contains(answers, a => a == "Resp2: scatter-question");
        }
        finally
        {
            responder1Bus.Dispose();
            (responder1Provider as IDisposable)?.Dispose();
            responder2Bus.Dispose();
            (responder2Provider as IDisposable)?.Dispose();
            requesterBus.Dispose();
            (requesterProvider as IDisposable)?.Dispose();
        }
    }
}

file class ScatterReplyHandler : IMessageHandler<TestRequest>
{
    private readonly string _prefix;

    public ScatterReplyHandler(string prefix) => _prefix = prefix;

    public IConsumeContext? Context { get; set; }

    public async Task HandleAsync(TestRequest message)
    {
        await Context!.ReplyAsync(new TestResponse(Guid.NewGuid())
        {
            Answer = $"{_prefix}: {message.Question}"
        });
    }
}
