namespace ServiceConnect.Interfaces.Configuration;

/// <summary>
/// Exposes the registered filter and middleware types for the message pipeline.
/// </summary>
public interface IPipelineConfiguration
{
    /// <summary>
    /// Gets the filters that run before handler invocation.
    /// </summary>
    IReadOnlyList<Type> BeforeConsumingFilters { get; }

    /// <summary>
    /// Gets the filters that run after handler invocation.
    /// </summary>
    IReadOnlyList<Type> AfterConsumingFilters { get; }

    /// <summary>
    /// Gets the filters that run only after a successful handler invocation
    /// (the dispatcher chain returned <see cref="ConsumeEventResult.Success"/> = true
    /// and <see cref="ConsumeEventResult.NotHandled"/> = false). Filters in this stage
    /// observe successful consumption only; failures and unhandled messages skip them.
    /// </summary>
    IReadOnlyList<Type> OnConsumedSuccessfullyFilters { get; }

    /// <summary>
    /// Gets the filters that run on outgoing messages.
    /// </summary>
    IReadOnlyList<Type> OutgoingFilters { get; }

    /// <summary>
    /// Gets the middleware types that wrap message processing.
    /// </summary>
    IReadOnlyList<Type> MessageProcessingMiddleware { get; }

    /// <summary>
    /// Gets the middleware types that wrap outgoing send and publish operations.
    /// </summary>
    IReadOnlyList<Type> SendMessageMiddleware { get; }
}
