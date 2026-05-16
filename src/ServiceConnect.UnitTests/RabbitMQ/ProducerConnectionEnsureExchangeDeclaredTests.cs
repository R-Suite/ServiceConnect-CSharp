using System.Reflection;
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
        // Arrange: ProducerConnection with _model forced to null (simulates a concurrent
        // TearDownChannelAndConnectionAsync racing between generation snapshot and the declare call).
        var connection = CreateProducerConnection();

        var modelField = typeof(ProducerConnection).GetField(
            "_model", BindingFlags.NonPublic | BindingFlags.Instance)!;
        modelField.SetValue(connection, null);

        // Act & Assert: the null channel should surface as ChannelTransientException so
        // the retry classifier in ExecuteRetryingPublishAsync skips MarkResetRequired.
        await Assert.ThrowsAsync<ChannelTransientException>(
            () => connection.EnsureExchangeDeclaredAsync("test-exchange", "fanout", CancellationToken.None));
    }
}
