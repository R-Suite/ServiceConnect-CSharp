using System.Globalization;
using ServiceConnect.Examples.StressHarness.Chaos;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Chaos;

public class ChaosSchedulerTests
{
    [Fact]
    public async Task RunAsync_KillsRestartsAndAdvancesClock_ThenRecordsEvent()
    {
        var fake = new RecordingBrokerChaos();
        var clock = new ChaosClock();
        var scheduler = new ChaosScheduler(
            chaos: fake,
            clock: clock,
            nodeName: "rabbitmq",
            interval: TimeSpan.FromMilliseconds(20),
            downtime: TimeSpan.FromMilliseconds(20));

        using var cts = new CancellationTokenSource();

        var task = scheduler.RunAsync(cts.Token);

        await Task.Delay(100);
        cts.Cancel();
        await task;

        Assert.True(fake.KillCount >= 1, string.Create(CultureInfo.InvariantCulture, $"expected at least 1 kill, got {fake.KillCount}"));
        Assert.True(fake.RestartCount >= 1, string.Create(CultureInfo.InvariantCulture, $"expected at least 1 restart, got {fake.RestartCount}"));
        Assert.True(scheduler.Events.Count >= 1, string.Create(CultureInfo.InvariantCulture, $"expected at least 1 event, got {scheduler.Events.Count}"));
        Assert.Equal("rabbitmq", scheduler.Events[0].NodeName);
    }

    [Fact]
    public async Task RunAsync_HonoursCancellation_BeforeFirstKill()
    {
        var fake = new RecordingBrokerChaos();
        var clock = new ChaosClock();
        var scheduler = new ChaosScheduler(
            chaos: fake,
            clock: clock,
            nodeName: "rabbitmq",
            interval: TimeSpan.FromSeconds(10),
            downtime: TimeSpan.FromSeconds(5));

        using var cts = new CancellationTokenSource();
        var task = scheduler.RunAsync(cts.Token);

        cts.Cancel();
        await task;

        Assert.Equal(0, fake.KillCount);
        Assert.Empty(scheduler.Events);
    }

    private sealed class RecordingBrokerChaos : IBrokerChaos
    {
        public int KillCount { get; private set; }
        public int RestartCount { get; private set; }

        public Task KillNodeAsync(string nodeName, CancellationToken cancellationToken)
        {
            KillCount++;
            return Task.CompletedTask;
        }

        public Task RestartNodeAsync(string nodeName, CancellationToken cancellationToken)
        {
            RestartCount++;
            return Task.CompletedTask;
        }

        public Task PartitionAsync(string nodeName, TimeSpan duration, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
