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
