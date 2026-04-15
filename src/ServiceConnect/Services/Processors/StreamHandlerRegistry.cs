using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed class StreamHandlerRegistry : IHandlerRegistry
{
    // Built once at construction; FrozenDictionary for read-heavy lookup (A-12).
    private readonly FrozenDictionary<Type, StreamHandlerDescriptor> _descriptors;

    internal StreamHandlerRegistry(
        IList<HandlerReference> handlerReferences,
        ILogger<StreamHandlerRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(handlerReferences);
        ArgumentNullException.ThrowIfNull(logger);

        var builder = new Dictionary<Type, (StreamHandlerDescriptor Descriptor, Type HandlerType)>();
        foreach (var href in handlerReferences)
        {
            var streamInterface = FindStreamHandlerInterface(href.HandlerType, href.MessageType);
            if (streamInterface == null)
                continue;

            if (builder.TryGetValue(href.MessageType, out var existing))
            {
                if (existing.HandlerType == href.HandlerType)
                    continue; // identical (MessageType, HandlerType) pair registered twice — dedupe silently
                throw new InvalidOperationException(
                    $"Duplicate stream-handler registration for message type '{href.MessageType.FullName}'. " +
                    $"Only one IStreamHandler<T> may be registered per message type; found '{existing.HandlerType.FullName}' and '{href.HandlerType.FullName}'.");
            }

            var descriptor = BuildDescriptor(href.MessageType, streamInterface);
            builder[href.MessageType] = (descriptor, href.HandlerType);

            logger.LogDebug(
                "Registered stream-handler descriptor: message={MessageType}, handler={HandlerType}",
                href.MessageType.Name, href.HandlerType.Name);
        }

        _descriptors = builder.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Descriptor).ToFrozenDictionary();
    }

    internal bool TryGet(Type messageType, [NotNullWhen(true)] out StreamHandlerDescriptor? descriptor)
        => _descriptors.TryGetValue(messageType, out descriptor);

    private static Type? FindStreamHandlerInterface(Type handlerType, Type messageType)
    {
        foreach (var interfaceType in handlerType.GetInterfaces())
        {
            if (interfaceType.IsGenericType
                && interfaceType.GetGenericTypeDefinition() == typeof(IStreamHandler<>)
                && interfaceType.GetGenericArguments()[0] == messageType)
            {
                return interfaceType;
            }
        }

        return null;
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
