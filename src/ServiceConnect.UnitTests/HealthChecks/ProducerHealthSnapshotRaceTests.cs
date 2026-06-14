using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

public class ProducerHealthSnapshotRaceTests
{
    [Fact]
    public async Task CheckHealthAsync_UsesSnapshot_NotIndividualPropertyReads()
    {
        // Construct a producer mock where IsHealthy and HasAttemptedConnection return
        // values that would surface the race (IsHealthy=false, HasAttemptedConnection=true)
        // when read separately, but GetHealthSnapshot returns a consistent pair.
        var producer = new Mock<IProducer>();
        producer.Setup(p => p.IsHealthy).Returns(false);
        producer.Setup(p => p.HasAttemptedConnection).Returns(true);
        producer.Setup(p => p.GetHealthSnapshot())
            .Returns(new ProducerHealthSnapshot(IsHealthy: true, HasAttemptedConnection: true));

        var check = new ProducerConnectionHealthCheck(producer.Object);
        var ctx = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("p", check, HealthStatus.Unhealthy, null),
        };
        var result = await check.CheckHealthAsync(ctx);

        // Healthy because the snapshot reports healthy; if the check were reading the
        // individual properties it would have reported Unhealthy.
        Assert.Equal(HealthStatus.Healthy, result.Status);
        producer.Verify(p => p.GetHealthSnapshot(), Times.AtLeastOnce);
        producer.VerifyGet(p => p.IsHealthy, Times.Never);
        producer.VerifyGet(p => p.HasAttemptedConnection, Times.Never);
    }

    [Fact]
    public async Task CheckHealthAsync_LazyState_ReturnsHealthy()
    {
        var producer = new Mock<IProducer>();
        producer.Setup(p => p.GetHealthSnapshot())
            .Returns(new ProducerHealthSnapshot(IsHealthy: false, HasAttemptedConnection: false));

        var check = new ProducerConnectionHealthCheck(producer.Object);
        var ctx = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("p", check, HealthStatus.Unhealthy, null),
        };
        var result = await check.CheckHealthAsync(ctx);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("not yet attempted", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckHealthAsync_AttemptedAndFailed_ReturnsUnhealthy()
    {
        var producer = new Mock<IProducer>();
        producer.Setup(p => p.GetHealthSnapshot())
            .Returns(new ProducerHealthSnapshot(IsHealthy: false, HasAttemptedConnection: true));

        var check = new ProducerConnectionHealthCheck(producer.Object);
        var ctx = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("p", check, HealthStatus.Unhealthy, null),
        };
        var result = await check.CheckHealthAsync(ctx);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    /// <summary>
    /// Sanity: third-party IProducer impls that don't override GetHealthSnapshot get
    /// the default impl which reads IsHealthy+HasAttemptedConnection as two separate
    /// reads — those impls retain the torn-read race; first-party (RabbitMQ) producers
    /// override to return an atomic snapshot.
    /// </summary>
    [Fact]
    public void IProducerDefaultImplementation_DerivesSnapshotFromTwoReads()
    {
        IProducer producer = new StubProducer { IsHealthy = true, HasAttemptedConnection = true };
        var snapshot = producer.GetHealthSnapshot();
        Assert.True(snapshot.IsHealthy);
        Assert.True(snapshot.HasAttemptedConnection);
    }

    /// <summary>
    /// Minimal IProducer stub for default-interface-method probing. Other members
    /// throw because they're not exercised by these tests.
    /// </summary>
    private sealed class StubProducer : IProducer
    {
        public bool IsHealthy { get; init; }
        public bool HasAttemptedConnection { get; init; }
        public long MaximumMessageSize => 0;
        public Task PublishAsync(Type type, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SendAsync(Type type, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SendAsync(string endPoint, Type type, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SendBytesAsync(string endPoint, Type type, ReadOnlyMemory<byte> packet, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
