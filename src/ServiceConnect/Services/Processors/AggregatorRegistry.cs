using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed class AggregatorRegistry
{
    private readonly Dictionary<Type, (AggregatorDescriptor Descriptor, Type HandlerType)> _descriptors = new();

    internal AggregatorRegistry(
        IList<HandlerReference> handlerReferences,
        IServiceProvider serviceProvider,
        ILogger<AggregatorRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(handlerReferences);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(logger);

        foreach (var href in handlerReferences)
        {
            var aggregatorBaseType = FindAggregatorBaseType(href.HandlerType, href.MessageType);
            if (aggregatorBaseType == null)
                continue;

            if (_descriptors.TryGetValue(href.MessageType, out var existing))
            {
                if (existing.HandlerType == href.HandlerType)
                    continue; // identical (MessageType, HandlerType) pair registered twice — dedupe silently
                throw new InvalidOperationException(
                    $"Duplicate aggregator registration for message type '{href.MessageType.FullName}'. " +
                    $"Only one Aggregator<T> may be registered per message type; found '{existing.HandlerType.FullName}' and '{href.HandlerType.FullName}'.");
            }

            var descriptor = BuildDescriptor(href.MessageType, aggregatorBaseType, serviceProvider);
            _descriptors[href.MessageType] = (descriptor, href.HandlerType);

            logger.LogDebug(
                "Registered aggregator descriptor: message={MessageType}, aggregator={AggregatorType}, batchSize={BatchSize}, timeout={Timeout}",
                href.MessageType.Name, href.HandlerType.Name, descriptor.BatchSize, descriptor.Timeout);
        }
    }

    internal bool TryGet(Type messageType, [NotNullWhen(true)] out AggregatorDescriptor? descriptor)
    {
        if (_descriptors.TryGetValue(messageType, out var entry))
        {
            descriptor = entry.Descriptor;
            return true;
        }
        descriptor = null;
        return false;
    }

    private static Type? FindAggregatorBaseType(Type handlerType, Type messageType)
    {
        if (handlerType.BaseType is not { IsGenericType: true } baseType)
            return null;
        if (baseType.GetGenericTypeDefinition() != typeof(Aggregator<>))
            return null;
        if (baseType.GetGenericArguments()[0] != messageType)
            return null;
        return baseType;
    }

    private static AggregatorDescriptor BuildDescriptor(Type messageType, Type aggregatorBaseType, IServiceProvider sp)
    {
        object aggregator;
        try
        {
            aggregator = sp.GetRequiredService(aggregatorBaseType);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"AggregatorRegistry could not materialize aggregator for message type '{messageType.FullName}'. Ensure the aggregator is registered in DI.", ex);
        }

        // One-time reflection at startup is acceptable — reading BatchSize/Timeout from an instance
        // whose values are treated as configuration constants for the aggregator class.
        var batchSize = (int)aggregatorBaseType.GetMethod(nameof(Aggregator<Message>.BatchSize))!.Invoke(aggregator, null)!;
        var timeout = (TimeSpan)aggregatorBaseType.GetMethod(nameof(Aggregator<Message>.Timeout))!.Invoke(aggregator, null)!;

        return new AggregatorDescriptor(
            MessageType: messageType,
            AggregatorBaseType: aggregatorBaseType,
            AggregatorName: aggregatorBaseType.FullName!,
            BatchSize: batchSize,
            Timeout: timeout,
            BuildTypedList: CompileBuildTypedList(messageType),
            InvokeExecute: CompileInvokeExecute(aggregatorBaseType, messageType));
    }

    private static Func<IList<object>, IList> CompileBuildTypedList(Type messageType)
    {
        // Build: (IList<object> raw) => { var list = new List<TMsg>(); foreach (var m in raw) list.Add((TMsg)m); return list; }
        var listType = typeof(List<>).MakeGenericType(messageType);

        var rawParam = Expression.Parameter(typeof(IList<object>), "raw");
        var listVar = Expression.Variable(listType, "list");
        var enumeratorVar = Expression.Variable(typeof(IEnumerator<object>), "enumerator");

        var listCtor = listType.GetConstructor(Type.EmptyTypes)!;
        var addMethod = listType.GetMethod("Add")!;
        var getEnumeratorMethod = typeof(IEnumerable<object>).GetMethod("GetEnumerator")!;
        var moveNextMethod = typeof(System.Collections.IEnumerator).GetMethod("MoveNext")!;
        var currentProp = typeof(IEnumerator<object>).GetProperty("Current")!;

        var breakLabel = Expression.Label("break");

        var block = Expression.Block(
            typeof(IList),
            new[] { listVar, enumeratorVar },
            Expression.Assign(listVar, Expression.New(listCtor)),
            Expression.Assign(enumeratorVar, Expression.Call(rawParam, getEnumeratorMethod)),
            Expression.Loop(
                Expression.IfThenElse(
                    Expression.Call(enumeratorVar, moveNextMethod),
                    Expression.Call(listVar, addMethod, Expression.Convert(Expression.Property(enumeratorVar, currentProp), messageType)),
                    Expression.Break(breakLabel)),
                breakLabel),
            Expression.Convert(listVar, typeof(IList)));

        return Expression.Lambda<Func<IList<object>, IList>>(block, rawParam).Compile();
    }

    private static Action<object, object> CompileInvokeExecute(Type aggregatorBaseType, Type messageType)
    {
        var aggParam = Expression.Parameter(typeof(object), "aggregator");
        var listParam = Expression.Parameter(typeof(object), "list");

        var aggCast = Expression.Convert(aggParam, aggregatorBaseType);
        var listCast = Expression.Convert(listParam, typeof(IList<>).MakeGenericType(messageType));

        var method = aggregatorBaseType.GetMethod(nameof(Aggregator<Message>.Execute))!;
        var call = Expression.Call(aggCast, method, listCast);

        return Expression.Lambda<Action<object, object>>(call, aggParam, listParam).Compile();
    }
}
