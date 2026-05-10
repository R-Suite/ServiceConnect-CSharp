using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    ILogger<HandlerProcessor> logger,
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
        if (message == null)
        {
            return ProcessResult.NotHandled;
        }

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
                    {
                        (invocations ??= new(capacity: 1)).Add((h, descriptor));
                    }
                }
            }
            checkedType = checkedType.BaseType;
        }

        if (invocations is null)
        {
            return ProcessResult.NotHandled;
        }

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
                        await descriptor.InvokeHandleAsync(handler, message, context, cancellationToken).ConfigureAwait(false);
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
                        (handlerExceptions ??= []).Add(ex);
                    }
                }

                if (handlerExceptions is not null)
                {
                    throw new AggregateException(
                        $"{handlerExceptions.Count} handler(s) threw while dispatching {message.GetType().Name}.",
                        handlerExceptions);
                }

                // Slip-forward is decoupled from handler success. A failure here previously
                // surfaced as Success=false out of the dispatcher, putting the message on the
                // retry queue so the handler ran again on every redelivery until the retry
                // budget was exhausted — duplicating side effects that already succeeded.
                // Slip-forward is at-most-once on transient failure; a redelivered slip would
                // also re-run the handler, which is the wrong trade-off for any handler with
                // observable side effects (Send, HTTP, mutation). Cancellation still
                // propagates so cooperative shutdown is unaffected.
                try
                {
                    await ForwardRoutingSlipAsync(message, messageType, headers, resolvedBus, busConfig, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Routing-slip forward failed for {MessageType} after handlers succeeded; slip dropped to avoid handler re-run on retry.",
                        messageType.Name);
                }
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
    // Cache compiled delegates for IBus.RouteAsync<T> keyed by message type.
    // Building a delegate via Expression.Lambda avoids repeated MakeGenericMethod + Invoke
    // overhead on every routed message.
    private static readonly ConcurrentDictionary<Type, Func<IBus, object, IReadOnlyList<string>, CancellationToken, Task>>
        RouteAsyncDelegateCache = new();

    private static Func<IBus, object, IReadOnlyList<string>, CancellationToken, Task> BuildRouteAsyncDelegate(Type messageType)
    {
        // IBus.RouteAsync<T>(T message, IReadOnlyList<string> destinations, CancellationToken ct)
        var openMethod = typeof(IBus).GetMethod(nameof(IBus.RouteAsync))!;
        var closedMethod = openMethod.MakeGenericMethod(messageType);

        var busParam = Expression.Parameter(typeof(IBus), "bus");
        var msgParam = Expression.Parameter(typeof(object), "message");
        var destParam = Expression.Parameter(typeof(IReadOnlyList<string>), "destinations");
        var ctParam = Expression.Parameter(typeof(CancellationToken), "cancellationToken");

        // Cast the untyped object parameter to the concrete message type expected by RouteAsync<T>.
        var castMsg = Expression.Convert(msgParam, messageType);

        var callExpr = Expression.Call(busParam, closedMethod, castMsg, destParam, ctParam);
        return Expression.Lambda<Func<IBus, object, IReadOnlyList<string>, CancellationToken, Task>>(
            callExpr, busParam, msgParam, destParam, ctParam).Compile();
    }

    /// <summary>
    /// Forwards the routing slip's next-step destinations after all handlers complete
    /// successfully.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Slip drop on handler throw.</b> If any handler threw during dispatch, the
    /// caller throws an <see cref="AggregateException"/> BEFORE this method runs; the
    /// in-flight slip-forward is therefore skipped on partial-failure dispatches. The
    /// slip data remains in the message envelope (the <c>RoutingSlip</c> header is
    /// not stripped during dispatch), so DLQ-routed messages and manual retries still
    /// carry the slip and can resume the chain after the failure is resolved.
    /// </para>
    /// <para>
    /// <b>Cross-service destinations.</b> v8 removed the <c>IsKnownQueue</c> check;
    /// destinations that are not in the local <see cref="IQueueConfiguration"/> are
    /// allowed as long as they pass <see cref="IsValidRoutingSlipDestination"/> (format,
    /// length, no AMQP control characters). RabbitMQ routes via the alternate-exchange /
    /// mandatory-return path if the queue does not exist downstream.
    /// </para>
    /// </remarks>
    private static async Task ForwardRoutingSlipAsync(
        object message, Type messageType, IDictionary<string, object> headers,
        IBus bus, IBusConfiguration busConfig,
        CancellationToken cancellationToken)
    {
        if (!busConfig.EnableRoutingSlipProcessing)
        {
            return;
        }

        if (!headers.TryGetValue(HeaderKeys.RoutingSlip, out var routingSlipRaw))
        {
            return;
        }

        var routingSlip = HeaderDecoder.Decode(routingSlipRaw);

        if (string.IsNullOrWhiteSpace(routingSlip))
        {
            return;
        }

        var destinations = new List<string>();
        foreach (var raw in routingSlip.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = raw.Trim();
            if (!IsValidRoutingSlipDestination(trimmed))
            {
                throw new InvalidOperationException(
                    $"Invalid routing-slip destination '{trimmed}'. Destinations must be non-empty, at most {RoutingSlipDestinationValidator.MaxDestinationLength} characters, and must not contain AMQP wildcards or control characters.");
            }

            destinations.Add(trimmed);
        }

        if (destinations.Count == 0)
        {
            return;
        }

        var routeDelegate = RouteAsyncDelegateCache.GetOrAdd(messageType, BuildRouteAsyncDelegate);
        await routeDelegate(bus, message, destinations, cancellationToken).ConfigureAwait(false);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsValidRoutingSlipDestination(string destination) =>
        RoutingSlipDestinationValidator.IsValid(destination);
}
