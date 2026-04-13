using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Facade over <see cref="RabbitMqConsumerHost"/>. Public shape retained so existing
/// transport-factory code keeps compiling without changes.
/// </summary>
public sealed class Client : IAsyncDisposable
{
    private readonly RabbitMqConsumerHost _host;

    public Client(
        IServiceConnectConnection connection,
        ITransportConfiguration transportConfiguration,
        IQueueConfiguration queueConfiguration,
        ILogger logger)
    {
        var retryHandler = new MessageRetryHandler(
            transportConfiguration.MaxRetries, queueConfiguration.ErrorQueueName, logger);
        var auditPublisher = new MessageAuditPublisher(queueConfiguration);
        _host = new RabbitMqConsumerHost(
            connection, transportConfiguration, queueConfiguration,
            retryHandler, auditPublisher, logger);
    }

    public Task StartConsumingAsync(
        ConsumerEventHandler messageReceived, string queueName,
        bool? exclusive = null, bool? autoDelete = null, CancellationToken cancellationToken = default)
        => _host.StartConsumingAsync(messageReceived, queueName, exclusive, autoDelete, cancellationToken);

    public Task ConsumeMessageTypeAsync(string messageTypeName) => _host.ConsumeMessageTypeAsync(messageTypeName);

    public ValueTask DisposeAsync() => _host.DisposeAsync();
}
