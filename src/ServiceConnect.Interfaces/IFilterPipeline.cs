namespace ServiceConnect.Interfaces;

/// <summary>
/// Manages the execution of outgoing and consuming filter stages.
/// </summary>
public interface IFilterPipeline
{
    /// <summary>
    /// Executes all outgoing filters. Returns <c>true</c> if any filter blocked the message.
    /// </summary>
    Task<bool> ExecuteOutgoingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes all before-consuming filters. Returns <c>true</c> if any filter blocked the message.
    /// </summary>
    Task<bool> ExecuteBeforeConsumingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes all after-consuming filters. Returns <c>true</c> if any filter blocked the message.
    /// </summary>
    Task<bool> ExecuteAfterConsumingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default);
}
