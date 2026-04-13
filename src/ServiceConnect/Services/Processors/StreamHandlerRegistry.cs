using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed class StreamHandlerRegistry
{
    private readonly Dictionary<Type, (StreamHandlerDescriptor Descriptor, Type HandlerType)> _descriptors = new();

    internal StreamHandlerRegistry(
        IList<HandlerReference> handlerReferences,
        ILogger<StreamHandlerRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(handlerReferences);
        ArgumentNullException.ThrowIfNull(logger);

        foreach (var href in handlerReferences)
        {
            var streamInterface = FindStreamHandlerInterface(href.HandlerType, href.MessageType);
            if (streamInterface == null)
                continue;

            if (_descriptors.TryGetValue(href.MessageType, out var existing))
            {
                if (existing.HandlerType == href.HandlerType)
                    continue; // identical (MessageType, HandlerType) pair registered twice — dedupe silently
                throw new InvalidOperationException(
                    $"Duplicate stream-handler registration for message type '{href.MessageType.FullName}'. " +
                    $"Only one IStreamHandler<T> may be registered per message type; found '{existing.HandlerType.FullName}' and '{href.HandlerType.FullName}'.");
            }

            var descriptor = BuildDescriptor(href.MessageType, streamInterface);
            _descriptors[href.MessageType] = (descriptor, href.HandlerType);

            logger.LogDebug(
                "Registered stream-handler descriptor: message={MessageType}, handler={HandlerType}",
                href.MessageType.Name, href.HandlerType.Name);
        }
    }

    internal bool TryGet(Type messageType, [NotNullWhen(true)] out StreamHandlerDescriptor? descriptor)
    {
        if (_descriptors.TryGetValue(messageType, out var entry))
        {
            descriptor = entry.Descriptor;
            return true;
        }
        descriptor = null;
        return false;
    }

    private static Type? FindStreamHandlerInterface(Type handlerType, Type messageType)
    {
        return handlerType.GetInterfaces().FirstOrDefault(i =>
            i.IsGenericType
            && i.GetGenericTypeDefinition() == typeof(IStreamHandler<>)
            && i.GetGenericArguments()[0] == messageType);
    }

    private static StreamHandlerDescriptor BuildDescriptor(Type messageType, Type handlerInterfaceType)
    {
        return new StreamHandlerDescriptor(
            MessageType: messageType,
            HandlerInterfaceType: handlerInterfaceType,
            SetStream: CompileSetStream(handlerInterfaceType),
            InvokeExecute: CompileInvokeExecute(handlerInterfaceType, messageType));
    }

    private static Action<object, IMessageBusReadStream> CompileSetStream(Type handlerInterface)
    {
        var handlerParam = Expression.Parameter(typeof(object), "handler");
        var streamParam = Expression.Parameter(typeof(IMessageBusReadStream), "stream");
        var cast = Expression.Convert(handlerParam, handlerInterface);
        var prop = handlerInterface.GetProperty("Stream")!;
        var assign = Expression.Assign(Expression.Property(cast, prop), streamParam);
        return Expression.Lambda<Action<object, IMessageBusReadStream>>(assign, handlerParam, streamParam).Compile();
    }

    private static Action<object, object> CompileInvokeExecute(Type handlerInterface, Type messageType)
    {
        var handlerParam = Expression.Parameter(typeof(object), "handler");
        var messageParam = Expression.Parameter(typeof(object), "message");

        var handlerCast = Expression.Convert(handlerParam, handlerInterface);
        var messageCast = Expression.Convert(messageParam, messageType);

        var method = handlerInterface.GetMethod("Execute")!;
        var call = Expression.Call(handlerCast, method, messageCast);

        return Expression.Lambda<Action<object, object>>(call, handlerParam, messageParam).Compile();
    }
}
