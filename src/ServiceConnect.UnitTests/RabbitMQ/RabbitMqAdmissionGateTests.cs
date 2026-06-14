using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Diagnostics;
using ServiceConnect.UnitTests.Diagnostics;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Covers the public surface of <see cref="RabbitMqAdmissionGate"/>: the in-flight
/// gauge emit (paired +1/-1 with identical tags), the shutdown gate (TryAdmit returns
/// false once BeginShutdown fires), and the drain protocol (DrainAsync completes only
/// once every admitted delivery has called Release).
/// </summary>
public sealed class RabbitMqAdmissionGateTests
{
    [Fact]
    public void TryAdmit_BeforeShutdown_ReturnsTrueAndIncrementsInFlight()
    {
        var queueName = $"q-{Guid.NewGuid():N}";
        using var collector = new MetricCollector("messaging.destination.name", queueName);
        var gate = new RabbitMqAdmissionGate(queueName);

        Assert.True(gate.TryAdmit());

        var record = Assert.Single(collector.GetLongRecords(MetricNames.InFlightMessages));
        Assert.Equal(1, record.Value);
        Assert.Equal("rabbitmq", record.GetTag("messaging.system"));
        Assert.Equal(queueName, record.GetTag("messaging.destination.name"));
    }

    [Fact]
    public void Release_PairedWithAdmit_DecrementsInFlight()
    {
        var queueName = $"q-{Guid.NewGuid():N}";
        using var collector = new MetricCollector("messaging.destination.name", queueName);
        var gate = new RabbitMqAdmissionGate(queueName);

        Assert.True(gate.TryAdmit());
        gate.Release();

        var records = collector.GetLongRecords(MetricNames.InFlightMessages);
        Assert.Equal(2, records.Count);
        Assert.Equal(1, records[0].Value);
        Assert.Equal(-1, records[1].Value);
        // Both records carry identical tags — gauge balances on this contract.
        Assert.Equal(queueName, records[1].GetTag("messaging.destination.name"));
        Assert.Equal("rabbitmq", records[1].GetTag("messaging.system"));
    }

    [Fact]
    public void TryAdmit_AfterShutdownBegins_ReturnsFalseAndDoesNotEmit()
    {
        var queueName = $"q-{Guid.NewGuid():N}";
        using var collector = new MetricCollector("messaging.destination.name", queueName);
        var gate = new RabbitMqAdmissionGate(queueName);

        gate.BeginShutdown();

        Assert.False(gate.TryAdmit());
        Assert.Empty(collector.GetLongRecords(MetricNames.InFlightMessages));
    }

    [Fact]
    public async Task DrainAsync_WithNoInFlight_CompletesImmediately()
    {
        var gate = new RabbitMqAdmissionGate("q");
        await gate.DrainAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DrainAsync_WaitsForAllInFlightToRelease()
    {
        var gate = new RabbitMqAdmissionGate("q");
        Assert.True(gate.TryAdmit());
        Assert.True(gate.TryAdmit());

        gate.BeginShutdown();
        var drainTask = gate.DrainAsync(CancellationToken.None);
        Assert.False(drainTask.IsCompleted);

        gate.Release();
        Assert.False(drainTask.IsCompleted);

        gate.Release();
        await drainTask;  // completes once last release fires
    }

    [Fact]
    public async Task DrainAsync_RespectsCancellation()
    {
        var gate = new RabbitMqAdmissionGate("q");
        Assert.True(gate.TryAdmit());
        gate.BeginShutdown();

        using var cts = new CancellationTokenSource();
        var drainTask = gate.DrainAsync(cts.Token);

        await cts.CancelAsync();
        // Task.WaitAsync raises TaskCanceledException (derives from OperationCanceledException);
        // ThrowsAnyAsync accepts the derived type without coupling the test to the BCL choice.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drainTask);
    }

    [Fact]
    public void Constructor_NullQueueName_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new RabbitMqAdmissionGate(null!));
    }

    [Fact]
    public void IsShuttingDown_ReflectsBeginShutdown()
    {
        var gate = new RabbitMqAdmissionGate("q");
        Assert.False(gate.IsShuttingDown);
        gate.BeginShutdown();
        Assert.True(gate.IsShuttingDown);
    }
}
