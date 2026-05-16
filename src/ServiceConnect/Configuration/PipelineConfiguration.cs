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
/// <see cref="IList{T}"/> view is backed by a <see cref="GuardedList{T}"/> that throws on
/// mutation once the configuration is frozen.
/// </remarks>
internal sealed class PipelineConfiguration : IPipelineConfiguration
{
    private bool _frozen;

    private readonly GuardedList<Type> _beforeConsumingFilters;
    private readonly GuardedList<Type> _afterConsumingFilters;
    private readonly GuardedList<Type> _onConsumedSuccessfullyFilters;
    private readonly GuardedList<Type> _outgoingFilters;
    private readonly GuardedList<Type> _messageProcessingMiddleware;
    private readonly GuardedList<Type> _sendMessageMiddleware;

    private readonly ReadOnlyCollection<Type> _beforeConsumingFiltersView;
    private readonly ReadOnlyCollection<Type> _afterConsumingFiltersView;
    private readonly ReadOnlyCollection<Type> _onConsumedSuccessfullyFiltersView;
    private readonly ReadOnlyCollection<Type> _outgoingFiltersView;
    private readonly ReadOnlyCollection<Type> _messageProcessingMiddlewareView;
    private readonly ReadOnlyCollection<Type> _sendMessageMiddlewareView;

    public PipelineConfiguration()
    {
        // GuardedList wraps a List<T> and delegates freeze-checking to () => _frozen.
        // Cache the read-only wrappers so the IPipelineConfiguration getters don't
        // allocate a fresh ReadOnlyCollection on every dispatch. Each wrapper is a live
        // view over its underlying List<T>; the builder's in-callback Add reflects
        // through. Post-builder mutation through the IReadOnlyList view is blocked
        // because the runtime type is ReadOnlyCollection<T>, not List<T>.
        _beforeConsumingFilters = new GuardedList<Type>(this, nameof(BeforeConsumingFilters));
        _afterConsumingFilters = new GuardedList<Type>(this, nameof(AfterConsumingFilters));
        _onConsumedSuccessfullyFilters = new GuardedList<Type>(this, nameof(OnConsumedSuccessfullyFilters));
        _outgoingFilters = new GuardedList<Type>(this, nameof(OutgoingFilters));
        _messageProcessingMiddleware = new GuardedList<Type>(this, nameof(MessageProcessingMiddleware));
        _sendMessageMiddleware = new GuardedList<Type>(this, nameof(SendMessageMiddleware));

        _beforeConsumingFiltersView = _beforeConsumingFilters.Inner.AsReadOnly();
        _afterConsumingFiltersView = _afterConsumingFilters.Inner.AsReadOnly();
        _onConsumedSuccessfullyFiltersView = _onConsumedSuccessfullyFilters.Inner.AsReadOnly();
        _outgoingFiltersView = _outgoingFilters.Inner.AsReadOnly();
        _messageProcessingMiddlewareView = _messageProcessingMiddleware.Inner.AsReadOnly();
        _sendMessageMiddlewareView = _sendMessageMiddleware.Inner.AsReadOnly();
    }

    /// <summary>
    /// Latches this configuration so further list-mutation calls throw <see cref="InvalidOperationException"/>.
    /// Called by <see cref="BusConfiguration.Freeze"/> after the user's configure callback returns.
    /// </summary>
    internal void Freeze() => _frozen = true;

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

    // Wraps a List<T> so all mutating IList<T> operations check the owning
    // PipelineConfiguration's _frozen flag before proceeding. Read-only operations
    // (indexer getter, Count, Contains, CopyTo, GetEnumerator) pass through without
    // the freeze check because they're safe at any time.
    private sealed class GuardedList<T>(PipelineConfiguration owner, string listName) : IList<T>
    {
        internal readonly List<T> Inner = [];
        private readonly PipelineConfiguration _owner = owner;
        private readonly string _listName = listName;

        private void ThrowIfFrozen()
        {
            if (_owner._frozen)
            {
                throw new InvalidOperationException(
                    $"PipelineConfiguration.{_listName} is frozen — the list cannot be modified after AddServiceConnect has returned. " +
                    "Configure all pipeline filters and middleware inside the AddServiceConnect callback.");
            }
        }

        public T this[int index]
        {
            get => Inner[index];
            set { ThrowIfFrozen(); Inner[index] = value; }
        }

        public int Count => Inner.Count;
        public bool IsReadOnly => false;
        public void Add(T item) { ThrowIfFrozen(); Inner.Add(item); }
        public void Clear() { ThrowIfFrozen(); Inner.Clear(); }
        public bool Contains(T item) => Inner.Contains(item);
        public void CopyTo(T[] array, int arrayIndex) => Inner.CopyTo(array, arrayIndex);
        public IEnumerator<T> GetEnumerator() => Inner.GetEnumerator();
        public int IndexOf(T item) => Inner.IndexOf(item);
        public void Insert(int index, T item) { ThrowIfFrozen(); Inner.Insert(index, item); }
        public bool Remove(T item) { ThrowIfFrozen(); return Inner.Remove(item); }
        public void RemoveAt(int index) { ThrowIfFrozen(); Inner.RemoveAt(index); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => Inner.GetEnumerator();
    }
}
