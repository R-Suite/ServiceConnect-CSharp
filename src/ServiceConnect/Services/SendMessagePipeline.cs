using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Services;

/// <summary>
/// Default implementation of ISendMessagePipeline that delegates directly to IProducer,
/// optionally wrapping calls in a middleware chain from IPipelineConfiguration.
/// </summary>
public sealed class SendMessagePipeline(
    IProducer producer,
    IPipelineConfiguration pipelineConfig,
    IServiceProvider serviceProvider) : ISendMessagePipeline
{
    private readonly IProducer _producer = producer ?? throw new ArgumentNullException(nameof(producer));
    private readonly IPipelineConfiguration _pipelineConfig = pipelineConfig ?? throw new ArgumentNullException(nameof(pipelineConfig));
    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    private volatile bool _disposed;

    public Task ExecutePublishMessagePipelineAsync(Type typeObject, byte[] messageBytes, Dictionary<string, string>? headers = null, string? endPoint = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        static Task Terminal(Type t, byte[] b, Dictionary<string, string> h, string? ep, CancellationToken ct, IProducer prod) =>
            prod.PublishAsync(t, b, h);

        var chain = BuildChain((t, b, h, ep, ct) => Terminal(t, b, h, ep, ct, _producer));
        return chain(typeObject, messageBytes, headers ?? [], endPoint, cancellationToken);
    }

    public Task ExecuteSendMessagePipelineAsync(Type typeObject, byte[] messageBytes, Dictionary<string, string>? headers = null, string? endPoint = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        static Task Terminal(Type t, byte[] b, Dictionary<string, string> h, string? ep, CancellationToken ct, IProducer prod)
        {
            if (!string.IsNullOrEmpty(ep))
                return prod.SendAsync(ep, t, b, h);
            return prod.SendAsync(t, b, h);
        }

        var chain = BuildChain((t, b, h, ep, ct) => Terminal(t, b, h, ep, ct, _producer));
        return chain(typeObject, messageBytes, headers ?? [], endPoint, cancellationToken);
    }

    private SendMessageDelegate BuildChain(SendMessageDelegate terminal)
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Producer lifetime is managed by the DI container — do not dispose it here
    }
}
