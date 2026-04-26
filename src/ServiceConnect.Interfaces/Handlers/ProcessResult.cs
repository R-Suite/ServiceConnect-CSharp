namespace ServiceConnect.Interfaces;

/// <summary>
/// Describes the outcome of a message processor invocation.
/// </summary>
public enum ProcessResult
{
    /// <summary>
    /// The processor handled the message and processing should stop.
    /// </summary>
    Handled,

    /// <summary>
    /// The processor did not handle the message and processing may continue.
    /// </summary>
    NotHandled
}
