using System.Reflection;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

public static class HandlerScanner
{
    public static IList<HandlerReference> ScanForHandlers(IEnumerable<Assembly> assemblies)
    {
        var handlerReferences = new List<HandlerReference>();
        var handlerInterfaceType = typeof(IMessageHandler<>);

        foreach (var assembly in assemblies)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface) continue;

                var handlerInterfaces = type.GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == handlerInterfaceType);

                foreach (var handlerInterface in handlerInterfaces)
                {
                    var messageType = handlerInterface.GetGenericArguments()[0];
                    handlerReferences.Add(new HandlerReference
                    {
                        HandlerType = type,
                        MessageType = messageType,
                        RoutingKeys = new List<string>()
                    });
                }
            }
        }
        return handlerReferences;
    }
}
