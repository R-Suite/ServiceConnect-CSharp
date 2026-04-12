using System.Reflection;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

public static class HandlerScanner
{
    public static IList<HandlerReference> ScanForHandlers(IEnumerable<Assembly> assemblies)
    {
        var handlerReferences = new List<HandlerReference>();
        var messageHandlerType = typeof(IMessageHandler<>);
        var processHandlerType = typeof(IProcessHandler<,>);
        var streamHandlerType = typeof(IStreamHandler<>);
        var aggregatorType = typeof(Aggregator<>);

        foreach (var assembly in assemblies)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition) continue;

                // Scan IMessageHandler<T>
                foreach (var iface in type.GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == messageHandlerType))
                {
                    var messageType = iface.GetGenericArguments()[0];
                    if (messageType.IsGenericParameter) continue;
                    handlerReferences.Add(new HandlerReference
                    {
                        HandlerType = type,
                        MessageType = messageType,
                        RoutingKeys = []
                    });
                }

                // Scan IProcessHandler<TData, TMessage> — message type is the last generic arg
                foreach (var iface in type.GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == processHandlerType))
                {
                    var messageType = iface.GetGenericArguments()[1];
                    if (messageType.IsGenericParameter) continue;
                    handlerReferences.Add(new HandlerReference
                    {
                        HandlerType = type,
                        MessageType = messageType,
                        RoutingKeys = []
                    });
                }

                // Scan IStreamHandler<T>
                foreach (var iface in type.GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == streamHandlerType))
                {
                    var messageType = iface.GetGenericArguments()[0];
                    if (messageType.IsGenericParameter) continue;
                    handlerReferences.Add(new HandlerReference
                    {
                        HandlerType = type,
                        MessageType = messageType,
                        RoutingKeys = []
                    });
                }

                // Scan Aggregator<T> subclasses
                if (type.BaseType is { IsGenericType: true } baseType &&
                    baseType.GetGenericTypeDefinition() == aggregatorType)
                {
                    var messageType = baseType.GetGenericArguments()[0];
                    if (!messageType.IsGenericParameter)
                    {
                        handlerReferences.Add(new HandlerReference
                        {
                            HandlerType = type,
                            MessageType = messageType,
                            RoutingKeys = []
                        });
                    }
                }
            }
        }
        return handlerReferences;
    }
}
