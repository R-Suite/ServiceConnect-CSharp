using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Verifies the broker-cancelled flag lifecycle on <see cref="RabbitMqChannelHost"/>:
/// the flag is set by <c>NotifyBrokerCancelled</c> and explicitly cleared by
/// <c>NotifyRecoverySucceeded</c> so that <c>IBus.IsConsuming</c> and
/// <c>BusConsumingHealthCheck</c> return to the healthy state once RabbitMQ.Client's
/// auto-recovery has restored the consumer. Reset semantics must be idempotent and the
/// flag must remain re-flippable across subsequent cancel/recover cycles.
/// </summary>
public sealed class RabbitMqChannelHostTests
{
    [Fact]
    public void NotifyRecoverySucceeded_AfterBrokerCancelled_ResetsFlag()
    {
        var host = CreateChannelHostForTest();
        host.NotifyBrokerCancelled();
        Assert.True(host.IsCancelledByBroker);

        host.NotifyRecoverySucceeded();
        Assert.False(host.IsCancelledByBroker);
    }

    [Fact]
    public void NotifyRecoverySucceeded_NeverCancelled_IsNoOp()
    {
        var host = CreateChannelHostForTest();
        Assert.False(host.IsCancelledByBroker);

        host.NotifyRecoverySucceeded();
        Assert.False(host.IsCancelledByBroker);
    }

    [Fact]
    public void NotifyRecoverySucceeded_AfterCancelAndRecover_ReFlippableOnSubsequentCancel()
    {
        var host = CreateChannelHostForTest();
        host.NotifyBrokerCancelled();
        host.NotifyRecoverySucceeded();
        Assert.False(host.IsCancelledByBroker);

        // A subsequent broker-cancel must still latch — the recovery reset is not a
        // permanent disable, only a per-cycle clear.
        host.NotifyBrokerCancelled();
        Assert.True(host.IsCancelledByBroker);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static RabbitMqChannelHost CreateChannelHostForTest()
    {
        var conn = new Mock<IServiceConnectConnection>(MockBehavior.Loose);
        return new RabbitMqChannelHost(conn.Object, NullLogger.Instance, "q");
    }
}
