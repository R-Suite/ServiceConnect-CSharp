using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class RabbitMqTopologyProvisionerRetryDlxAutoDeleteTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfigureRetryTopologyAsync_DlxIsAlwaysAutoDeleteFalse_RegardlessOfCallerAutoDelete(bool callerAutoDelete)
    {
        bool? capturedDlxAutoDelete = null;
        var channel = new Mock<IChannel>();
        channel
            .Setup(c => c.ExchangeDeclareAsync(
                It.Is<string>(s => s.EndsWith(".Retries.DeadLetter", StringComparison.Ordinal)),
                ExchangeType.Direct,
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>>(),
                false, false, It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, bool, IDictionary<string, object?>, bool, bool, CancellationToken>(
                (_, _, _, autoDelete, _, _, _, _) => capturedDlxAutoDelete = autoDelete)
            .Returns(Task.CompletedTask);
        // Other ExchangeDeclareAsync / QueueDeclareAsync / QueueBindAsync calls accept anything.
        channel
            .Setup(c => c.QueueDeclareAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>>(), false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueueDeclareOk("q", 0, 0));
        channel
            .Setup(c => c.QueueBindAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var provisioner = new RabbitMqTopologyProvisioner(NullLogger.Instance);
        await provisioner.ConfigureRetryTopologyAsync(
            channel.Object,
            queueName: "main-q",
            durable: true,
            autoDelete: callerAutoDelete,
            retryDelayMs: 1000,
            retryQueueArguments: new Dictionary<string, object?>(),
            isInitialSetup: true);

        Assert.False(capturedDlxAutoDelete);
    }
}
