using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

/// <summary>
/// Discovers message, process, stream, and aggregator handlers from a set of assemblies.
/// </summary>
internal static class HandlerScanner
{
    /// <summary>
    /// Scans the supplied assemblies and returns handler registrations keyed by handled message type.
    /// </summary>
    /// <param name="assemblies">The assemblies to inspect.</param>
    /// <param name="logger">Optional logger; when supplied, partial-scan warnings from
    /// <see cref="ReflectionTypeLoadException"/> are reported with assembly name and loader exceptions.
    /// When called from DI registration (where no logger is yet available), pass
    /// <see cref="NullLogger.Instance"/> and capture warnings via the
    /// <see cref="ScanForHandlers(IEnumerable{Assembly}, out IReadOnlyList{HandlerScanWarning})"/>
    /// overload so they can be replayed against the configured logger at startup.</param>
    /// <returns>A list of discovered handler references.</returns>
    public static IReadOnlyList<HandlerReference> ScanForHandlers(IEnumerable<Assembly> assemblies, ILogger? logger = null)
        => ScanForHandlersCore(assemblies, logger ?? NullLogger.Instance, warnings: null);

    /// <summary>
    /// Like the logger-only overload, but additionally captures partial-scan warnings into
    /// <paramref name="warnings"/> so a caller (typically the DI registration path that has
    /// no logger yet) can replay them against the configured logger later.
    /// </summary>
    public static IReadOnlyList<HandlerReference> ScanForHandlers(IEnumerable<Assembly> assemblies, out IReadOnlyList<HandlerScanWarning> warnings)
    {
        var collected = new List<HandlerScanWarning>();
        var result = ScanForHandlersCore(assemblies, NullLogger.Instance, collected);
        warnings = collected;
        return result;
    }

    private static IReadOnlyList<HandlerReference> ScanForHandlersCore(IEnumerable<Assembly> assemblies, ILogger logger, List<HandlerScanWarning>? warnings)
    {
        var handlerReferences = new List<HandlerReference>();
        var messageHandlerType = typeof(IMessageHandler<>);
        var processHandlerType = typeof(IProcessHandler<,>);
        var streamHandlerType = typeof(IStreamHandler<>);

        foreach (var assembly in assemblies)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                // Partial scan — loaded types are still usable. Warn so a misconfigured deploy
                // doesn't silently drop handlers until a message arrives with no handler.
                types = ex.Types.Where(t => t != null).ToArray()!;
                var loaderExceptionMessages = string.Join(" | ", (ex.LoaderExceptions ?? [])
                    .Where(e => e is not null).Select(e => e!.Message));
                logger.LogWarning(
                    ex,
                    "Assembly {AssemblyName} threw ReflectionTypeLoadException during handler scan; continuing with partial type list ({LoadedCount}/{RequestedCount}). Loader exceptions: {LoaderExceptionMessages}",
                    assembly.FullName ?? "<unknown>",
                    types.Length,
                    ex.Types.Length,
                    loaderExceptionMessages);
                warnings?.Add(new HandlerScanWarning(
                    assembly.FullName ?? "<unknown>",
                    nameof(ReflectionTypeLoadException),
                    $"Partial type list ({types.Length}/{ex.Types.Length}). Loader exceptions: {loaderExceptionMessages}",
                    ex));
            }
            catch (Exception ex) when (ex is FileNotFoundException
                                       or FileLoadException
                                       or BadImageFormatException
                                       or TypeLoadException)
            {
                // GetTypes() can throw any of these for assemblies in the AppDomain that aren't
                // properly resolvable: missing reference, version drift, mismatched native bitness,
                // or a type whose dependent assembly is broken. Catch them all here so one broken
                // assembly doesn't abort the entire scan and leave the host running with no
                // handlers registered. Skip the offending assembly with a warning instead.
                logger.LogWarning(
                    ex,
                    "Assembly {AssemblyName} threw {ExceptionType} during handler scan; skipping assembly.",
                    assembly.FullName ?? "<unknown>",
                    ex.GetType().Name);
                warnings?.Add(new HandlerScanWarning(
                    assembly.FullName ?? "<unknown>",
                    ex.GetType().Name,
                    "Assembly skipped during handler scan.",
                    ex));
                types = [];
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
                        MessageType = messageType,
                        InterfaceKind = HandlerInterfaceKind.MessageHandler,
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
                        MessageType = messageType,
                        InterfaceKind = HandlerInterfaceKind.ProcessHandler,
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
                        MessageType = messageType,
                        InterfaceKind = HandlerInterfaceKind.StreamHandler,
                    });
                }

                // Scan Aggregator<T> subclasses (full hierarchy walk so two+ level
                // inheritance chains are discovered).
                var aggregatorBase = FindAggregatorBaseType(type);
                if (aggregatorBase is not null)
                {
                    var messageType = aggregatorBase.GetGenericArguments()[0];
                    if (!messageType.IsGenericParameter)
                    {
                        handlerReferences.Add(new HandlerReference
                        {
                            HandlerType = type,
                            MessageType = messageType,
                            InterfaceKind = HandlerInterfaceKind.Aggregator,
                        });
                    }
                }
            }
        }
        return handlerReferences;
    }

    /// <summary>
    /// Walks the full base-type hierarchy of <paramref name="type"/> and returns the first
    /// closed <c>Aggregator&lt;T&gt;</c> it finds, or <see langword="null"/> if the type does
    /// not descend from <c>Aggregator&lt;T&gt;</c>. Handles chains of any depth.
    /// </summary>
    internal static Type? FindAggregatorBaseType(Type type)
    {
        var current = type.BaseType;
        while (current is not null && current != typeof(object))
        {
            if (current.IsGenericType &&
                current.GetGenericTypeDefinition() == typeof(Aggregator<>))
            {
                return current;
            }
            current = current.BaseType;
        }
        return null;
    }
}
