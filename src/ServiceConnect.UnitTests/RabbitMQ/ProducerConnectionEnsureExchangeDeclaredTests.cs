using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Verifies that <see cref="ProducerConnection.EnsureExchangeDeclaredAsync"/> throws
/// <see cref="ChannelTransientException"/> when <c>_model</c> is null at the point of
/// the declare call, rather than a <see cref="NullReferenceException"/>.
/// </summary>
public sealed class ProducerConnectionEnsureExchangeDeclaredTests
{
    private static ProducerConnection CreateProducerConnection()
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        return new ProducerConnection(transport.Object, NullLogger.Instance);
    }

    [Fact]
    public async Task EnsureExchangeDeclaredAsync_WhenChannelIsNull_ThrowsChannelTransientException()
    {
        // _model is null at construction (no initializer) — same observable state left by
        // TearDownChannelAndConnectionAsync. That covers both the never-connected case
        // and the torn-down-between-snapshot-and-declare race: TryGetChannel() returns
        // null in both, and the production code must surface ChannelTransientException
        // rather than NRE so the retry classifier in ExecuteRetryingPublishAsync skips
        // MarkResetRequired.
        var connection = CreateProducerConnection();

        await Assert.ThrowsAsync<ChannelTransientException>(
            () => connection.EnsureExchangeDeclaredAsync("test-exchange", "fanout", CancellationToken.None));
    }
}
