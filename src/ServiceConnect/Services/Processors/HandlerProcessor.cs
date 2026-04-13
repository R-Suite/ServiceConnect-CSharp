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

        // Walk up the message hierarchy — stop at Message and object. All matching
        // handlers in the hierarchy are invoked. Defer list allocation until we
        // actually find a handler (P-05); most no-op dispatches keep the list null.
        List<(object Handler, MessageHandlerDescriptor Descriptor)>? invocations = null;
        var checkedType = messageType;
        while (checkedType != null && checkedType != typeof(Message) && checkedType != typeof(object))
        {
            if (registry.TryGetOrBuild(checkedType, out var descriptor))
            {
                foreach (var h in serviceProvider.GetServices(descriptor.HandlerInterfaceType))
                {
                    if (h != null)
                        (invocations ??= new(capacity: 1)).Add((h, descriptor));
                }
            }
            checkedType = checkedType.BaseType;
        }

        if (invocations is null)
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

    // AMQP queue/exchange names are at most 255 bytes; we clamp tighter and reject
    // characters that are either structural in AMQP routing or commonly used in
    // injection attempts. This guards against attacker-controlled RoutingSlip headers
    // redirecting traffic to arbitrary queues.
    private const int MaxRoutingSlipDestinationLength = 128;
    private static readonly char[] ForbiddenRoutingSlipChars = ['*', '#', '\0', '\r', '\n', '\t', '"', '\''];

    private static async Task ForwardRoutingSlipAsync(object message, Type messageType, IDictionary<string, object> headers, IBus bus, CancellationToken cancellationToken)
    {
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
            destinations.Add(trimmed);
        }

        if (destinations.Count == 0)
            return;

        // MakeGenericMethod here is intentionally retained — IBus.RouteAsync is a generic method
        // with no descriptor to compile against. Separate concern from R-009.
        var routeMethod = typeof(IBus).GetMethod(nameof(IBus.RouteAsync))!.MakeGenericMethod(messageType);
        var task = (Task)routeMethod.Invoke(bus, [message, destinations, cancellationToken])!;
        await task.ConfigureAwait(false);
    }

    private static bool IsValidRoutingSlipDestination(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) return false;
        if (destination.Length > MaxRoutingSlipDestinationLength) return false;
        if (destination.IndexOfAny(ForbiddenRoutingSlipChars) >= 0) return false;
        return true;
    }
}
