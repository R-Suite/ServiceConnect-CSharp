namespace ServiceConnect.Interfaces;

/// <summary>
/// The outcome of a filter or filter-pipeline invocation.
/// </summary>
public enum FilterAction
{
    /// <summary>
    /// Continue pipeline execution. The next filter runs, or — when emitted by the pipeline — the
    /// caller proceeds with the publish/send/dispatch the filters guarded.
    /// </summary>
    Continue,

    /// <summary>
    /// Stop pipeline execution. No subsequent filters run, and the caller short-circuits the
    /// guarded operation.
    /// </summary>
    Stop,
}
