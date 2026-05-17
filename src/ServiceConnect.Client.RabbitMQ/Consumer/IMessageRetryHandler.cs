using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Abstraction over the terminal-failure publish path used by
/// <see cref="RabbitMqHeaderValidator"/>. Exposing only the single method the
/// validator calls keeps the interface narrow and allows the concrete
/// <see cref="MessageRetryHandler"/> to remain sealed.
/// </summary>
internal interface IMessageRetryHandler
{
    /// <summary>
    /// Routes a permanently-invalid delivery to the error exchange. The caller
    /// is responsible for acking the original inbound delivery after this returns.
    /// </summary>
    Task HandleTerminalFailureAsync(
        IChannel channel,
        BasicDeliverEventArgs args,
        Dictionary<string, object> headers,
        Exception ex,
        CancellationToken cancellationToken = default);
}
