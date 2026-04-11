using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

public class HandlerProcessor : IMessageProcessor
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<HandlerProcessor> _logger;

    public HandlerProcessor(IServiceProvider serviceProvider, ILogger<HandlerProcessor> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<ProcessResult> ProcessAsync(
        byte[] messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope)
    {
        if (message == null) return ProcessResult.NotHandled;

        // Resolve handlers — walk up base types, stop before Message and object.
        // All matching handlers in the hierarchy are invoked (not just the most specific).
        var allHandlers = new List<(object Handler, Type InterfaceType)>();
        var checkedType = messageType;
        while (checkedType != null && checkedType != typeof(Message) && checkedType != typeof(object))
        {
            var handlerInterfaceType = typeof(IMessageHandler<>).MakeGenericType(checkedType);
            var handlers = _serviceProvider.GetServices(handlerInterfaceType);
            foreach (var h in handlers)
            {
                if (h != null)
                    allHandlers.Add((h, handlerInterfaceType));
            }
            checkedType = checkedType.BaseType;
        }

        if (allHandlers.Count == 0)
            return ProcessResult.NotHandled;

        var bus = _serviceProvider.GetRequiredService<IBus>();
        var context = new ConsumeContext(bus, headers);

        foreach (var (handler, resolvedInterface) in allHandlers)
        {
            var contextProperty = resolvedInterface.GetProperty("Context");
            var handleAsyncMethod = resolvedInterface.GetMethod("HandleAsync");

            contextProperty?.SetValue(handler, context);

            var task = (Task?)handleAsyncMethod?.Invoke(handler, new[] { message });
            if (task != null)
                await task;
        }

        return ProcessResult.Handled;
    }
}
