using System.Collections.Concurrent;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Services;

internal sealed class ConsumeContextPool
{
    // Bleed excess contexts to GC rather than growing the pool unboundedly under bursts.
    private const int MaxPoolSize = 512;

    private static readonly Dictionary<string, object> EmptyHeaders = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<PooledConsumeContext> _pool = [];

    public RentalHandle Rent(
        IBus bus,
        IDictionary<string, object> headers,
        IQueueConfiguration queueConfig,
        IBusConfiguration busConfig,
        IReplyStatusRequestReplyManager? replyStatusRequestReplyManager,
        CancellationToken cancellationToken)
    {
        if (!_pool.TryTake(out var context))
        {
            context = new PooledConsumeContext(this);
        }

        var token = context.Initialize(bus, headers, queueConfig, busConfig, replyStatusRequestReplyManager, cancellationToken);
        return new RentalHandle(context, token);
    }

    private void Return(PooledConsumeContext context)
    {
        // Cheap upper bound: ConcurrentBag has no Count that is both fast and exact, but
        // the Count property is O(n) over segments. For our pool sizes this is acceptable
        // and a rare cost compared to burst arrivals.
        if (_pool.Count >= MaxPoolSize)
        {
            return;
        }

        _pool.Add(context);
    }

    /// <summary>
    /// Caller-side handle returned by <see cref="ConsumeContextPool.Rent"/>. Captures the
    /// rent-token at rent time so that the guard compares the snapshot against the pooled
    /// instance's current generation — a stale reference retains the old snapshot and throws
    /// once the instance is released and re-rented.
    /// </summary>
    internal readonly struct RentalHandle(PooledConsumeContext inner, long token) : IConsumeContext
    {
        private readonly PooledConsumeContext _inner = inner;
        private readonly long _token = token;

        public IBus Bus { get { _inner.EnsureActive(_token); return _inner.BusUnsafe; } }
        public IReadOnlyDictionary<string, object> Headers { get { _inner.EnsureActive(_token); return _inner.HeadersUnsafe; } }
        public CancellationToken CancellationToken { get { _inner.EnsureActive(_token); return _inner.CancellationTokenUnsafe; } }

        public string? MessageId
        {
            get
            {
                _inner.EnsureActive(_token);
                return _inner.GetOrCacheMessageId();
            }
        }

        public Guid CorrelationId
        {
            get
            {
                _inner.EnsureActive(_token);
                return _inner.GetOrCacheCorrelationId();
            }
        }

        public Task ReplyAsync<TReply>(TReply message, ReplyOptions? options = null, CancellationToken cancellationToken = default)
            where TReply : Message
        {
            _inner.EnsureActive(_token);
            return _inner.ReplyAsyncCore(message, options, cancellationToken);
        }

        public void Release()
        {
            _inner.Release();
        }
    }

    internal sealed class PooledConsumeContext(ConsumeContextPool owner) : IConsumeContext
    {
        private readonly ConsumeContextPool _owner = owner;
        private Dictionary<string, object> _headers = EmptyHeaders;
        private IQueueConfiguration _queueConfig = null!;
        private IBusConfiguration _busConfig = null!;
        private IReplyStatusRequestReplyManager? _replyStatusRequestReplyManager;
        private string? _messageId;
        private bool _messageIdCached;
        private Guid? _correlationId;

        // Rent-token guard: every Initialize bumps the instance token; Release bumps it
        // again. The RentalHandle struct captures the token at rent time. If a caller holds
        // onto the RentalHandle after Release, its snapshot no longer matches the instance's
        // _rentToken and EnsureActive(snapshot) throws rather than returning another
        // handler's data.
        private long _rentToken;

        // Unsafe accessors — callers MUST call EnsureActive(_token) before using these.
        internal IBus BusUnsafe { get; private set; } = null!;
        internal IReadOnlyDictionary<string, object> HeadersUnsafe => _headers;
        internal CancellationToken CancellationTokenUnsafe { get; private set; }

