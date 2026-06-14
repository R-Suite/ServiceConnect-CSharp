using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed class MessageHandlerRegistry : IHandlerRegistry
{
    private readonly FrozenDictionary<Type, MessageHandlerDescriptor?> _knownDescriptors;
    private readonly ConcurrentDictionary<Type, MessageHandlerDescriptor?> _lazyDescriptors = new();
    private readonly ILogger<MessageHandlerRegistry> _logger;

    internal MessageHandlerRegistry(
        IReadOnlyList<HandlerReference> handlerReferences,
        ILogger<MessageHandlerRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(handlerReferences);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var builder = new Dictionary<Type, MessageHandlerDescriptor?>();
        foreach (var href in handlerReferences)
        {
            var messageHandlerInterface = FindMessageHandlerInterface(href.HandlerType, href.MessageType);
            if (messageHandlerInterface == null)
            {
                // Record the message type as seen (null = no IMessageHandler<T>) so
                // the lazy-build path does not fabricate a descriptor for a type the
                // application only handles via process-manager or stream handlers.
                builder.TryAdd(href.MessageType, null);
                continue;
            }

            // The dispatch walk in HandlerProcessor stops at typeof(Message) and typeof(object),
            // so a handler registered for either base type would silently never be invoked.
            // Fail fast here so the misconfiguration surfaces at startup rather than at runtime.
            // Use IFilter / IMessageProcessingMiddleware for catch-all message interception.
            if (href.MessageType == typeof(Message) || href.MessageType == typeof(object))
            {
                var baseTypeName = href.MessageType == typeof(Message) ? "IMessageHandler<Message>" : "IMessageHandler<object>";
                throw new InvalidOperationException(
                    $"Handler '{href.HandlerType.FullName}' implements {baseTypeName}, which is the catch-all base type. " +
                    $"The dispatch walk stops before reaching {href.MessageType.Name}, so this handler would never be invoked. " +
                    $"Use IFilter or IMessageProcessingMiddleware for catch-all message interception.");
            }

            // Duplicates are legitimate — multiple handler classes for one message type are allowed.
            // Only one descriptor per message type (it describes the interface, not the instances).
            // Overwrite a previous null (from a non-message-handler ref for the same type).
            builder[href.MessageType] = BuildDescriptor(href.MessageType, messageHandlerInterface);

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Registered message-handler descriptor: message={MessageType}, handler={HandlerType}",
                    href.MessageType.Name, href.HandlerType.Name);
            }
        }

        _knownDescriptors = builder.ToFrozenDictionary();
    }

    internal bool TryGetOrBuild(Type messageType, [NotNullWhen(true)] out MessageHandlerDescriptor? descriptor)
    {
        if (_knownDescriptors.TryGetValue(messageType, out descriptor))
        {
            return descriptor != null;
        }

        if (_lazyDescriptors.TryGetValue(messageType, out descriptor))
        {
            return descriptor != null;
        }

        // Build a descriptor for any type not seen at construction so users who register
        // IMessageHandler<T> directly in DI (without going through AddServiceConnect's
        // scanner) still get their handlers invoked. The cache grows by one entry per
        // distinct message type observed at runtime — bounded in practice by the bus's
        // message-type catalogue.
        descriptor = _lazyDescriptors.GetOrAdd(messageType, TryBuild);
        return descriptor != null;
    }

    private static MessageHandlerDescriptor? TryBuild(Type messageType)
    {
        if (messageType == typeof(Message) || messageType == typeof(object))
        {
            return null;
        }

        var handlerInterfaceType = typeof(IMessageHandler<>).MakeGenericType(messageType);
        return BuildDescriptor(messageType, handlerInterfaceType);
    }

    private static Type? FindMessageHandlerInterface(Type handlerType, Type messageType)
    {
        foreach (var interfaceType in handlerType.GetInterfaces())
        {
            if (interfaceType.IsGenericType
                && interfaceType.GetGenericTypeDefinition() == typeof(IMessageHandler<>)
                && interfaceType.GetGenericArguments()[0] == messageType)
            {
                return interfaceType;
            }
        }

        return null;
    }

    private static MessageHandlerDescriptor BuildDescriptor(Type messageType, Type handlerInterfaceType)
    {
        return new MessageHandlerDescriptor(
            MessageType: messageType,
            HandlerInterfaceType: handlerInterfaceType,
            InvokeHandleAsync: CompileInvokeHandleAsync(handlerInterfaceType, messageType));
    }

    private static Func<object, object, IConsumeContext, CancellationToken, Task> CompileInvokeHandleAsync(
        Type handlerInterface, Type messageType)
    {
        var handlerParam = Expression.Parameter(typeof(object), "handler");
        var messageParam = Expression.Parameter(typeof(object), "message");
        var ctxParam = Expression.Parameter(typeof(IConsumeContext), "context");
        var ctParam = Expression.Parameter(typeof(CancellationToken), "ct");

        var handlerCast = Expression.Convert(handlerParam, handlerInterface);
        var messageCast = Expression.Convert(messageParam, messageType);

        var method = handlerInterface.GetMethod(
            "HandleAsync",
            [messageType, typeof(IConsumeContext), typeof(CancellationToken)])!;
        var call = Expression.Call(handlerCast, method, messageCast, ctxParam, ctParam);

        return Expression.Lambda<Func<object, object, IConsumeContext, CancellationToken, Task>>(
            call, handlerParam, messageParam, ctxParam, ctParam).Compile();
    }
}
