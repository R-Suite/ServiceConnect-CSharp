using Microsoft.Extensions.DependencyInjection;
using Moq;
using ServiceConnect;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.DI;

public class RequestReplyManagerRegistrationTests
{
    private static IServiceCollection CreateMinimalServices()
    {
        var services = new ServiceCollection();
        // SendMessagePipeline requires IProducer
        services.AddSingleton(new Mock<IProducer>().Object);
        services.AddLogging();
        return services;
    }

    private static void ConfigureMinimal(ServiceConnectBuilder b) =>
        b.ConfigureQueues(q => q.QueueName = "test")
         .ConfigureBus(c => c.ScanForMessageHandlers = false);

    // Case A: user pre-registers a custom IRequestReplyManager that ALSO implements
    // IReplyStatusRequestReplyManager. Both interfaces should resolve to the user's instance.
    [Fact]
    public async Task AddServiceConnect_UsesCustomImpl_WhenUserPreregistersCompleteImpl()
    {
        var services = CreateMinimalServices();
        services.AddSingleton<IRequestReplyManager, CustomManager>();
        services.AddSingleton<IReplyStatusRequestReplyManager>(sp =>
            (CustomManager)sp.GetRequiredService<IRequestReplyManager>());
        services.AddServiceConnect(ConfigureMinimal);

        await using var provider = services.BuildServiceProvider();
        var rrm = provider.GetRequiredService<IRequestReplyManager>();
        var rsrrm = provider.GetRequiredService<IReplyStatusRequestReplyManager>();

        Assert.IsType<CustomManager>(rrm);
        Assert.IsType<CustomManager>(rsrrm);
        Assert.Same(rrm, rsrrm);
    }

    // Case B: user pre-registers ONLY IRequestReplyManager without the secondary interface.
    // Without a guard the container would resolve outgoing requests to the user's impl while
    // IReplyStatusRequestReplyManager fell back to the stock RequestReplyManager — split-brain,
    // replies silently dropped. AddServiceConnect must throw InvalidOperationException at
    // configuration time (not at resolve time) with a clear message directing the user to
    // implement both interfaces or remove the custom registration.
    [Fact]
    public void AddServiceConnect_FailsFast_WhenUserPartiallyReplacesImpl()
    {
        var services = CreateMinimalServices();
        services.AddSingleton<IRequestReplyManager, PartialManager>(); // does NOT implement IReplyStatusRequestReplyManager

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddServiceConnect(ConfigureMinimal));

        Assert.Contains(nameof(IRequestReplyManager), exception.Message);
        Assert.Contains(nameof(IReplyStatusRequestReplyManager), exception.Message);
    }

    // Case C (reverse split-brain): user pre-registers ONLY IReplyStatusRequestReplyManager
    // without also pre-registering IRequestReplyManager. The stock RequestReplyManager would
    // be used for outgoing requests while the custom impl handles incoming reply correlation —
    // the two instances are unrelated and replies are silently dropped. AddServiceConnect must
    // throw InvalidOperationException at configuration time with a symmetric diagnostic message.
    [Fact]
    public void AddServiceConnect_FailsFast_WhenUserRegistersReplyStatusWithoutRequestReplyManager()
    {
        var services = CreateMinimalServices();
        services.AddSingleton<IReplyStatusRequestReplyManager>(new ReplyStatusOnlyManager());

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddServiceConnect(ConfigureMinimal));

        Assert.Contains(nameof(IReplyStatusRequestReplyManager), exception.Message);
        Assert.Contains(nameof(IRequestReplyManager), exception.Message);
    }

    private sealed class CustomManager : IRequestReplyManager, IReplyStatusRequestReplyManager
    {
        public Task<TReply> SendRequestAsync<TRequest, TReply>(
            TRequest message,
            IDictionary<string, string> headers,
            RequestOptions options,
            CancellationToken cancellationToken = default)
            where TRequest : Message
            where TReply : Message =>
            throw new NotImplementedException();

        public Task<IList<TReply>> SendRequestMultiAsync<TRequest, TReply>(
            TRequest message,
            IDictionary<string, string> headers,
            RequestOptions options,
            CancellationToken cancellationToken = default)
            where TRequest : Message
            where TReply : Message =>
            throw new NotImplementedException();

        public Task PublishRequestAsync<TRequest, TReply>(
            TRequest message,
            IDictionary<string, string> headers,
            RequestOptions options,
            Action<TReply> onReply,
            CancellationToken cancellationToken = default)
            where TRequest : Message
            where TReply : Message =>
            throw new NotImplementedException();

        public void ProcessReply(string messageId, ReadOnlyMemory<byte> messageBytes, Type type) =>
            throw new NotImplementedException();

        public bool TryProcessReply(string messageId, ReadOnlyMemory<byte> messageBytes, Type type) =>
            throw new NotImplementedException();

        public bool IsTrackedRequest(string messageId) =>
            throw new NotImplementedException();
    }

    private sealed class PartialManager : IRequestReplyManager
    {
        public Task<TReply> SendRequestAsync<TRequest, TReply>(
            TRequest message,
            IDictionary<string, string> headers,
            RequestOptions options,
            CancellationToken cancellationToken = default)
            where TRequest : Message
            where TReply : Message =>
            throw new NotImplementedException();

        public Task<IList<TReply>> SendRequestMultiAsync<TRequest, TReply>(
            TRequest message,
            IDictionary<string, string> headers,
            RequestOptions options,
            CancellationToken cancellationToken = default)
            where TRequest : Message
            where TReply : Message =>
            throw new NotImplementedException();

        public Task PublishRequestAsync<TRequest, TReply>(
            TRequest message,
            IDictionary<string, string> headers,
            RequestOptions options,
            Action<TReply> onReply,
            CancellationToken cancellationToken = default)
            where TRequest : Message
            where TReply : Message =>
            throw new NotImplementedException();

        public void ProcessReply(string messageId, ReadOnlyMemory<byte> messageBytes, Type type) =>
            throw new NotImplementedException();
    }

    // Only implements the internal half — used to test the reverse split-brain guard.
    private sealed class ReplyStatusOnlyManager : IReplyStatusRequestReplyManager
    {
        public bool TryProcessReply(string messageId, ReadOnlyMemory<byte> messageBytes, Type type) =>
            throw new NotImplementedException();

        public bool IsTrackedRequest(string messageId) =>
            throw new NotImplementedException();
    }
}
