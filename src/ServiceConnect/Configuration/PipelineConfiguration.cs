using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Configuration;

/// <summary>
/// Mutable implementation of <see cref="IPipelineConfiguration"/> that stores filter and middleware type registrations.
/// </summary>
public sealed class PipelineConfiguration : IPipelineConfiguration
{
    private readonly List<Type> _beforeConsumingFilters = [];
    private readonly List<Type> _afterConsumingFilters = [];
    private readonly List<Type> _outgoingFilters = [];
    private readonly List<Type> _messageProcessingMiddleware = [];
    private readonly List<Type> _sendMessageMiddleware = [];

    /// <summary>
    /// Gets the filters that run before handler invocation.
    /// </summary>
    public IList<Type> BeforeConsumingFilters => _beforeConsumingFilters;
    /// <summary>
    /// Gets the filters that run after handler invocation.
    /// </summary>
    public IList<Type> AfterConsumingFilters => _afterConsumingFilters;
    /// <summary>
    /// Gets the filters that run for outgoing messages.
    /// </summary>
    public IList<Type> OutgoingFilters => _outgoingFilters;
    /// <summary>
    /// Gets the middleware types that wrap inbound message processing.
    /// </summary>
    public IList<Type> MessageProcessingMiddleware => _messageProcessingMiddleware;
    /// <summary>
    /// Gets the middleware types that wrap outbound send and publish operations.
    /// </summary>
    public IList<Type> SendMessageMiddleware => _sendMessageMiddleware;

    IReadOnlyList<Type> IPipelineConfiguration.BeforeConsumingFilters => _beforeConsumingFilters;
    IReadOnlyList<Type> IPipelineConfiguration.AfterConsumingFilters => _afterConsumingFilters;
    IReadOnlyList<Type> IPipelineConfiguration.OutgoingFilters => _outgoingFilters;
    IReadOnlyList<Type> IPipelineConfiguration.MessageProcessingMiddleware => _messageProcessingMiddleware;
    IReadOnlyList<Type> IPipelineConfiguration.SendMessageMiddleware => _sendMessageMiddleware;
}
