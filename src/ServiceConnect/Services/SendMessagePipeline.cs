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
    private int _disposed;

    /// <summary>
    /// Creates a send pipeline backed by a producer and optional outbound middleware.
    /// </summary>
    /// <param name="producer">The producer that performs the terminal send or publish operation.</param>
    /// <param name="pipelineConfig">The pipeline configuration that supplies middleware types.</param>
    /// <param name="serviceProvider">The service provider used to resolve middleware instances.</param>
    public SendMessagePipeline(IProducer producer, IPipelineConfiguration pipelineConfig, IServiceProvider serviceProvider)
    {
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
        _pipelineConfig = pipelineConfig ?? throw new ArgumentNullException(nameof(pipelineConfig));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _publishChain = new Lazy<SendMessageDelegate>(BuildPublishChain, isThreadSafe: true);
        _sendChain = new Lazy<SendMessageDelegate>(BuildSendChain, isThreadSafe: true);
    }

    /// <inheritdoc />
    public Task ExecutePublishMessagePipelineAsync(SendContext context, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(context);
        return _publishChain.Value(context, cancellationToken);
    }

    /// <inheritdoc />
    public Task ExecuteSendMessagePipelineAsync(SendContext context, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(context);
        return _sendChain.Value(context, cancellationToken);
    }

    private SendMessageDelegate BuildPublishChain()
    {
        var producer = _producer;
        Task terminal(SendContext ctx, CancellationToken ct) =>
            producer.PublishAsync(ctx.MessageType, ctx.MessageBytes, ToReadOnly(ctx.Headers), ct);
        return WrapMiddleware(terminal);
    }

    private SendMessageDelegate BuildSendChain()
    {
        var producer = _producer;
        Task terminal(SendContext ctx, CancellationToken ct) =>
            !string.IsNullOrEmpty(ctx.EndPoint)
                ? producer.SendAsync(ctx.EndPoint, ctx.MessageType, ctx.MessageBytes, ToReadOnly(ctx.Headers), ct)
                : producer.SendAsync(ctx.MessageType, ctx.MessageBytes, ToReadOnly(ctx.Headers), ct);
        return WrapMiddleware(terminal);
    }

    // SendContext.Headers is IDictionary<string,string> for middleware mutability; IProducer
    // accepts IReadOnlyDictionary<string,string> as a tighter contract. Bus.cs constructs the
    // headers as a concrete Dictionary<string,string> which implements both, so the runtime
    // cast succeeds without copying. Defensive fallback wraps any other IDictionary impl in a
    // shallow copy so the read-only contract is honoured.
    private static IReadOnlyDictionary<string, string>? ToReadOnly(IDictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return null;
        }

        if (headers is IReadOnlyDictionary<string, string> ro)
        {
            return ro;
        }

        return new Dictionary<string, string>(headers, StringComparer.Ordinal);
    }

    private SendMessageDelegate WrapMiddleware(SendMessageDelegate terminal)
    {
        var middlewareTypes = _pipelineConfig.SendMessageMiddleware;
        if (middlewareTypes.Count == 0)
        {
            return terminal;
        }

        var chain = terminal;
        for (int i = middlewareTypes.Count - 1; i >= 0; i--)
        {
            var mw = (ISendMessageMiddleware)_serviceProvider.GetRequiredService(middlewareTypes[i]);
            var next = chain;
            chain = (ctx, ct) => mw.ProcessAsync(ctx, next, ct);
        }
        return chain;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        // Producer lifetime is managed by the DI container — do not dispose it here
        return ValueTask.CompletedTask;
    }
}
