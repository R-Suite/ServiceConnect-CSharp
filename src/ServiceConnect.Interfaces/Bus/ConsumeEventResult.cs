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
    /// Gets or sets a value indicating whether the dispatcher ran to completion but no
    /// processor claimed the message. Distinct from <see cref="Success"/> because a
    /// handler-less message is not a failure, but callers may want to route it to the
    /// error exchange instead of silently acking (see
    /// <see cref="Interfaces.Configuration.IBusConfiguration.DeadLetterUnhandledMessages"/>).
    /// </summary>
    public bool NotHandled { get; set; }

    /// <summary>
    /// Gets or sets the exception raised by the consumer, if any.
    /// </summary>
    public Exception? Exception { get; set; }
}
