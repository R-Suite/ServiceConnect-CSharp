namespace ServiceConnect.Interfaces;

/// <summary>
/// Manages the execution of outgoing and consuming filter stages.
/// </summary>
public interface IFilterPipeline
{
    /// <summary>
    /// Executes all outgoing filters. Returns <see cref="FilterAction.Stop"/> if any filter
    /// blocked the message; otherwise <see cref="FilterAction.Continue"/>.
    /// </summary>
    Task<FilterAction> ExecuteOutgoingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes all before-consuming filters. Returns <see cref="FilterAction.Stop"/> if any
    /// filter blocked the message; otherwise <see cref="FilterAction.Continue"/>.
    /// </summary>
    Task<FilterAction> ExecuteBeforeConsumingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes all after-consuming filters. Returns <see cref="FilterAction.Stop"/> if any
    /// filter blocked the message; otherwise <see cref="FilterAction.Continue"/>.
    /// </summary>
    Task<FilterAction> ExecuteAfterConsumingFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes all on-consumed-successfully filters. Returns
    /// <see cref="FilterAction.Stop"/> if any filter halted the pipeline;
    /// otherwise <see cref="FilterAction.Continue"/>. The dispatcher invokes this
    /// stage only after a successful handler — failures and unhandled messages
    /// skip it. <see cref="FilterAction.Stop"/> halts further on-success filters
    /// but does not flip the dispatch result to failure.
    /// </summary>
    Task<FilterAction> ExecuteOnConsumedSuccessfullyFiltersAsync(Envelope envelope, CancellationToken cancellationToken = default);
}
