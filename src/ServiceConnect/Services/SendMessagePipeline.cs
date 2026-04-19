using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Services;

/// <summary>
/// Default implementation of ISendMessagePipeline that delegates directly to IProducer,
/// optionally wrapping calls in a middleware chain from IPipelineConfiguration.
/// Chains are built once (lazily) and cached rather than rebuilt per message.
/// </summary>
/// <remarks>
/// Because the chain caches middleware instances captured at first use,
/// <see cref="ISendMessageMiddleware"/> implementations MUST be registered as
/// singletons. Scoped or transient registrations will be silently promoted to
/// singleton lifetime, which can cause cross-request state leaks.
/// </remarks>
public sealed class SendMessagePipeline : ISendMessagePipeline
{
    private readonly IProducer _producer;
    private readonly IPipelineConfiguration _pipelineConfig;
    private readonly IServiceProvider _serviceProvider;
    private readonly Lazy<SendMessageDelegate> _publishChain;
    private readonly Lazy<SendMessageDelegate> _sendChain;
    private volatile bool _disposed;

    public SendMessagePipeline(IProducer producer, IPipelineConfiguration pipelineConfig, IServiceProvider serviceProvider)
    {
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
        _pipelineConfig = pipelineConfig ?? throw new ArgumentNullException(nameof(pipelineConfig));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _publishChain = new Lazy<SendMessageDelegate>(BuildPublishChain, isThreadSafe: true);
        _sendChain = new Lazy<SendMessageDelegate>(BuildSendChain, isThreadSafe: true);
    }

    public Task ExecutePublishMessagePipelineAsync(Type typeObject, byte[] messageBytes, Dictionary<string, string>? headers = null, string? endPoint = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _publishChain.Value(typeObject, messageBytes, headers ?? [], endPoint, cancellationToken);
    }

    public Task ExecuteSendMessagePipelineAsync(Type typeObject, byte[] messageBytes, Dictionary<string, string>? headers = null, string? endPoint = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _sendChain.Value(typeObject, messageBytes, headers ?? [], endPoint, cancellationToken);
    }

    private SendMessageDelegate BuildPublishChain()
    {
        var producer = _producer;
        SendMessageDelegate terminal = (t, b, h, ep, ct) => producer.PublishAsync(t, b, h, ct);
        return WrapMiddleware(terminal);
    }

    private SendMessageDelegate BuildSendChain()
    {
        var producer = _producer;
        SendMessageDelegate terminal = (t, b, h, ep, ct) =>
            !string.IsNullOrEmpty(ep)
                ? producer.SendAsync(ep, t, b, h, ct)
                : producer.SendAsync(t, b, h, ct);
        return WrapMiddleware(terminal);
    }

    private SendMessageDelegate WrapMiddleware(SendMessageDelegate terminal)
    {
        var middlewareTypes = _pipelineConfig.SendMessageMiddleware;
        if (middlewareTypes.Count == 0)
            return terminal;

        var chain = terminal;
        for (int i = middlewareTypes.Count - 1; i >= 0; i--)
        {
            var mw = (ISendMessageMiddleware)_serviceProvider.GetRequiredService(middlewareTypes[i]);
            var next = chain;
            chain = (t, b, h, ep, ct) => mw.Process(t, b, h, ep, next, ct);
        }
        return chain;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        // Producer lifetime is managed by the DI container — do not dispose it here
        return ValueTask.CompletedTask;
    }
}
