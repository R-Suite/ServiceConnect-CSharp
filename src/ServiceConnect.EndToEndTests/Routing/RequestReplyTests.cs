using Microsoft.Extensions.DependencyInjection;
using Moq;
using ServiceConnect.DependencyInjection;
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
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockProducer
            .Setup(p => p.SendAsync(It.IsAny<Type>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
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
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, Type, ReadOnlyMemory<byte>, int?, IReadOnlyDictionary<string, string>?, CancellationToken>((ep, t, b, hops, h, ct) =>
            {
                if (h is not null && h.TryGetValue("RequestMessageId", out var messageId) && replyManager != null && serializer != null)
                {
                    var response = new TestResponse(Guid.NewGuid()) { Answer = "reply data" };
                    var bw = new System.Buffers.ArrayBufferWriter<byte>();
                    serializer.Serialize(response, bw);
                    var responseBytes = bw.WrittenMemory;
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

        // Filter-stop now surfaces as the typed OutgoingFiltersBlockedException (formerly a
        // raw InvalidOperationException) so callers can distinguish a deliberate filter
        // rejection from state-misuse or transport faults.
        await Assert.ThrowsAsync<ServiceConnect.Interfaces.Exceptions.OutgoingFiltersBlockedException>(() =>
            bus.SendRequestAsync<TestRequest, TestResponse>(
                request,
                new RequestOptions { EndPoint = "responder-queue", Timeout = 5000 }));
    }

    private class BlockAllFilter : IFilter
    {
        public Task<FilterAction> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default) => Task.FromResult(FilterAction.Stop);
    }
}
