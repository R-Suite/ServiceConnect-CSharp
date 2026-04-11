using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

public class MessageDispatcher
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IMessageSerializer _serializer;
    private readonly IFilterPipeline _filterPipeline;
    private readonly IRequestReplyManager _replyManager;
    private readonly ILogger<MessageDispatcher> _logger;

    public MessageDispatcher(
        IServiceProvider serviceProvider,
        IMessageSerializer serializer,
        IFilterPipeline filterPipeline,
        IRequestReplyManager replyManager,
        ILogger<MessageDispatcher> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _filterPipeline = filterPipeline ?? throw new ArgumentNullException(nameof(filterPipeline));
        _replyManager = replyManager ?? throw new ArgumentNullException(nameof(replyManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ConsumeEventResult> Dispatch(byte[] messageBytes, string messageType, IDictionary<string, object> headers)
    {
        try
        {
            // 1. Resolve CLR Type from FullTypeName header (RabbitMQ sends header values as byte[])
            if (!headers.TryGetValue(HeaderKeys.FullTypeName, out var fullTypeNameRaw))
                throw new InvalidOperationException("Message is missing FullTypeName header.");

            var fullTypeName = fullTypeNameRaw is byte[] bytes
                ? Encoding.UTF8.GetString(bytes)
                : fullTypeNameRaw?.ToString() ?? throw new InvalidOperationException("FullTypeName header is null.");

            var type = Type.GetType(fullTypeName)
                ?? throw new InvalidOperationException($"Cannot resolve type '{fullTypeName}'.");

            // 2. Check for ResponseMessageId header — route to reply manager
            if (headers.TryGetValue(HeaderKeys.ResponseMessageId, out var responseMessageIdRaw))
            {
                var responseMessageId = responseMessageIdRaw is byte[] rmidBytes
                    ? Encoding.UTF8.GetString(rmidBytes)
                    : responseMessageIdRaw?.ToString();

                if (!string.IsNullOrEmpty(responseMessageId))
                {
                    _replyManager.ProcessReply(responseMessageId, messageBytes, type);
                    return new ConsumeEventResult { Success = true };
                }
            }

            // 3. Deserialize the message
            var message = _serializer.Deserialize(messageBytes, type);

            // 4. Build Envelope and run BeforeConsumingFilters
            var envelope = new Envelope { Headers = headers, Body = messageBytes };
            bool blocked = _filterPipeline.ExecuteBeforeConsumingFilters(envelope);
            if (blocked)
                return new ConsumeEventResult { Success = true };

            // 5. Resolve handlers — check exact type, then walk up base types
            // (interface-based handlers like IMessageHandler<IEvent> are not supported)
            var allHandlers = new List<(object Handler, Type InterfaceType)>();
            var checkedType = type;
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
            {
                _logger.LogWarning("No handlers found for message type {MessageType}", type.FullName);
                return new ConsumeEventResult { Success = true };
            }

            // 6. Create ConsumeContext and dispatch to each handler
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

            // 7. Run AfterConsumingFilters
            _filterPipeline.ExecuteAfterConsumingFilters(envelope);

            // 8. Return success
            return new ConsumeEventResult { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dispatching message of type {MessageType}", messageType);
            return new ConsumeEventResult { Success = false, Exception = ex };
        }
    }
}
