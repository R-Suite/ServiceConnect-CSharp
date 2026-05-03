using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed class StreamHandlerRegistry : IHandlerRegistry
{
    // Built once at construction; FrozenDictionary for read-heavy lookup.
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
            {
                continue;
            }

            if (builder.TryGetValue(href.MessageType, out var existing))
            {
                if (existing.HandlerType == href.HandlerType)
                {
                    continue; // identical (MessageType, HandlerType) pair registered twice — dedupe silently
                }

                throw new InvalidOperationException(
                    $"Duplicate stream-handler registration for message type '{href.MessageType.FullName}'. " +
                    $"Only one IStreamHandler<T> may be registered per message type; found '{existing.HandlerType.FullName}' and '{href.HandlerType.FullName}'.");
            }

            var descriptor = BuildDescriptor(href.MessageType, streamInterface);
            builder[href.MessageType] = (descriptor, href.HandlerType);

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "Registered stream-handler descriptor: message={MessageType}, handler={HandlerType}",
                    href.MessageType.Name, href.HandlerType.Name);
            }
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
            InvokeExecuteAsync: CompileInvokeExecuteAsync(handlerInterfaceType, messageType));
    }

    private static Func<object, object, IMessageBusReadStream, CancellationToken, Task> CompileInvokeExecuteAsync(
        Type handlerInterface, Type messageType)
    {
        var handlerParam = Expression.Parameter(typeof(object), "handler");
        var messageParam = Expression.Parameter(typeof(object), "message");
        var streamParam = Expression.Parameter(typeof(IMessageBusReadStream), "stream");
        var ctParam = Expression.Parameter(typeof(CancellationToken), "cancellationToken");

        var handlerCast = Expression.Convert(handlerParam, handlerInterface);
        var messageCast = Expression.Convert(messageParam, messageType);

        var method = handlerInterface.GetMethod(
            "ExecuteAsync",
            [messageType, typeof(IMessageBusReadStream), typeof(CancellationToken)])!;
        var call = Expression.Call(handlerCast, method, messageCast, streamParam, ctParam);

        return Expression.Lambda<Func<object, object, IMessageBusReadStream, CancellationToken, Task>>(
            call, handlerParam, messageParam, streamParam, ctParam).Compile();
    }
}
