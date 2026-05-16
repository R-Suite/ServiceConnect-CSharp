using System.Collections.ObjectModel;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Configuration;

/// <summary>
/// Mutable implementation of <see cref="IPipelineConfiguration"/> that stores filter and middleware type registrations.
/// </summary>
/// <remarks>
/// The internal <see cref="IList{T}"/> accessors expose mutable lists to the builder for
/// in-callback configuration. The external <see cref="IPipelineConfiguration"/> interface
/// projects each list through a cached <see cref="ReadOnlyCollection{T}"/> — a runtime type
/// distinct from <see cref="List{T}"/> — so callers that resolve <see cref="IPipelineConfiguration"/>
/// from DI cannot cast the returned <see cref="IReadOnlyList{T}"/> back to <see cref="List{T}"/>
/// and bypass the builder by appending filters / middleware post-startup. The internal
/// <see cref="IList{T}"/> view stays mutable because the concrete <see cref="PipelineConfiguration"/>
/// class is internal and unreachable through DI; only same-assembly code (the builder) can
/// reach it during the configure callback.
/// </remarks>
internal sealed class PipelineConfiguration : IPipelineConfiguration
{
    private readonly List<Type> _beforeConsumingFilters = [];
    private readonly List<Type> _afterConsumingFilters = [];
    private readonly List<Type> _onConsumedSuccessfullyFilters = [];
    private readonly List<Type> _outgoingFilters = [];
    private readonly List<Type> _messageProcessingMiddleware = [];
    private readonly List<Type> _sendMessageMiddleware = [];

    private readonly ReadOnlyCollection<Type> _beforeConsumingFiltersView;
    private readonly ReadOnlyCollection<Type> _afterConsumingFiltersView;
    private readonly ReadOnlyCollection<Type> _onConsumedSuccessfullyFiltersView;
    private readonly ReadOnlyCollection<Type> _outgoingFiltersView;
    private readonly ReadOnlyCollection<Type> _messageProcessingMiddlewareView;
    private readonly ReadOnlyCollection<Type> _sendMessageMiddlewareView;

    public PipelineConfiguration()
    {
        // Cache the read-only wrappers so the IPipelineConfiguration getters don't
        // allocate a fresh ReadOnlyCollection on every dispatch. Each wrapper is a live
        // view over its underlying List<T>; the builder's in-callback Add reflects
        // through. Post-builder mutation through the IReadOnlyList view is blocked
        // because the runtime type is ReadOnlyCollection<T>, not List<T>.
        _beforeConsumingFiltersView = _beforeConsumingFilters.AsReadOnly();
        _afterConsumingFiltersView = _afterConsumingFilters.AsReadOnly();
        _onConsumedSuccessfullyFiltersView = _onConsumedSuccessfullyFilters.AsReadOnly();
        _outgoingFiltersView = _outgoingFilters.AsReadOnly();
        _messageProcessingMiddlewareView = _messageProcessingMiddleware.AsReadOnly();
        _sendMessageMiddlewareView = _sendMessageMiddleware.AsReadOnly();
    }

    /// <summary>
    /// Gets the filters that run before handler invocation.
    /// </summary>
    public IList<Type> BeforeConsumingFilters => _beforeConsumingFilters;
    /// <summary>
    /// Gets the filters that run after handler invocation.
    /// </summary>
    public IList<Type> AfterConsumingFilters => _afterConsumingFilters;
    /// <summary>
    /// Gets the filters that run only after a successful handler invocation.
    /// </summary>
    public IList<Type> OnConsumedSuccessfullyFilters => _onConsumedSuccessfullyFilters;
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

    IReadOnlyList<Type> IPipelineConfiguration.BeforeConsumingFilters => _beforeConsumingFiltersView;
    IReadOnlyList<Type> IPipelineConfiguration.AfterConsumingFilters => _afterConsumingFiltersView;
    IReadOnlyList<Type> IPipelineConfiguration.OnConsumedSuccessfullyFilters => _onConsumedSuccessfullyFiltersView;
    IReadOnlyList<Type> IPipelineConfiguration.OutgoingFilters => _outgoingFiltersView;
    IReadOnlyList<Type> IPipelineConfiguration.MessageProcessingMiddleware => _messageProcessingMiddlewareView;
    IReadOnlyList<Type> IPipelineConfiguration.SendMessageMiddleware => _sendMessageMiddlewareView;
}
