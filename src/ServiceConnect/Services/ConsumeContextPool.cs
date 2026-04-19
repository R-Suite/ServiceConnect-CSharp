using System.Collections.Concurrent;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Services;

internal sealed class ConsumeContextPool
{
    // Bleed excess contexts to GC rather than growing the pool unboundedly under bursts.
    private const int MaxPoolSize = 512;

    private static readonly Dictionary<string, object> EmptyHeaders = [];
    private readonly ConcurrentBag<PooledConsumeContext> _pool = new();

    public PooledConsumeContext Rent(
        IBus bus,
        IDictionary<string, object> headers,
        IQueueConfiguration queueConfig,
        IBusConfiguration busConfig,
        IReplyStatusRequestReplyManager? replyStatusRequestReplyManager,
        CancellationToken cancellationToken)
    {
        if (!_pool.TryTake(out var context))
            context = new PooledConsumeContext(this);

        context.Initialize(bus, headers, queueConfig, busConfig, replyStatusRequestReplyManager, cancellationToken);
        return context;
    }

    private void Return(PooledConsumeContext context)
    {
        // Cheap upper bound: ConcurrentBag has no Count that is both fast and exact, but
        // the Count property is O(n) over segments. For our pool sizes this is acceptable
        // and a rare cost compared to burst arrivals.
        if (_pool.Count >= MaxPoolSize) return;
        _pool.Add(context);
    }

    internal sealed class PooledConsumeContext(ConsumeContextPool owner) : IConsumeContext
    {
        private readonly ConsumeContextPool _owner = owner;
        private Dictionary<string, object> _headers = EmptyHeaders;
        private IBus _bus = null!;
        private IQueueConfiguration _queueConfig = null!;
        private IBusConfiguration _busConfig = null!;
        private IReplyStatusRequestReplyManager? _replyStatusRequestReplyManager;
        private string? _messageId;
        private bool _messageIdCached;
        private Guid? _correlationId;

        // Rent-token guard: every Initialize bumps the instance token; the renter captures
        // it into _activeToken. If a caller holds onto the IConsumeContext after Release,
        // the next Rent bumps _rentToken and _activeToken no longer matches — subsequent
        // property reads throw rather than returning another handler's data.
        private long _rentToken;
        private long _activeToken;

        public IBus Bus { get { EnsureActive(); return _bus; } }
        public IReadOnlyDictionary<string, object> Headers { get { EnsureActive(); return _headers; } }
        public CancellationToken CancellationToken { get { EnsureActive(); return _cancellationToken; } }
        private CancellationToken _cancellationToken;

        public string? MessageId
        {
            get
            {
                EnsureActive();
                if (!_messageIdCached)
                {
                    _messageId = _headers.TryGetValue(HeaderKeys.MessageId, out var value)
                        ? HeaderDecoder.Decode(value) : null;
                    _messageIdCached = true;
                }

                return _messageId;
            }
        }

        public Guid CorrelationId
        {
            get
            {
                EnsureActive();
                if (_correlationId is null)
                {
                    _correlationId = _headers.TryGetValue(HeaderKeys.CorrelationId, out var value)
                        && Guid.TryParse(HeaderDecoder.Decode(value), out var id)
                        ? id : Guid.Empty;
                }

                return _correlationId.Value;
            }
        }

        internal void Initialize(
            IBus bus,
            IDictionary<string, object> headers,
            IQueueConfiguration queueConfig,
            IBusConfiguration busConfig,
            IReplyStatusRequestReplyManager? replyStatusRequestReplyManager,
            CancellationToken cancellationToken)
        {
            var token = Interlocked.Increment(ref _rentToken);
            _activeToken = token;
            _bus = bus;
            _queueConfig = queueConfig;
            _busConfig = busConfig;
            _replyStatusRequestReplyManager = replyStatusRequestReplyManager;
            _cancellationToken = cancellationToken;
            _headers = headers as Dictionary<string, object> ?? new Dictionary<string, object>(headers);
            _messageId = null;
            _messageIdCached = false;
            _correlationId = null;
        }

        private void EnsureActive()
        {
            if (Volatile.Read(ref _rentToken) != _activeToken)
                throw new InvalidOperationException(
                    "IConsumeContext is no longer valid — it was released when the handler returned. "
                    + "Do not capture it beyond the handler lifetime.");
        }

        public async Task ReplyAsync<TReply>(TReply message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
            where TReply : Message
        {
            EnsureActive();
            var sourceAddress = ConsumeContext.GetDecodedHeader(_headers, HeaderKeys.SourceAddress);
            if (string.IsNullOrEmpty(sourceAddress))
                throw new InvalidOperationException("Cannot reply: incoming message has no SourceAddress header.");

            var requestMessageId = ConsumeContext.GetDecodedHeader(_headers, HeaderKeys.RequestMessageId);
            var isTrustedRequestReply = ConsumeContext.IsTrustedRequestReplyEnvelope(
                _headers,
                _queueConfig,
                _replyStatusRequestReplyManager,
                requestMessageId,
                sourceAddress);

            if (_busConfig.ValidateReplyDestinations && !isTrustedRequestReply && !ConsumeContext.IsKnownQueue(sourceAddress, _queueConfig))
            {
                throw new InvalidOperationException(
                    $"Cannot reply: SourceAddress '{sourceAddress}' is not a recognized queue. " +
                    "This may indicate a spoofed message. Configure queue mappings or use RequestReplyManager for safe replies.");
            }

            var replyHeaders = headers ?? [];
            if (!string.IsNullOrEmpty(requestMessageId))
                replyHeaders[HeaderKeys.ResponseMessageId] = requestMessageId;

            var options = new SendOptions { EndPoint = sourceAddress, Headers = replyHeaders };
            await _bus.SendAsync(message, options, cancellationToken).ConfigureAwait(false);
        }

        public void Release()
        {
            // Invalidate the outstanding _activeToken view held by consumers before
            // handing the context back to the pool.
            Interlocked.Increment(ref _rentToken);
            _owner.Return(this);
        }
    }
}
