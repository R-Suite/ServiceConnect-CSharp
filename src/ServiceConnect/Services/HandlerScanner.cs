using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

/// <summary>
/// Discovers message, process, stream, and aggregator handlers from a set of assemblies.
/// </summary>
public static class HandlerScanner
{
    /// <summary>
    /// Scans the supplied assemblies and returns handler registrations keyed by handled message type.
    /// </summary>
    /// <param name="assemblies">The assemblies to inspect.</param>
    /// <param name="logger">Optional logger; when supplied, partial-scan warnings from
    /// <see cref="ReflectionTypeLoadException"/> are reported with assembly name and loader exceptions.
    /// Pass <see cref="NullLogger.Instance"/> or omit to preserve previous silent behaviour.</param>
    /// <returns>A list of discovered handler references.</returns>
    public static IList<HandlerReference> ScanForHandlers(IEnumerable<Assembly> assemblies, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        var handlerReferences = new List<HandlerReference>();
        var messageHandlerType = typeof(IMessageHandler<>);
        var processHandlerType = typeof(IProcessHandler<,>);
        var streamHandlerType = typeof(IStreamHandler<>);
        var aggregatorType = typeof(Aggregator<>);

        foreach (var assembly in assemblies)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                // Partial scan — loaded types are still usable, but this is a strong signal that
                // the consumer's intent doesn't match what's on disk (missing reference, version
                // drift, etc.). Surfacing a Warning here lets a misconfigured deploy fail loudly
                // instead of dropping handlers silently until a message arrives with no handler.
                types = ex.Types.Where(t => t != null).ToArray()!;
                logger.LogWarning(
                    ex,
                    "Assembly {AssemblyName} threw ReflectionTypeLoadException during handler scan; continuing with partial type list ({LoadedCount}/{RequestedCount}). Loader exceptions: {LoaderExceptionMessages}",
                    assembly.FullName ?? "<unknown>",
                    types.Length,
                    ex.Types.Length,
                    string.Join(" | ", (ex.LoaderExceptions ?? [])
                        .Where(e => e is not null).Select(e => e!.Message)));
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition)
                {
                    continue;
                }

                // Scan IMessageHandler<T>
                foreach (var iface in type.GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == messageHandlerType))
                {
                    var messageType = iface.GetGenericArguments()[0];
                    if (messageType.IsGenericParameter)
                    {
                        continue;
                    }

                    handlerReferences.Add(new HandlerReference
                    {
                        HandlerType = type,
                        MessageType = messageType
                    });
                }

                // Scan IProcessHandler<TData, TMessage> — message type is the last generic arg
                foreach (var iface in type.GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == processHandlerType))
                {
                    var messageType = iface.GetGenericArguments()[1];
                    if (messageType.IsGenericParameter)
                    {
                        continue;
                    }

                    handlerReferences.Add(new HandlerReference
                    {
                        HandlerType = type,
                        MessageType = messageType
                    });
                }

                // Scan IStreamHandler<T>
                foreach (var iface in type.GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == streamHandlerType))
                {
                    var messageType = iface.GetGenericArguments()[0];
                    if (messageType.IsGenericParameter)
                    {
                        continue;
                    }

                    handlerReferences.Add(new HandlerReference
                    {
                        HandlerType = type,
                        MessageType = messageType
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
                            MessageType = messageType
                        });
                    }
                }
            }
        }
        return handlerReferences;
    }
}
