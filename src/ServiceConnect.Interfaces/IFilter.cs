namespace ServiceConnect.Interfaces;

/// <summary>
/// A filter that inspects or modifies messages as they pass through the pipeline.
/// </summary>
public interface IFilter
{
    /// <summary>
    /// The bus instance available for use by the filter.
    /// </summary>
    IBus Bus { get; set; }

    /// <summary>
    /// Processes the given envelope. Returns <c>true</c> to <b>block</b> the message
    /// and stop further pipeline execution; returns <c>false</c> to continue processing.
    /// </summary>
    bool Process(Envelope envelope);
}
