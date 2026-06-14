using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.DependencyInjection;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(RequestReplyCollection))]
public class ScatterGatherTests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task PublishRequestAsync_TwoResponders_BothRepliesReceived()
    {
        // Arrange
        var responder1Queue = _fixture.GetUniqueQueueName("scatter-responder1");
        var responder2Queue = _fixture.GetUniqueQueueName("scatter-responder2");
        var requesterQueue = _fixture.GetUniqueQueueName("scatter-requester");

        // --- Responder 1 bus setup ---
        var responder1HandlerReferences = new List<HandlerReference>
        {
            new() {
                HandlerType = typeof(ScatterReplyHandler),
                MessageType = typeof(TestRequest)
            }
        };

        var responder1Services = new ServiceCollection();
        responder1Services.AddLogging();
        responder1Services.AddSingleton<IReadOnlyList<HandlerReference>>(responder1HandlerReferences);
        responder1Services.AddTransient<IMessageHandler<TestRequest>>(_ => new ScatterReplyHandler("Resp1"));

        responder1Services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.SetClientSetting("Port", _fixture.RabbitMqPort);
                t.SetClientSetting("RetryCount", 3);
                t.SetClientSetting("RetrySeconds", 1);
                t.SslEnabled = false; // Testcontainers RabbitMQ runs plaintext
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
            new() {
                HandlerType = typeof(ScatterReplyHandler),
                MessageType = typeof(TestRequest)
            }
        };

        var responder2Services = new ServiceCollection();
        responder2Services.AddLogging();
        responder2Services.AddSingleton<IReadOnlyList<HandlerReference>>(responder2HandlerReferences);
        responder2Services.AddTransient<IMessageHandler<TestRequest>>(_ => new ScatterReplyHandler("Resp2"));

        responder2Services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.SetClientSetting("Port", _fixture.RabbitMqPort);
                t.SetClientSetting("RetryCount", 3);
                t.SetClientSetting("RetrySeconds", 1);
                t.SslEnabled = false; // Testcontainers RabbitMQ runs plaintext
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
        requesterServices.AddSingleton<IReadOnlyList<HandlerReference>>(requesterHandlerReferences);

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
                t.SslEnabled = false; // Testcontainers RabbitMQ runs plaintext
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
            // Act — broadcast to all subscribers; both responders will see the request and reply
            var request = new TestRequest(Guid.NewGuid()) { Question = "scatter-question" };
            var replies = new List<TestResponse>();
            await requesterBus.PublishRequestAsync<TestRequest, TestResponse>(
                request,
                reply => { lock (replies) { replies.Add(reply); } },
                new RequestOptions
                {
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
            await responder1Bus.DisposeAsync();
            if (responder1Provider is IAsyncDisposable asyncResponder1Provider)
            {
                await asyncResponder1Provider.DisposeAsync();
            }

            await responder2Bus.DisposeAsync();
            if (responder2Provider is IAsyncDisposable asyncResponder2Provider)
            {
                await asyncResponder2Provider.DisposeAsync();
            }

            await requesterBus.DisposeAsync();
            if (requesterProvider is IAsyncDisposable asyncRequesterProvider)
            {
                await asyncRequesterProvider.DisposeAsync();
            }
        }
    }
}

file class ScatterReplyHandler(string prefix) : IMessageHandler<TestRequest>
{
    private readonly string _prefix = prefix;

    public async Task HandleAsync(TestRequest message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        await context.ReplyAsync(new TestResponse(Guid.NewGuid())
        {
            Answer = $"{_prefix}: {message.Question}"
        });
    }
}
