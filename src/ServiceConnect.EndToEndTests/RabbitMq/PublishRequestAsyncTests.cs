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
public class PublishRequestAsyncTests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task PublishRequestAsync_CallbackFiresForEachReply()
    {
        // Arrange
        var responder1Queue = _fixture.GetUniqueQueueName("pubreq-responder1");
        var responder2Queue = _fixture.GetUniqueQueueName("pubreq-responder2");
        var requesterQueue = _fixture.GetUniqueQueueName("pubreq-requester");

        // --- Responder 1 bus setup ---
        var responder1HandlerReferences = new List<HandlerReference>
        {
            new() {
                HandlerType = typeof(PubReqReplyHandler),
                MessageType = typeof(TestRequest)
            }
        };

        var responder1Services = new ServiceCollection();
        responder1Services.AddLogging();
        responder1Services.AddSingleton<IList<HandlerReference>>(responder1HandlerReferences);
        responder1Services.AddTransient<IMessageHandler<TestRequest>, PubReqReplyHandler>();

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
                HandlerType = typeof(PubReqReplyHandler),
                MessageType = typeof(TestRequest)
            }
        };

        var responder2Services = new ServiceCollection();
        responder2Services.AddLogging();
        responder2Services.AddSingleton<IList<HandlerReference>>(responder2HandlerReferences);
        responder2Services.AddTransient<IMessageHandler<TestRequest>, PubReqReplyHandler>();

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

        // Give consumers time to set up


        try
        {
            // Act
            var replies = new ConcurrentBag<TestResponse>();
            var request = new TestRequest(Guid.NewGuid()) { Question = "publish-request-question" };

            await requesterBus.PublishRequestAsync<TestRequest, TestResponse>(
                request,
                replies.Add,
                new RequestOptions
                {
                    Timeout = 30000,
                    ExpectedReplyCount = 2
                });

            // Assert
            Assert.Equal(2, replies.Count);
            Assert.All(replies, reply => Assert.Equal("publish-request-question", reply.Answer));
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

file class PubReqReplyHandler : IMessageHandler<TestRequest>
{
    public IConsumeContext Context { get; set; } = null!;

    public async Task HandleAsync(TestRequest message, CancellationToken cancellationToken = default)
    {
        await Context!.ReplyAsync(new TestResponse(Guid.NewGuid())
        {
            Answer = message.Question
        });
    }
}
