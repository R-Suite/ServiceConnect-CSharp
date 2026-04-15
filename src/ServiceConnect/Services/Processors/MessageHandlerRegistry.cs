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
        IList<HandlerReference> handlerReferences,
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

            // Duplicates are legitimate — multiple handler classes for one message type are allowed.
            // Only one descriptor per message type (it describes the interface, not the instances).
            // Overwrite a previous null (from a non-message-handler ref for the same type).
            builder[href.MessageType] = BuildDescriptor(href.MessageType, messageHandlerInterface);

            _logger.LogDebug(
                "Registered message-handler descriptor: message={MessageType}, handler={HandlerType}",
                href.MessageType.Name, href.HandlerType.Name);
        }

        _knownDescriptors = builder.ToFrozenDictionary();
    }

    internal bool TryGetOrBuild(Type messageType, [NotNullWhen(true)] out MessageHandlerDescriptor? descriptor)
    {
        if (_knownDescriptors.TryGetValue(messageType, out descriptor))
            return descriptor != null;

        if (_lazyDescriptors.TryGetValue(messageType, out descriptor))
            return descriptor != null;

        descriptor = _lazyDescriptors.GetOrAdd(messageType, TryBuild);
        return descriptor != null;
    }

    private static MessageHandlerDescriptor? TryBuild(Type messageType)
    {
        if (messageType == typeof(Message) || messageType == typeof(object))
            return null;

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
            SetContext: CompileSetContext(handlerInterfaceType),
            InvokeHandleAsync: CompileInvokeHandleAsync(handlerInterfaceType, messageType));
    }

    private static Action<object, IConsumeContext> CompileSetContext(Type handlerInterface)
    {
        var handlerParam = Expression.Parameter(typeof(object), "handler");
        var ctxParam = Expression.Parameter(typeof(IConsumeContext), "ctx");
        var cast = Expression.Convert(handlerParam, handlerInterface);
        var prop = handlerInterface.GetProperty("Context")!;
        var assign = Expression.Assign(Expression.Property(cast, prop), ctxParam);
        return Expression.Lambda<Action<object, IConsumeContext>>(assign, handlerParam, ctxParam).Compile();
    }

    private static Func<object, object, Task> CompileInvokeHandleAsync(Type handlerInterface, Type messageType)
    {
        var handlerParam = Expression.Parameter(typeof(object), "handler");
        var messageParam = Expression.Parameter(typeof(object), "message");

        var handlerCast = Expression.Convert(handlerParam, handlerInterface);
        var messageCast = Expression.Convert(messageParam, messageType);

        var method = handlerInterface.GetMethod("HandleAsync")!;
        var call = Expression.Call(handlerCast, method, messageCast);

        return Expression.Lambda<Func<object, object, Task>>(call, handlerParam, messageParam).Compile();
    }
}
