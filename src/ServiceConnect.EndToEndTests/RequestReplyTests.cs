using Microsoft.Extensions.DependencyInjection;
using Moq;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Interfaces.Options;
using Xunit;

namespace ServiceConnect.EndToEndTests;

public class RequestReplyTests
{
    [Fact]
    public async Task SendRequestAsync_ThrowsTimeout_WhenNoResponder()
    {
        var mockProducer = new Mock<IProducer>();
        mockProducer
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(Task.CompletedTask);
        mockProducer
            .Setup(p => p.SendAsync(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProducer>(mockProducer.Object);
        services.AddServiceConnect(builder =>
        {
            builder.ConfigureQueues(q => q.QueueName = "request-reply-timeout-test");
            builder.ConfigureBus(c => c.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        var request = new TestRequest(Guid.NewGuid()) { Question = "test request" };

        await Assert.ThrowsAsync<RequestTimeoutException>(() =>
            bus.SendRequestAsync<TestRequest, TestResponse>(
                request,
                new RequestOptions { EndPoint = "responder-queue", Timeout = 200 }));
    }

    [Fact]
    public async Task SendRequestAsync_ReturnsReply_WhenResponderReplies()
    {
        IRequestReplyManager? replyManager = null;
        IMessageSerializer? serializer = null;

        var mockProducer = new Mock<IProducer>();
        mockProducer
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, Type, byte[], Dictionary<string, string>, CancellationToken>((ep, t, b, h, ct) =>
            {
                if (h.TryGetValue("RequestMessageId", out var messageId) && replyManager != null && serializer != null)
                {
                    var response = new TestResponse(Guid.NewGuid()) { Answer = "reply data" };
                    var responseBytes = serializer.Serialize(response);
                    Task.Run(() => replyManager.ProcessReply(messageId, responseBytes, typeof(TestResponse)));
                }
            })
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProducer>(mockProducer.Object);
        services.AddServiceConnect(builder =>
        {
            builder.ConfigureQueues(q => q.QueueName = "request-reply-success-test");
            builder.ConfigureBus(c => c.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        replyManager = provider.GetRequiredService<IRequestReplyManager>();
        serializer = provider.GetRequiredService<IMessageSerializer>();

        var bus = provider.GetRequiredService<IBus>();

        var request = new TestRequest(Guid.NewGuid()) { Question = "test request" };

        var response = await bus.SendRequestAsync<TestRequest, TestResponse>(
            request,
            new RequestOptions { EndPoint = "responder-queue", Timeout = 5000 });

        Assert.NotNull(response);
        Assert.Equal("reply data", response.Answer);
    }

    [Fact]
    public async Task SendRequestAsync_BlockedByFilter_ThrowsInvalidOperationException()
    {
        var mockProducer = new Mock<IProducer>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProducer>(mockProducer.Object);
        services.AddSingleton<BlockAllFilter>();
        services.AddServiceConnect(builder =>
        {
            builder.ConfigureQueues(q => q.QueueName = "request-reply-blocked-test");
            builder.AddOutgoingFilter<BlockAllFilter>();
            builder.ConfigureBus(c => c.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        var request = new TestRequest(Guid.NewGuid()) { Question = "blocked request" };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bus.SendRequestAsync<TestRequest, TestResponse>(
                request,
                new RequestOptions { EndPoint = "responder-queue", Timeout = 5000 }));
    }

    private class BlockAllFilter : IFilter
    {
        public IBus Bus { get; set; } = null!;
        public Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
