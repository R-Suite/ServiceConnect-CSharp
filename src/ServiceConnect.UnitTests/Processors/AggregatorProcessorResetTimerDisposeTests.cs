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

public class AggregatorProcessorResetTimerDisposeTests
{
    [Fact]
    public void ResetTimer_AfterDispose_DoesNotInstallTimer()
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

        // Pre-set _disposed so ResetTimer sees the disposed state under the lock.
        var disposedField = typeof(AggregatorProcessor).GetField("_disposed",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        disposedField.SetValue(processor, 1);

        var timers = (ConcurrentDictionary<string, ITimer>)typeof(AggregatorProcessor)
            .GetField("_timers", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(processor)!;

        var resetTimerMethod = typeof(AggregatorProcessor).GetMethod(
            "ResetTimer", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var descriptor = new AggregatorDescriptor(
            MessageType: typeof(object),
            AggregatorBaseType: typeof(object),
            AggregatorName: "post-dispose",
            BatchSize: 0,
            Timeout: TimeSpan.FromMilliseconds(50),
            BuildTypedList: _ => new List<object>(),
            InvokeExecuteAsync: (_, _, _) => Task.CompletedTask);

        resetTimerMethod.Invoke(processor, [descriptor]);

        Assert.False(timers.ContainsKey("post-dispose"),
            "ResetTimer must not install a Timer after Dispose");
        Assert.Empty(timers);
    }

    [Fact]
    public async Task DisposeAsync_DrainsConcurrentResetTimer()
    {
        // Concurrent ResetTimer calls during DisposeAsync must not leak Timers past
        // the disposal foreach. Run a tight race and assert _timers is empty after
        // both DisposeAsync and the racer return.
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

        var resetTimerMethod = typeof(AggregatorProcessor).GetMethod(
            "ResetTimer", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var timers = (ConcurrentDictionary<string, ITimer>)typeof(AggregatorProcessor)
            .GetField("_timers", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(processor)!;

        // Spawn racers that hammer ResetTimer for distinct aggregator names.
        var racers = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            var descriptor = new AggregatorDescriptor(
                MessageType: typeof(object),
                AggregatorBaseType: typeof(object),
                AggregatorName: $"racer-{i}",
                BatchSize: 0,
                Timeout: TimeSpan.FromMilliseconds(50),
                BuildTypedList: _ => new List<object>(),
                InvokeExecuteAsync: (_, _, _) => Task.CompletedTask);

            for (var j = 0; j < 50; j++)
            {
                resetTimerMethod.Invoke(processor, [descriptor]);
            }
        })).ToArray();

        // Concurrently dispose.
        await processor.DisposeAsync();
        await Task.WhenAll(racers);

        Assert.Empty(timers);
    }
}
