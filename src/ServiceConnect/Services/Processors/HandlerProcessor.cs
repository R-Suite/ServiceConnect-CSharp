using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;

namespace ServiceConnect.Services.Processors;

internal sealed class HandlerProcessor(
    MessageHandlerRegistry registry,
    ConsumeScopeAccessor scopeAccessor,
    Lazy<IBus> bus,
    IBusConfiguration busConfig,
    IQueueConfiguration queueConfig,
    ConsumeContextPool contextPool,
    ConsumeContextAccessor consumeContextAccessor,
    IReplyStatusRequestReplyManager? replyStatusRequestReplyManager = null) : IMessageProcessor
{
    private readonly ConsumeContextAccessor _consumeContextAccessor = consumeContextAccessor;
    private readonly ConsumeContextPool _contextPool = contextPool;

    public async Task<ProcessResult> ProcessAsync(
        ReadOnlyMemory<byte> messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (message == null) return ProcessResult.NotHandled;

        var scopedProvider = scopeAccessor.Current;

        // Walk up the message hierarchy — stop at Message and object. All matching
        // handlers in the hierarchy are invoked. Defer list allocation until we
        // actually find a handler; most no-op dispatches keep the list null.
        List<(object Handler, MessageHandlerDescriptor Descriptor)>? invocations = null;
        var checkedType = messageType;
        while (checkedType != null && checkedType != typeof(Message) && checkedType != typeof(object))
        {
            if (registry.TryGetOrBuild(checkedType, out var descriptor))
            {
                foreach (var h in scopedProvider.GetServices(descriptor.HandlerInterfaceType))
                {
                    if (h != null)
                        (invocations ??= new(capacity: 1)).Add((h, descriptor));
                }
            }
            checkedType = checkedType.BaseType;
        }

        if (invocations is null)
            return ProcessResult.NotHandled;

        var resolvedBus = bus.Value;
        var trustQuery = replyStatusRequestReplyManager
            ?? scopedProvider.GetService<IReplyStatusRequestReplyManager>()
            ?? scopedProvider.GetService<IRequestReplyManager>() as IReplyStatusRequestReplyManager;
        var context = _contextPool.Rent(
            resolvedBus,
            headers,
            queueConfig,
            busConfig,
            trustQuery,
            cancellationToken);
        try
        {
            using (_consumeContextAccessor.Push(context.Headers))
            {
                List<Exception>? handlerExceptions = null;
                foreach (var (handler, descriptor) in invocations)
                {
                    try
                    {
                        descriptor.SetContext(handler, context);
                        await descriptor.InvokeHandleAsync(handler, message).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Co-operative shutdown — don't run remaining handlers; rethrow the OCE
                        // unaltered so callers can distinguish shutdown from handler faults.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Collect handler faults so independent handlers for the same message
                        // all get a chance to run; aggregate at end of loop.
                        (handlerExceptions ??= new List<Exception>()).Add(ex);
                    }
                }

                if (handlerExceptions is not null)
                    throw new AggregateException(
                        $"{handlerExceptions.Count} handler(s) threw while dispatching {message.GetType().Name}.",
                        handlerExceptions);

                await ForwardRoutingSlipAsync(message, messageType, headers, resolvedBus, busConfig, queueConfig, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            context.Release();
        }

        return ProcessResult.Handled;
    }

    // AMQP queue/exchange names are at most 255 bytes; we clamp tighter and reject
    // characters that are either structural in AMQP routing or commonly used in
    // injection attempts. This guards against attacker-controlled RoutingSlip headers
    // redirecting traffic to arbitrary queues.
    private const int MaxRoutingSlipDestinationLength = 128;
    private static readonly char[] ForbiddenRoutingSlipChars = ['*', '#', '\0', '\r', '\n', '\t', '"', '\''];

    // Cache compiled delegates for IBus.RouteAsync<T> keyed by message type.
    // Building a delegate via Expression.Lambda avoids repeated MakeGenericMethod + Invoke
    // overhead on every routed message.
    private static readonly ConcurrentDictionary<Type, Func<IBus, object, IList<string>, CancellationToken, Task>>
        RouteAsyncDelegateCache = new();

    private static Func<IBus, object, IList<string>, CancellationToken, Task> BuildRouteAsyncDelegate(Type messageType)
    {
        // IBus.RouteAsync<T>(T message, IList<string> destinations, CancellationToken ct)
        var openMethod = typeof(IBus).GetMethod(nameof(IBus.RouteAsync))!;
        var closedMethod = openMethod.MakeGenericMethod(messageType);

        var busParam = Expression.Parameter(typeof(IBus), "bus");
        var msgParam = Expression.Parameter(typeof(object), "message");
        var destParam = Expression.Parameter(typeof(IList<string>), "destinations");
        var ctParam = Expression.Parameter(typeof(CancellationToken), "cancellationToken");

        // Cast the untyped object parameter to the concrete message type expected by RouteAsync<T>.
        var castMsg = Expression.Convert(msgParam, messageType);

        var callExpr = Expression.Call(busParam, closedMethod, castMsg, destParam, ctParam);
        return Expression.Lambda<Func<IBus, object, IList<string>, CancellationToken, Task>>(
            callExpr, busParam, msgParam, destParam, ctParam).Compile();
    }

    private static async Task ForwardRoutingSlipAsync(
        object message, Type messageType, IDictionary<string, object> headers,
        IBus bus, IBusConfiguration busConfig, IQueueConfiguration queueConfig,
        CancellationToken cancellationToken)
    {
        if (!busConfig.EnableRoutingSlipProcessing)
            return;

        if (!headers.TryGetValue(HeaderKeys.RoutingSlip, out var routingSlipRaw))
            return;

        var routingSlip = HeaderDecoder.Decode(routingSlipRaw);

        if (string.IsNullOrWhiteSpace(routingSlip))
            return;

        var destinations = new List<string>();
        foreach (var raw in routingSlip.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = raw.Trim();
            if (!IsValidRoutingSlipDestination(trimmed))
                throw new InvalidOperationException(
                    $"Invalid routing-slip destination '{trimmed}'. Destinations must be non-empty, at most {MaxRoutingSlipDestinationLength} characters, and must not contain AMQP wildcards or control characters.");
            if (!ConsumeContext.IsKnownQueue(trimmed, queueConfig))
                throw new InvalidOperationException(
                    $"Routing-slip destination '{trimmed}' is not a recognized queue. " +
                    "Configure queue mappings to allow this destination.");
            destinations.Add(trimmed);
        }

        if (destinations.Count == 0)
            return;

        var routeDelegate = RouteAsyncDelegateCache.GetOrAdd(messageType, BuildRouteAsyncDelegate);
        await routeDelegate(bus, message, destinations, cancellationToken).ConfigureAwait(false);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsValidRoutingSlipDestination(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) return false;
        if (destination.Length > MaxRoutingSlipDestinationLength) return false;
        if (destination.IndexOfAny(ForbiddenRoutingSlipChars) >= 0) return false;
        return true;
    }
}
