using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Configuration;

/// <summary>
/// Mutable implementation of <see cref="IPipelineConfiguration"/> that stores filter and middleware type registrations.
/// </summary>
public sealed class PipelineConfiguration : IPipelineConfiguration
{
    /// <summary>
    /// Gets the filters that run before handler invocation.
    /// </summary>
    public List<Type> BeforeConsumingFilters { get; } = [];
    /// <summary>
    /// Gets the filters that run after handler invocation.
    /// </summary>
    public List<Type> AfterConsumingFilters { get; } = [];
    /// <summary>
    /// Gets the filters that run for outgoing messages.
    /// </summary>
    public List<Type> OutgoingFilters { get; } = [];
    /// <summary>
    /// Gets the middleware types that wrap inbound message processing.
    /// </summary>
    public List<Type> MessageProcessingMiddleware { get; } = [];
    /// <summary>
    /// Gets the middleware types that wrap outbound send and publish operations.
    /// </summary>
    public List<Type> SendMessageMiddleware { get; } = [];

    // Explicit interface implementations to satisfy the IReadOnlyList<Type> contract
    IReadOnlyList<Type> IPipelineConfiguration.BeforeConsumingFilters => BeforeConsumingFilters;
    IReadOnlyList<Type> IPipelineConfiguration.AfterConsumingFilters => AfterConsumingFilters;
    IReadOnlyList<Type> IPipelineConfiguration.OutgoingFilters => OutgoingFilters;
    IReadOnlyList<Type> IPipelineConfiguration.MessageProcessingMiddleware => MessageProcessingMiddleware;
    IReadOnlyList<Type> IPipelineConfiguration.SendMessageMiddleware => SendMessageMiddleware;
}
