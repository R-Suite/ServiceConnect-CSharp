using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Services;

internal sealed class ConsumeContextPool
{
    private static readonly IReadOnlyDictionary<string, object> EmptyHeaders =
        new ReadOnlyDictionary<string, object>(new Dictionary<string, object>());
    private readonly ConcurrentBag<PooledConsumeContext> _pool = new();

    public PooledConsumeContext Rent(
        IBus bus,
        IDictionary<string, object> headers,
        IQueueConfiguration queueConfig,
        IBusConfiguration busConfig,
        CancellationToken cancellationToken)
    {
        if (!_pool.TryTake(out var context))
            context = new PooledConsumeContext(this);

        context.Initialize(bus, headers, queueConfig, busConfig, cancellationToken);
        return context;
    }

    private void Return(PooledConsumeContext context)
    {
        _pool.Add(context);
    }

    internal sealed class PooledConsumeContext(ConsumeContextPool owner) : IConsumeContext
    {
        private readonly ConsumeContextPool _owner = owner;
        private IReadOnlyDictionary<string, object> _headers = EmptyHeaders;
        private IBus _bus = null!;
        private IQueueConfiguration _queueConfig = null!;
        private IBusConfiguration _busConfig = null!;
        private string? _messageId;
        private bool _messageIdCached;
        private Guid? _correlationId;

        public IBus Bus => _bus;
        public IReadOnlyDictionary<string, object> Headers => _headers;
        public CancellationToken CancellationToken { get; private set; }

        public string? MessageId
        {
            get
            {
                if (!_messageIdCached)
                {
                    _messageId = Headers.TryGetValue(HeaderKeys.MessageId, out var value)
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
                if (_correlationId is null)
                {
                    _correlationId = Headers.TryGetValue(HeaderKeys.CorrelationId, out var value)
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
            CancellationToken cancellationToken)
        {
            _bus = bus;
            _queueConfig = queueConfig;
            _busConfig = busConfig;
            CancellationToken = cancellationToken;
            _headers = new ReadOnlyDictionary<string, object>(
                headers as Dictionary<string, object> ?? new Dictionary<string, object>(headers));
            _messageId = null;
            _messageIdCached = false;
            _correlationId = null;
        }

        public async Task ReplyAsync<TReply>(TReply message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
            where TReply : Message
        {
            var sourceAddress = Headers.TryGetValue(HeaderKeys.SourceAddress, out var sa) ? HeaderDecoder.Decode(sa) : null;
            if (string.IsNullOrEmpty(sourceAddress))
                throw new InvalidOperationException("Cannot reply: incoming message has no SourceAddress header.");

            var requestMessageId = Headers.TryGetValue(HeaderKeys.RequestMessageId, out var rmi) ? HeaderDecoder.Decode(rmi) : null;
            var isRequestReply = !string.IsNullOrEmpty(requestMessageId);

            if (_busConfig.ValidateReplyDestinations && !isRequestReply && !ConsumeContext.IsKnownQueue(sourceAddress, _queueConfig))
            {
                throw new InvalidOperationException(
                    $"Cannot reply: SourceAddress '{sourceAddress}' is not a recognized queue. " +
                    "This may indicate a spoofed message. Configure queue mappings or use RequestReplyManager for safe replies.");
            }

            var replyHeaders = headers ?? [];
            if (!string.IsNullOrEmpty(requestMessageId))
                replyHeaders[HeaderKeys.ResponseMessageId] = requestMessageId;

            var options = new SendOptions { EndPoint = sourceAddress, Headers = replyHeaders };
            await Bus.SendAsync(message, options, cancellationToken).ConfigureAwait(false);
        }

        public void Release()
        {
            _owner.Return(this);
        }
    }
}
