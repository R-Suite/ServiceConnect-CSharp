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

public class AggregatorProcessorTimerDisposeRaceTests
{
    [Fact]
    public void OnTimerFired_AfterDispose_DoesNotLeakActiveFlushEntry()
    {
        // Pre-set _disposed so OnTimerFired observes the disposed state immediately
        // after its TryAdd. The fix's post-TryAdd re-check must remove the entry
        // and complete the tcs as cancelled.
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

        var disposedField = typeof(AggregatorProcessor).GetField("_disposed",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        disposedField.SetValue(processor, 1);

        var activeFlushes = (ConcurrentDictionary<int, Task>)typeof(AggregatorProcessor)
            .GetField("_activeFlushes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(processor)!;

        var onTimerFired = typeof(AggregatorProcessor).GetMethod(
            "OnTimerFired", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var descriptor = new AggregatorDescriptor(
            MessageType: typeof(object),
            AggregatorBaseType: typeof(object),
            AggregatorName: "race-test",
            BatchSize: 0,
            Timeout: TimeSpan.FromMilliseconds(50),
            BuildTypedList: _ => new List<object>(),
            InvokeExecuteAsync: (_, _, _) => Task.CompletedTask);

        // Invoke the timer callback synchronously — same path the real Timer would take.
        onTimerFired.Invoke(processor, [descriptor]);

        // The post-TryAdd re-check must have removed the entry. _activeFlushes is empty
        // (no leaked registration) and no flush task was started.
        Assert.Empty(activeFlushes);
    }

    [Fact]
    public async Task OnTimerFired_RegistrationVisibleToConcurrentDispose()
    {
        // Verify the contract: a TryAdd that LANDS before DisposeAsync's snapshot is
        // awaited by DisposeAsync. We can't easily prove the race-window is closed in
        // a unit test (it's an interleaving), but we CAN prove the happy path: a
        // pre-Dispose registration is drained.
        var services = new ServiceCollection();
        var provider = services.BuildServiceProvider();
        var registry = new AggregatorRegistry(
            [],
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AggregatorRegistry>.Instance);
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var scopeAccessor = new ConsumeScopeAccessor();

        var flushBlock = new TaskCompletionSource();
        var persistorMock = new Mock<IAggregatorPersistor>();
        persistorMock.Setup(p => p.GetSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) =>
            {
                await flushBlock.Task.WaitAsync(ct).ConfigureAwait(false);
                return AggregatorSnapshot.Empty as IAggregatorSnapshot;
            });

        var processor = new AggregatorProcessor(
            registry, scopeAccessor, scopeFactory,
            NullLogger<AggregatorProcessor>.Instance, persistorMock.Object);

        var onTimerFired = typeof(AggregatorProcessor).GetMethod(
            "OnTimerFired", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var descriptor = new AggregatorDescriptor(
            MessageType: typeof(object),
            AggregatorBaseType: typeof(object),
            AggregatorName: "drain-test",
            BatchSize: 0,
            Timeout: TimeSpan.FromMilliseconds(50),
            BuildTypedList: _ => new List<object>(),
            InvokeExecuteAsync: (_, _, _) => Task.CompletedTask);

        onTimerFired.Invoke(processor, [descriptor]);

        // The flush is now awaiting flushBlock. DisposeAsync should drain it (cancelling).
        var disposeTask = processor.DisposeAsync().AsTask();

        // Unblock the persistor with cancellation.
        flushBlock.TrySetCanceled();

        // Dispose completes within a bounded window — the registration was drained.
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
