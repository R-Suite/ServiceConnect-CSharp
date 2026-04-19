namespace ServiceConnect.Interfaces;

/// <summary>
/// Represents the outcome of invoking a consumer callback.
/// </summary>
public sealed class ConsumeEventResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the consumer completed successfully.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the exception raised by the consumer, if any.
    /// </summary>
    public Exception? Exception { get; set; }
}
