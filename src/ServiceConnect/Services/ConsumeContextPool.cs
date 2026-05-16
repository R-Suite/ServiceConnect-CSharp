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
        // Cached backing fields — same memory-model contract as ConsumeContext: each payload
        // field is plain; the volatile bool flag publishes it. Writers MUST write the payload
        // before the flag (release barrier); readers MUST check the flag before reading the
        // payload. Volatile flag-clear in Release/Initialize happens BEFORE the payload clear
        // so a reader who sees the flag false never observes a stale payload from the
        // previous rental.
        private volatile bool _messageIdCached;
        private Guid _correlationIdValue;
        private volatile bool _correlationIdCached;

        // Rent-token guard: every Initialize bumps the instance token; Release bumps it
        // again. The RentalHandle struct captures the token at rent time. If a caller holds
        // onto the RentalHandle after Release, its snapshot no longer matches the instance's
        // _rentToken and EnsureActive(snapshot) throws rather than returning another
        // handler's data.
        private long _rentToken;
        // Idempotency guard for Release: 0 = active (held by caller), 1 = pooled.
        // Release CAS-flips 1 only on the active->pooled transition; a defensive
        // double-Release CAS-flips 0->1 the first call (pushes to pool), then sees
        // the field is already 1 and no-ops the second call. Without this guard a
        // double-Release would push the same instance into _pool twice and two
        // concurrent Rent calls would hand the same underlying instance to two
        // handlers — a use-after-rent corruption.
        private int _pooled;

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
                _messageIdCached = true;  // volatile write — release barrier publishes _messageId
            }
            return _messageId;
        }

        internal Guid GetOrCacheCorrelationId()
        {
            if (!_correlationIdCached)
            {
                _correlationIdValue = _headers.TryGetValue(HeaderKeys.CorrelationId, out var value)
                        && Guid.TryParse(HeaderDecoder.Decode(value), out var id)
                    ? id : Guid.Empty;
                _correlationIdCached = true;  // volatile write — release barrier publishes _correlationIdValue
            }
            return _correlationIdValue;
        }

        internal long Initialize(
            IBus bus,
            IDictionary<string, object> headers,
            IQueueConfiguration queueConfig,
            IBusConfiguration busConfig,
            IReplyStatusRequestReplyManager? replyStatusRequestReplyManager,
            CancellationToken cancellationToken)
        {
            // Write all instance fields BEFORE bumping the rent token. The Interlocked.Increment
            // below acts as the publish-fence: a third party who captures a stale handle and
            // does EnsureActive(prevToken) → field-read could otherwise observe the new token
            // (via Volatile.Read) but a still-stale BusUnsafe / CancellationTokenUnsafe under
            // weak memory ordering (ARM64). Writing fields first ensures the field-state
            // publish happens-before the token publish, so any reader who sees the new token
            // is guaranteed to see the fresh fields.
            BusUnsafe = bus;
            _queueConfig = queueConfig;
            _busConfig = busConfig;
            _replyStatusRequestReplyManager = replyStatusRequestReplyManager;
            CancellationTokenUnsafe = cancellationToken;
            _headers = headers as Dictionary<string, object> ?? new Dictionary<string, object>(headers, StringComparer.Ordinal);
            // Flag-clear before payload-clear: a reader who sees the flag false never
            // observes a stale payload from the previous rental.
            _messageIdCached = false;
            _messageId = null;
            _correlationIdCached = false;
            _correlationIdValue = default;
            // Re-arm the idempotency guard so a future Release can transition active->pooled
            // exactly once. Sequenced before the token bump so an EnsureActive reader who
            // sees the new token never observes _pooled=1 (which would indicate the context
            // is already back in the pool).
            Volatile.Write(ref _pooled, 0);
            // Token bump publishes all preceding writes via the Interlocked full fence; the
            // matching acquire is Volatile.Read in EnsureActive.
            return Interlocked.Increment(ref _rentToken);
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
            // handing the context back to the pool. The Interlocked.Increment is the
            // publish-fence: any reader that observes the new _rentToken via Volatile.Read
            // happens-after our field clears below — so a stale RentalHandle calling
            // EnsureActive(oldToken) sees the mismatch and throws; a never-rented field
            // read via the IConsumeContext explicit-interface path already throws
            // NotSupportedException.
            Interlocked.Increment(ref _rentToken);

            // Null mutable references so the previous message's headers / bus / inflight CT
            // are GC-reclaimable while the context sits in the pool. Without this clear, a
            // pooled instance keeps the broker-supplied headers dict and bus reference alive
            // until next Rent, which on a quiet bus after a burst pins ≤ MaxPoolSize×N
            // unnecessarily. Safe because the rent-token bump above invalidated every
            // outstanding RentalHandle.
            _headers = EmptyHeaders;
            BusUnsafe = null!;
            CancellationTokenUnsafe = default;
            _replyStatusRequestReplyManager = null;
            // Flag-clear before payload-clear: a reader who sees the flag false never
            // observes a stale payload from the previous rental.
            _messageIdCached = false;
            _messageId = null;
            _correlationIdCached = false;
            _correlationIdValue = default;

            // Idempotency guard: only the first Release call after Initialize transitions
            // _pooled from 0 to 1; a defensive double-Release CAS-fails the second call
            // and skips the Return. Without this guard the same instance would be added
            // to _pool twice and two concurrent Rent calls would hand it out to two
            // handlers (use-after-rent corruption).
            if (Interlocked.Exchange(ref _pooled, 1) == 0)
            {
                _owner.Return(this);
            }
        }
    }
}
