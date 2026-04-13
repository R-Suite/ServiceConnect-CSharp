using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed class HandlerProcessor(
    MessageHandlerRegistry registry,
    IServiceProvider serviceProvider) : IMessageProcessor
{
    public async Task<ProcessResult> ProcessAsync(
        byte[] messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (message == null) return ProcessResult.NotHandled;

        // Walk up the message hierarchy — stop at Message and object.
        // All matching handlers in the hierarchy are invoked.
        var invocations = new List<(object Handler, MessageHandlerDescriptor Descriptor)>();
        var checkedType = messageType;
        while (checkedType != null && checkedType != typeof(Message) && checkedType != typeof(object))
        {
            if (registry.TryGetOrBuild(checkedType, out var descriptor))
            {
                foreach (var h in serviceProvider.GetServices(descriptor.HandlerInterfaceType))
                {
                    if (h != null)
                        invocations.Add((h, descriptor));
                }
            }
            checkedType = checkedType.BaseType;
        }

        if (invocations.Count == 0)
            return ProcessResult.NotHandled;

        var bus = serviceProvider.GetRequiredService<IBus>();
        var context = new ConsumeContext(bus, headers) { CancellationToken = cancellationToken };

        foreach (var (handler, descriptor) in invocations)
        {
            descriptor.SetContext(handler, context);
            await descriptor.InvokeHandleAsync(handler, message).ConfigureAwait(false);
        }

        await ForwardRoutingSlipAsync(message, messageType, headers, bus, cancellationToken).ConfigureAwait(false);

        return ProcessResult.Handled;
    }

    private static async Task ForwardRoutingSlipAsync(object message, Type messageType, IDictionary<string, object> headers, IBus bus, CancellationToken cancellationToken)
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

        // MakeGenericMethod here is intentionally retained — IBus.RouteAsync is a generic method
        // with no descriptor to compile against. Separate concern from R-009.
        var routeMethod = typeof(IBus).GetMethod(nameof(IBus.RouteAsync))!.MakeGenericMethod(messageType);
        var task = (Task)routeMethod.Invoke(bus, [message, destinations, cancellationToken])!;
        await task;
    }
}
