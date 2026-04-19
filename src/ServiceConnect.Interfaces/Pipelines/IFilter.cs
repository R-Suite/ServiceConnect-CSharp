namespace ServiceConnect.Interfaces;

/// <summary>
/// A filter that inspects or modifies messages as they pass through the pipeline.
/// </summary>
public interface IFilter
{
    /// <summary>
    /// Processes the given envelope. Returns <c>true</c> to <b>continue</b> pipeline execution;
    /// returns <c>false</c> to <b>block</b> the message and stop further pipeline execution.
    /// </summary>
    /// <remarks>
    /// Filters that need the bus should take <see cref="IBus"/> as a constructor dependency
    /// and be registered in DI (the previous <c>IFilter.Bus</c> property was never populated
    /// by the pipeline and returned null at runtime).
    /// </remarks>
    Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default);
}
