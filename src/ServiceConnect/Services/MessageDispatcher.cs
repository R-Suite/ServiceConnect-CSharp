using System;
using System.Collections.Generic;
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
            if (headers.TryGetValue("ResponseMessageId", out var responseMessageIdRaw))
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

            // 5. Resolve handlers
            var handlerInterfaceType = typeof(IMessageHandler<>).MakeGenericType(type);
            var handlers = _serviceProvider.GetServices(handlerInterfaceType);

            // 6. Create ConsumeContext and dispatch to each handler
            var bus = _serviceProvider.GetRequiredService<IBus>();
            var context = new ConsumeContext(bus, headers);
            var contextProperty = handlerInterfaceType.GetProperty("Context");
            var handleAsyncMethod = handlerInterfaceType.GetMethod("HandleAsync");

            foreach (var handler in handlers)
            {
                if (handler == null) continue;

                // Set context via reflection
                contextProperty?.SetValue(handler, context);

                // Call HandleAsync via reflection
                var task = (Task?)handleAsyncMethod?.Invoke(handler, new[] { message });
                if (task != null)
                    await task;
            }

            // 8. Run AfterConsumingFilters
            _filterPipeline.ExecuteAfterConsumingFilters(envelope);

            // 9. Return success
            return new ConsumeEventResult { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dispatching message of type {MessageType}", messageType);
            return new ConsumeEventResult { Success = false, Exception = ex };
        }
    }
}
