using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Shared event-handler scaffolding for connection-lifecycle Info logs.
/// <see cref="Connection"/> and <c>ProducerConnection</c> each own one instance and call
/// <see cref="Attach"/> after a successful create / <see cref="Detach"/> before close, with
/// the helper supplying the captured event handlers and the <see cref="RabbitMqClientLog"/>
/// emit calls. Pre-extraction these lived as duplicated near-verbatim bodies in both
/// connection classes.
/// </summary>
/// <remarks>
/// Subscribe and unsubscribe must use the same delegate reference, otherwise
/// <c>connection.RecoverySucceededAsync -= …</c> silently no-ops and the handler leaks on
/// the original <see cref="IConnection"/> until GC. The helper captures method-group
/// conversions in fields so <see cref="Attach"/> and <see cref="Detach"/> always pass the
/// same instance.
/// </remarks>
internal sealed class ConnectionLifecycleHooks
{
    private readonly ILogger _logger;
    private readonly AsyncEventHandler<AsyncEventArgs> _onRecoverySucceeded;
    private readonly AsyncEventHandler<ShutdownEventArgs> _onConnectionShutdown;

    public ConnectionLifecycleHooks(ILogger logger)
    {
        _logger = logger;
        _onRecoverySucceeded = OnRecoverySucceededAsync;
        _onConnectionShutdown = OnConnectionShutdownAsync;
    }

    public void Attach(IConnection connection)
    {
        connection.RecoverySucceededAsync += _onRecoverySucceeded;
        connection.ConnectionShutdownAsync += _onConnectionShutdown;
    }

    public void Detach(IConnection connection)
    {
        connection.RecoverySucceededAsync -= _onRecoverySucceeded;
        connection.ConnectionShutdownAsync -= _onConnectionShutdown;
    }

    private Task OnRecoverySucceededAsync(object? sender, AsyncEventArgs e)
    {
        if (sender is IConnection connection)
        {
            var (host, port) = ResolveEndpoint(connection);
            RabbitMqClientLog.ConnectionRecovered(
                _logger,
                host,
                port,
                connection.ClientProvidedName ?? string.Empty);
        }
        return Task.CompletedTask;
    }

    private Task OnConnectionShutdownAsync(object? sender, ShutdownEventArgs e)
    {
        if (sender is IConnection connection)
        {
            var (host, port) = ResolveEndpoint(connection);
            RabbitMqClientLog.ConnectionLost(
                _logger,
                host,
                port,
                connection.ClientProvidedName ?? string.Empty,
                e.Initiator.ToString(),
                string.IsNullOrEmpty(e.ReplyText) ? "<no reason>" : e.ReplyText);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads <see cref="IConnection.Endpoint"/> with a guard for the transient null cases:
    /// RabbitMQ.Client can surface a null Endpoint mid-shutdown (the field is torn down
    /// before the IConnection itself observably disposes), and Moq <see cref="IConnection"/>
    /// proxies leave it null on the Loose default. Returns <c>("&lt;unknown&gt;", 0)</c> so
    /// the lifecycle log emits searchably rather than NREing or printing <c>:0</c>-noise.
    /// </summary>
    public static (string host, int port) ResolveEndpoint(IConnection connection)
    {
        var endpoint = connection.Endpoint;
        return endpoint is null
            ? ("<unknown>", 0)
            : (endpoint.HostName, endpoint.Port);
    }
}
