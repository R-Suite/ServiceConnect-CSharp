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

public class AggregatorProcessorFlushAfterDisposeTests
{
    [Fact]
    public async Task FlushAggregator_AfterDisposed_DoesNotInsertNewLock()
    {
        var services = new ServiceCollection();
        var provider = services.BuildServiceProvider();
        var registry = new AggregatorRegistry(
            [],
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AggregatorRegistry>.Instance);
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var scopeAccessor = new ConsumeScopeAccessor();
        var persistor = Mock.Of<IAggregatorPersistor>();

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

        // Drives FlushAggregatorAsync after _disposed has been pre-set, asserting the entry
        // fast-fail throws ODE without inserting a _flushLocks entry. The deeper
        // recovery branch (TryRemove + Dispose after a disposed-mid-GetOrAdd race)
        // is correctness-by-inspection — not exercised here, since the synthetic
        // precondition trips the entry guard before reaching it.
        // FlushAggregatorAsync signature: (descriptor, ambientScope, minThreshold, cancellationToken).
        // minThreshold of 1 mirrors the timer-path call; the batch path passes BatchSize.
        var task = (Task)flushMethod.Invoke(processor,
            [descriptor, null, 1, CancellationToken.None])!;

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await task);

        Assert.False(flushLocks.ContainsKey("post-dispose-aggregator"),
            "FlushAggregatorAsync must not install a new _flushLocks entry after Dispose");
    }
}
