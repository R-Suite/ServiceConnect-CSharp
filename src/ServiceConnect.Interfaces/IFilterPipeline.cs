namespace ServiceConnect.Interfaces;

/// <summary>
/// Manages the execution of outgoing and consuming filter stages.
/// </summary>
public interface IFilterPipeline
{
    /// <summary>
    /// Executes all outgoing filters. Returns <c>true</c> if any filter blocked the message.
    /// </summary>
    bool ExecuteOutgoingFilters(Envelope envelope);

    /// <summary>
    /// Executes all before-consuming filters. Returns <c>true</c> if any filter blocked the message.
    /// </summary>
    bool ExecuteBeforeConsumingFilters(Envelope envelope);

    /// <summary>
    /// Executes all after-consuming filters. Returns <c>true</c> if any filter blocked the message.
    /// </summary>
    bool ExecuteAfterConsumingFilters(Envelope envelope);
}
