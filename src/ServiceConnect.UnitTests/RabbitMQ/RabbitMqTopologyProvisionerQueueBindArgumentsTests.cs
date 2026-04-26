using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public class RabbitMqTopologyProvisionerQueueBindArgumentsTests
{
    private static (Mock<IChannel> Channel, RabbitMqTopologyProvisioner Provisioner) CreateProvisioner()
    {
        var channel = new Mock<IChannel>(MockBehavior.Loose);
        var provisioner = new RabbitMqTopologyProvisioner(NullLogger.Instance);
        return (channel, provisioner);
    }

    [Fact]
    public async Task ConfigureDeclareUtilityQueueAsync_QueueBindAsync_ReceivesNullArguments()
    {
        var (channel, provisioner) = CreateProvisioner();
        var capturedArgs = new List<IDictionary<string, object?>?>();

        channel.Setup(c => c.QueueBindAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IDictionary<string, object?>, bool, CancellationToken>(
                (_, _, _, args, _, _) => capturedArgs.Add(args))
            .Returns(Task.CompletedTask);

        var queueArguments = new Dictionary<string, object?> { { "x-message-ttl", 60000 } };
        await provisioner.ConfigureDeclareUtilityQueueAsync(channel.Object, "some-queue", queueArguments, isInitialSetup: false);

        Assert.NotEmpty(capturedArgs);
        Assert.All(capturedArgs, args => Assert.Null(args));
    }

    [Fact]
    public async Task ConfigureRetryTopologyAsync_QueueBindAsync_ReceivesNullArguments()
    {
        var (channel, provisioner) = CreateProvisioner();
        var capturedArgs = new List<IDictionary<string, object?>?>();

        channel.Setup(c => c.QueueBindAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IDictionary<string, object?>, bool, CancellationToken>(
                (_, _, _, args, _, _) => capturedArgs.Add(args))
            .Returns(Task.CompletedTask);

        var retryQueueArguments = new Dictionary<string, object?>();
        await provisioner.ConfigureRetryTopologyAsync(
            channel.Object,
            queueName: "my-queue",
            durable: true,
            autoDelete: false,
            retryDelayMs: 5000,
            retryQueueArguments: retryQueueArguments,
            isInitialSetup: false);

        Assert.NotEmpty(capturedArgs);
        Assert.All(capturedArgs, args => Assert.Null(args));
    }
}
