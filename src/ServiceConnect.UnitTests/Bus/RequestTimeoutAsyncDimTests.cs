using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using Xunit;

namespace ServiceConnect.UnitTests.BusInterface;

public class RequestTimeoutAsyncDimTests
{
    private sealed class StubBus : IBus
    {
        public Task PublishAsync<T>(T message, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : Message => Task.CompletedTask;
        public Task SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default) where T : Message => Task.CompletedTask;
        public Task SendToManyAsync<T>(T message, IReadOnlyList<string> endPoints, SendOptions? options = null, CancellationToken cancellationToken = default) where T : Message => Task.CompletedTask;
        public Task<TReply> SendRequestAsync<TRequest, TReply>(TRequest message, RequestOptions? options = null, CancellationToken cancellationToken = default)
            where TRequest : Message where TReply : Message => Task.FromResult<TReply>(default!);
        public Task<IList<TReply>> SendRequestMultiAsync<TRequest, TReply>(TRequest message, RequestOptions? options = null, CancellationToken cancellationToken = default)
            where TRequest : Message where TReply : Message => Task.FromResult<IList<TReply>>([]);
        public Task PublishRequestAsync<TRequest, TReply>(TRequest message, Action<TReply> onReply, RequestOptions? options = null, CancellationToken cancellationToken = default)
            where TRequest : Message where TReply : Message => Task.CompletedTask;
        public Task RouteAsync<T>(T message, IReadOnlyList<string> destinations, CancellationToken cancellationToken = default) where T : Message => Task.CompletedTask;
        public IMessageBusWriteStream CreateStream<T>(string endpoint) where T : Message => throw new NotImplementedException();
        public Task StartConsumingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopConsumingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool IsConsuming => false;
        public ValueTask DisposeAsync() => default;
        // RequestTimeoutAsync NOT overridden — falls through to the DIM.
    }

    [Fact]
    public async Task RequestTimeoutAsync_DimNotOverridden_DefersExceptionUntilAwait()
    {
        IBus bus = new StubBus();

        // Pre-fix: throws synchronously on the call line.
        // Post-fix: returns a faulted Task; exception observed at await.
        Task task = bus.RequestTimeoutAsync(Guid.NewGuid(), TimeSpan.FromSeconds(1));
        Assert.NotNull(task);  // synchronous throw would prevent reaching here

        await Assert.ThrowsAsync<NotSupportedException>(async () => await task);
    }
}
