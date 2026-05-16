using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed class AggregatorRegistry : IHandlerRegistry
{
    // Built once at construction; FrozenDictionary for read-heavy lookup.
    private readonly FrozenDictionary<Type, AggregatorDescriptor> _descriptors;

    internal AggregatorRegistry(
        IReadOnlyList<HandlerReference> handlerReferences,
        IServiceScopeFactory scopeFactory,
        ILogger<AggregatorRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(handlerReferences);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        var builder = new Dictionary<Type, (AggregatorDescriptor Descriptor, Type HandlerType)>();

        // Single short-lived scope: aggregator instances are only consulted for BatchSize/Timeout
        // (configuration constants). Disposing the scope before the constructor returns prevents
        // captive scoped dependencies and disposable aggregators being tracked by the root provider.
        using var scope = scopeFactory.CreateScope();
        foreach (var href in handlerReferences)
        {
            var aggregatorBaseType = FindAggregatorBaseType(href.HandlerType, href.MessageType);
            if (aggregatorBaseType == null)
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
                    $"Duplicate aggregator registration for message type '{href.MessageType.FullName}'. " +
                    $"Only one Aggregator<T> may be registered per message type; found '{existing.HandlerType.FullName}' and '{href.HandlerType.FullName}'.");
            }

            // Synchronous `using var scope = scopeFactory.CreateScope()` calls IServiceScope.Dispose,
            // which throws `InvalidOperationException("AsyncDisposableServiceNotSupported")` from MS.DI
            // for any tracked service that implements IAsyncDisposable only (not also IDisposable).
            // Check the concrete handler type before resolving so the guard fires before any scope
            // disposal interleaves with the exception path.
            if (typeof(IAsyncDisposable).IsAssignableFrom(href.HandlerType) &&
                !typeof(IDisposable).IsAssignableFrom(href.HandlerType))
            {
                throw new InvalidOperationException(
                    $"Aggregator '{aggregatorBaseType.FullName}' implements IAsyncDisposable but not IDisposable. " +
                    "AggregatorRegistry uses synchronous scope disposal at construction (the aggregator instance is " +
                    "only consulted for BatchSize/Timeout configuration), which is incompatible with IAsyncDisposable-only " +
                    "lifetimes. Either implement IDisposable alongside IAsyncDisposable, or refactor the aggregator's " +
                    "shutdown logic to avoid IAsyncDisposable.");
            }

            var descriptor = BuildDescriptor(href.MessageType, aggregatorBaseType, scope.ServiceProvider);
            builder[href.MessageType] = (descriptor, href.HandlerType);

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "Registered aggregator descriptor: message={MessageType}, aggregator={AggregatorType}, batchSize={BatchSize}, timeout={Timeout}",
                    href.MessageType.Name, href.HandlerType.Name, descriptor.BatchSize, descriptor.Timeout);
            }
        }

        _descriptors = builder.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Descriptor).ToFrozenDictionary();
    }

    internal bool TryGet(Type messageType, [NotNullWhen(true)] out AggregatorDescriptor? descriptor)
        => _descriptors.TryGetValue(messageType, out descriptor);

    private static Type? FindAggregatorBaseType(Type handlerType, Type messageType)
    {
        if (handlerType.BaseType is not { IsGenericType: true } baseType)
        {
            return null;
        }

        if (baseType.GetGenericTypeDefinition() != typeof(Aggregator<>))
        {
            return null;
        }

        if (baseType.GetGenericArguments()[0] != messageType)
        {
            return null;
        }

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

        // Both BatchSize and Timeout must be positive. If BatchSize > 0 but Timeout is zero,
        // the processor never schedules a timer; when the count stays below BatchSize the
        // buffered tail is never flushed. If BatchSize is 0 or negative there is no count-based
        // flush trigger either. Requiring both guarantees at least one flush path is always active.
        if (batchSize <= 0 || timeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Aggregator '{aggregatorBaseType.FullName}' has BatchSize={batchSize} and Timeout={timeout}. " +
                "Both BatchSize (>0) and Timeout (>TimeSpan.Zero) must be configured; without both, " +
                "messages can be buffered with no flush path to deliver them.");
        }

        return new AggregatorDescriptor(
            MessageType: messageType,
            AggregatorBaseType: aggregatorBaseType,
            AggregatorName: aggregatorBaseType.FullName!,
            BatchSize: batchSize,
            Timeout: timeout,
            BuildTypedList: CompileBuildTypedList(messageType),
            InvokeExecuteAsync: CompileInvokeExecuteAsync(aggregatorBaseType, messageType));
    }

    private static Func<IList<object>, object> CompileBuildTypedList(Type messageType)
    {
        // Build: (IList<object> raw) => { var list = new List<TMsg>(); for (var i = 0; i < raw.Count; i++) list.Add((TMsg)raw[i]); return (IReadOnlyList<TMsg>)list; }
        // The block's return type is IReadOnlyList<TMsg> so the descriptor's contract is satisfied
        // at expression-tree construction time: only a value that IS an IReadOnlyList<TMsg> can
        // be returned. The lambda is stored as Func<IList<object>, object>; the box is a no-op
        // reference cast since IReadOnlyList<TMsg> is a reference type.
        var readOnlyListType = typeof(IReadOnlyList<>).MakeGenericType(messageType);
        var listType = typeof(List<>).MakeGenericType(messageType);

        var rawParam = Expression.Parameter(typeof(IList<object>), "raw");
        var listVar = Expression.Variable(listType, "list");
        var indexVar = Expression.Variable(typeof(int), "index");

        var listCtor = listType.GetConstructor(Type.EmptyTypes)!;
        var addMethod = listType.GetMethod("Add")!;
        var countProp = typeof(ICollection<object>).GetProperty(nameof(ICollection<object>.Count))!;
        var indexerProp = typeof(IList<object>).GetProperty("Item")!;

        var breakLabel = Expression.Label("break");

        var block = Expression.Block(
            readOnlyListType,
            [listVar, indexVar],
            Expression.Assign(listVar, Expression.New(listCtor)),
            Expression.Assign(indexVar, Expression.Constant(0)),
            Expression.Loop(
                Expression.IfThenElse(
                    Expression.LessThan(indexVar, Expression.Property(rawParam, countProp)),
                    Expression.Block(
                        Expression.Call(listVar, addMethod, Expression.Convert(Expression.Property(rawParam, indexerProp, indexVar), messageType)),
                        Expression.PostIncrementAssign(indexVar)),
                    Expression.Break(breakLabel)),
                breakLabel),
            Expression.Convert(listVar, readOnlyListType));

        return Expression.Lambda<Func<IList<object>, object>>(block, rawParam).Compile();
    }

    private static Func<object, object, CancellationToken, Task> CompileInvokeExecuteAsync(Type aggregatorBaseType, Type messageType)
    {
        var aggParam = Expression.Parameter(typeof(object), "aggregator");
        var listParam = Expression.Parameter(typeof(object), "list");
        var ctParam = Expression.Parameter(typeof(CancellationToken), "cancellationToken");

        var aggCast = Expression.Convert(aggParam, aggregatorBaseType);
        var listCast = Expression.Convert(listParam, typeof(IReadOnlyList<>).MakeGenericType(messageType));

        var method = aggregatorBaseType.GetMethod(nameof(Aggregator<Message>.ExecuteAsync))!;
        var call = Expression.Call(aggCast, method, listCast, ctParam);

        return Expression.Lambda<Func<object, object, CancellationToken, Task>>(call, aggParam, listParam, ctParam).Compile();
    }
}
