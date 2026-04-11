using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Services;

/// <summary>
/// Default implementation of ISendMessagePipeline that delegates directly to IProducer,
/// optionally wrapping calls in a middleware chain from IPipelineConfiguration.
/// </summary>
public class SendMessagePipeline : ISendMessagePipeline
{
    private readonly IProducer _producer;
    private readonly IPipelineConfiguration _pipelineConfig;
    private readonly IServiceProvider _serviceProvider;
    private bool _disposed;

    public SendMessagePipeline(IProducer producer, IPipelineConfiguration pipelineConfig, IServiceProvider serviceProvider)
    {
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
        _pipelineConfig = pipelineConfig ?? throw new ArgumentNullException(nameof(pipelineConfig));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public Task ExecutePublishMessagePipelineAsync(Type typeObject, byte[] messageBytes, Dictionary<string, string>? headers = null, string? endPoint = null)
    {
        SendMessageDelegate terminal = (t, b, h, ep) => _producer.PublishAsync(t, b, h);
        var chain = BuildChain(terminal);
        return chain(typeObject, messageBytes, headers ?? new Dictionary<string, string>(), endPoint);
    }

    public Task ExecuteSendMessagePipelineAsync(Type typeObject, byte[] messageBytes, Dictionary<string, string>? headers = null, string? endPoint = null)
    {
        SendMessageDelegate terminal = (t, b, h, ep) =>
        {
            if (!string.IsNullOrEmpty(ep))
                return _producer.SendAsync(ep, t, b, h);
            return _producer.SendAsync(t, b, h);
        };
        var chain = BuildChain(terminal);
        return chain(typeObject, messageBytes, headers ?? new Dictionary<string, string>(), endPoint);
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
            mw.Next = chain;
            var current = mw;
            chain = (t, b, h, ep) => current.Process(t, b, h, ep);
        }
        return chain;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _producer.Dispose();
    }
}
