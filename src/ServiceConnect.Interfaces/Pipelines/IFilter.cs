namespace ServiceConnect.Interfaces;

/// <summary>
/// A filter that inspects or modifies messages as they pass through the pipeline.
/// </summary>
public interface IFilter
{
    /// <summary>
    /// Processes the given envelope. Returns <see cref="FilterAction.Continue"/> to continue
    /// pipeline execution, or <see cref="FilterAction.Stop"/> to block the message and stop
    /// further pipeline execution.
    /// </summary>
    /// <remarks>
    /// Filters that need the bus should take <see cref="IBus"/> as a constructor dependency
    /// and be registered in DI (the previous <c>IFilter.Bus</c> property was never populated
    /// by the pipeline and returned null at runtime).
    /// </remarks>
    Task<FilterAction> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default);
}
