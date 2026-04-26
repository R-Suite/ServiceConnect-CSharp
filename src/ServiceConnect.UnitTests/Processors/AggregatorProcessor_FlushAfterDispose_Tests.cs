using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class AggregatorProcessor_FlushAfterDispose_Tests
{
    [Fact]
    public async Task FlushAggregator_AfterDisposed_DoesNotInsertNewLock()
    {
        var services = new ServiceCollection();
        var provider = services.BuildServiceProvider();
        var registry = new AggregatorRegistry(
            new List<HandlerReference>(),
            provider,
            NullLogger<AggregatorRegistry>.Instance);
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var scopeAccessor = new ConsumeScopeAccessor();
        var persistor = new Mock<IAggregatorPersistor>(MockBehavior.Strict).Object;

        var processor = new AggregatorProcessor(
            registry, scopeAccessor, scopeFactory,
            NullLogger<AggregatorProcessor>.Instance, persistor);

        // Pre-set _disposed to 1 so that DisposeAsync bookkeeping (draining _activeFlushes,
        // clearing _flushLocks) has been skipped — simulating the race where a timer callback
        // reaches FlushAggregatorAsync after DisposeAsync's Clear() has already run.
        var disposedField = typeof(AggregatorProcessor).GetField("_disposed",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        disposedField.SetValue(processor, 1);

        var flushLocks = (ConcurrentDictionary<string, SemaphoreSlim>)typeof(AggregatorProcessor)
            .GetField("_flushLocks", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(processor)!;

        var flushMethod = typeof(AggregatorProcessor).GetMethod(
            "FlushAggregatorAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // Build a descriptor with a known name; BuildTypedList and InvokeExecuteAsync will
        // never be reached because we expect the method to throw before acquiring the lock.
        var descriptor = new AggregatorDescriptor(
            MessageType: typeof(object),
            AggregatorBaseType: typeof(object),
            AggregatorName: "post-dispose-aggregator",
            BatchSize: 0,
            Timeout: TimeSpan.Zero,
            BuildTypedList: _ => new List<object>(),
            InvokeExecuteAsync: (_, _, _) => Task.CompletedTask);

        var task = (Task)flushMethod.Invoke(processor,
            new object?[] { descriptor, null, CancellationToken.None })!;

        await Assert.ThrowsAnyAsync<Exception>(async () => await task);

        Assert.False(flushLocks.ContainsKey("post-dispose-aggregator"),
            "FlushAggregatorAsync must not install a new _flushLocks entry after Dispose");
    }
}