        // IConsumeContext explicit implementation routes through RentalHandle; direct use
        // of the pooled instance (without a captured token) is intentionally unsupported.
        IBus IConsumeContext.Bus => throw new NotSupportedException("Use RentalHandle.");
        IReadOnlyDictionary<string, object> IConsumeContext.Headers => throw new NotSupportedException("Use RentalHandle.");
        CancellationToken IConsumeContext.CancellationToken => throw new NotSupportedException("Use RentalHandle.");
        string? IConsumeContext.MessageId => throw new NotSupportedException("Use RentalHandle.");
        Guid IConsumeContext.CorrelationId => throw new NotSupportedException("Use RentalHandle.");
        Task IConsumeContext.ReplyAsync<TReply>(TReply message, ReplyOptions? options, CancellationToken cancellationToken)
            => throw new NotSupportedException("Use RentalHandle.");

        internal string? GetOrCacheMessageId()
        {
            if (!_messageIdCached)
            {
                _messageId = _headers.TryGetValue(HeaderKeys.MessageId, out var value)
                    ? HeaderDecoder.Decode(value) : null;
                _messageIdCached = true;
            }
            return _messageId;
        }

        internal Guid GetOrCacheCorrelationId()
        {
            _correlationId ??= _headers.TryGetValue(HeaderKeys.CorrelationId, out var value)
                    && Guid.TryParse(HeaderDecoder.Decode(value), out var id)
                    ? id : Guid.Empty;
            return _correlationId.Value;
        }

        internal long Initialize(
            IBus bus,
            IDictionary<string, object> headers,
            IQueueConfiguration queueConfig,
            IBusConfiguration busConfig,
            IReplyStatusRequestReplyManager? replyStatusRequestReplyManager,
            CancellationToken cancellationToken)
        {
            var token = Interlocked.Increment(ref _rentToken);
            BusUnsafe = bus;
            _queueConfig = queueConfig;
            _busConfig = busConfig;
            _replyStatusRequestReplyManager = replyStatusRequestReplyManager;
            CancellationTokenUnsafe = cancellationToken;
            _headers = headers as Dictionary<string, object> ?? new Dictionary<string, object>(headers, StringComparer.Ordinal);
            _messageId = null;
            _messageIdCached = false;
            _correlationId = null;
            return token;
        }

        internal void EnsureActive(long expectedToken)
        {
            if (Volatile.Read(ref _rentToken) != expectedToken)
            {
                throw new InvalidOperationException(
                    "IConsumeContext is no longer valid — it was released when the handler returned. "
                    + "Do not capture it beyond the handler lifetime.");
            }
        }

        internal Task ReplyAsyncCore<TReply>(TReply message, ReplyOptions? options, CancellationToken cancellationToken)
            where TReply : Message
        {
            var sourceAddress = ConsumeContext.GetDecodedHeader(_headers, HeaderKeys.SourceAddress);
            if (string.IsNullOrEmpty(sourceAddress))
            {
                throw new InvalidOperationException("Cannot reply: incoming message has no SourceAddress header.");
            }

            var requestMessageId = ConsumeContext.GetDecodedHeader(_headers, HeaderKeys.RequestMessageId);
            var isTrustedRequestReply = ConsumeContext.IsTrustedRequestReplyEnvelope(
                _headers,
                _queueConfig,
                _replyStatusRequestReplyManager,
                _busConfig,
                requestMessageId,
                sourceAddress);

            if (_busConfig.ValidateReplyDestinations && !isTrustedRequestReply && !ConsumeContext.IsKnownQueue(sourceAddress, _queueConfig))
            {
                throw new InvalidOperationException(
                    $"Cannot reply: SourceAddress '{sourceAddress}' is not a recognized queue. " +
                    "This may indicate a spoofed message. Configure queue mappings or use RequestReplyManager for safe replies.");
            }

            var callerHeaders = options?.Headers;
            Dictionary<string, string> replyHeaders = callerHeaders is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(callerHeaders, StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(requestMessageId))
            {
                replyHeaders[HeaderKeys.ResponseMessageId] = requestMessageId;
            }

            var sendOptions = new SendOptions { EndPoint = sourceAddress, Headers = replyHeaders };
            return BusUnsafe.SendAsync(message, sendOptions, cancellationToken);
        }

        public void Release()
        {
            // Invalidate the outstanding RentalHandle view held by consumers before
            // handing the context back to the pool.
            Interlocked.Increment(ref _rentToken);
            _owner.Return(this);
        }
    }
}
