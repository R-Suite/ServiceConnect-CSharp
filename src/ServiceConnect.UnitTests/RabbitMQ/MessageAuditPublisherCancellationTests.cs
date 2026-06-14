using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public class MessageAuditPublisherCancellationTests
{
    [Fact]
    public async Task PublishAuditIfEnabledAsync_AuditingDisabled_PreCancelled_ThrowsOCE()
    {
        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.AuditingEnabled).Returns(false);

        var publisher = new MessageAuditPublisher(queueConfig.Object);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            publisher.PublishAuditIfEnabledAsync(
                channel: null!,
                args: null!,
                headers: null!,
                cancellationToken: cts.Token));
    }
}
