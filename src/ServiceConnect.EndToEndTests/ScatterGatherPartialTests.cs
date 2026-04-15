using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(RequestReplyCollection))]
public class ScatterGatherPartialTests
{
    private readonly MessagingFixture _fixture;

    public ScatterGatherPartialTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task SendRequestMultiAsync_OneOfTwoResponds_ReturnsPartialResults()
    {
        // Arrange
        var responderQueue = _fixture.GetUniqueQueueName("scatter-partial-responder");
        var silentQueue = _fixture.GetUniqueQueueName("scatter-partial-silent");
        var requesterQueue = _fixture.GetUniqueQueueName("scatter-partial-requester");

        // --- Responder bus setup (replies to requests) ---
        var responderHandlerReferences = new List<HandlerReference>
        {
            new HandlerReference
            {
                HandlerType = typeof(PartialScatterReplyHandler),
                MessageType = typeof(TestRequest)
            }
        };

        var responderServices = new ServiceCollection();
        responderServices.AddLogging();
        responderServices.AddSingleton<IList<HandlerReference>>(responderHandlerReferences);
        responderServices.AddTransient<IMessageHandler<TestRequest>>(_ => new PartialScatterReplyHandler("Resp1"));

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

        // --- Silent consumer bus setup (consumes but never replies) ---
        var silentHandlerReferences = new List<HandlerReference>
        {
            new HandlerReference
            {
                HandlerType = typeof(CallbackHandler<TestRequest>),
                MessageType = typeof(TestRequest)
            }
        };

        var silentServices = new ServiceCollection();
        silentServices.AddLogging();
        silentServices.AddSingleton<IList<HandlerReference>>(silentHandlerReferences);
        silentServices.AddTransient<IMessageHandler<TestRequest>>(_ => new CallbackHandler<TestRequest>(_ => { }));

        silentServices.AddServiceConnect(builder =>
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
            builder.ConfigureQueues(q => q.QueueName = silentQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var silentProvider = silentServices.BuildServiceProvider();
        var silentBus = silentProvider.GetRequiredService<IBus>();
        await silentBus.StartConsumingAsync();

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
            // Act — request sent to both endpoints; only one will reply; timeout returns partial results
            var request = new TestRequest(Guid.NewGuid()) { Question = "partial-question" };
            var replies = await requesterBus.SendRequestMultiAsync<TestRequest, TestResponse>(
                request,
                new RequestOptions
                {
                    EndPoints = new List<string> { responderQueue, silentQueue },
                    ExpectedReplyCount = 2,
                    Timeout = 5000
                });

            // Assert — only one reply received before timeout
            Assert.NotNull(replies);
            Assert.Single(replies);
            Assert.Equal("Resp1: partial-question", replies[0].Answer);
        }
        finally
        {
            await responderBus.DisposeAsync();
            if (responderProvider is IAsyncDisposable asyncResponderProvider) await asyncResponderProvider.DisposeAsync();
            await silentBus.DisposeAsync();
            if (silentProvider is IAsyncDisposable asyncSilentProvider) await asyncSilentProvider.DisposeAsync();
            await requesterBus.DisposeAsync();
            if (requesterProvider is IAsyncDisposable asyncRequesterProvider) await asyncRequesterProvider.DisposeAsync();
        }
    }
}

file class PartialScatterReplyHandler : IMessageHandler<TestRequest>
{
    private readonly string _prefix;

    public PartialScatterReplyHandler(string prefix) => _prefix = prefix;

    public IConsumeContext? Context { get; set; }

    public async Task HandleAsync(TestRequest message)
    {
        await Context!.ReplyAsync(new TestResponse(Guid.NewGuid())
        {
            Answer = $"{_prefix}: {message.Question}"
        });
    }
}
