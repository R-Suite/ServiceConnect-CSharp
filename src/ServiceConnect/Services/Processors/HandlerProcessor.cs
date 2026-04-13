using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

public sealed class HandlerProcessor(IServiceProvider serviceProvider) : IMessageProcessor
{
    public async Task<ProcessResult> ProcessAsync(
        byte[] messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (message == null) return ProcessResult.NotHandled;

        // Resolve handlers — walk up base types, stop before Message and object.
        // All matching handlers in the hierarchy are invoked (not just the most specific).
        var allHandlers = new List<(object Handler, Type InterfaceType)>();
        var checkedType = messageType;
        while (checkedType != null && checkedType != typeof(Message) && checkedType != typeof(object))
        {
            var handlerInterfaceType = typeof(IMessageHandler<>).MakeGenericType(checkedType);
            var handlers = serviceProvider.GetServices(handlerInterfaceType);
            foreach (var h in handlers)
            {
                if (h != null)
                    allHandlers.Add((h, handlerInterfaceType));
            }
            checkedType = checkedType.BaseType;
        }

        if (allHandlers.Count == 0)
            return ProcessResult.NotHandled;

        var bus = serviceProvider.GetRequiredService<IBus>();
        var context = new ConsumeContext(bus, headers) { CancellationToken = cancellationToken };

        foreach (var (handler, resolvedInterface) in allHandlers)
        {
            var contextProperty = resolvedInterface.GetProperty("Context");
            var handleAsyncMethod = resolvedInterface.GetMethod("HandleAsync");

            contextProperty?.SetValue(handler, context);

            var task = (Task?)handleAsyncMethod?.Invoke(handler, [message]);
            if (task != null)
                await task;
        }

        await ForwardRoutingSlipAsync(message, messageType, headers, bus, cancellationToken);

        return ProcessResult.Handled;
    }

    private async Task ForwardRoutingSlipAsync(object message, Type messageType, IDictionary<string, object> headers, IBus bus, CancellationToken cancellationToken)
    {
        if (!headers.TryGetValue(HeaderKeys.RoutingSlip, out var routingSlipRaw))
            return;

        var routingSlip = HeaderDecoder.Decode(routingSlipRaw);

        if (string.IsNullOrWhiteSpace(routingSlip))
            return;

        var destinations = routingSlip.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim())
            .ToList();

        if (destinations.Count == 0)
            return;

        var routeMethod = typeof(IBus).GetMethod(nameof(IBus.RouteAsync))!.MakeGenericMethod(messageType);
        var task = (Task)routeMethod.Invoke(bus, [message, destinations, cancellationToken])!;
        await task;
    }
}
