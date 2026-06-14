namespace ServiceConnect.Interfaces;

/// <summary>
/// Represents the outcome of invoking a consumer callback. Framework-produced and
/// consumer-observed only; user mutation after construction has no defined effect.
/// </summary>
public sealed class ConsumeEventResult
{
    /// <summary>
    /// Gets a value indicating whether the consumer completed successfully.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets a value indicating whether the dispatcher ran to completion but no
    /// processor claimed the message. Distinct from <see cref="Success"/> because a
    /// handler-less message is not a failure, but callers may want to route it to the
    /// error exchange instead of silently acking (see
    /// <see cref="Interfaces.Configuration.IBusConfiguration.DeadLetterUnhandledMessages"/>).
    /// </summary>
    public bool NotHandled { get; init; }

    /// <summary>
    /// Gets the exception raised by the consumer, if any.
    /// </summary>
    public Exception? Exception { get; init; }

    /// <summary>
    /// Gets a value indicating whether the failure is terminal — the message is permanently
    /// malformed (e.g. unparseable wire payload) and retrying will produce the identical
    /// failure. Distinct from <see cref="Success"/>=false: a terminal failure must bypass the
    /// retry queue and route directly to the error exchange, so the retry budget is not
    /// burned on a poison payload that no amount of redelivery will fix.
    /// </summary>
    /// <remarks>
    /// Set by the dispatcher when a structural fault is observed: payload-level deserialisation
    /// failures (<see cref="System.Text.Json.JsonException"/>, <see cref="NotSupportedException"/>
    /// from a converter mismatch). Handler-thrown exceptions remain non-terminal — those reflect
    /// the handler's dependencies and SHOULD retry. Transports that don't honour this flag fall
    /// back to the normal retry path.
    /// </remarks>
    public bool TerminalFailure { get; init; }
}
