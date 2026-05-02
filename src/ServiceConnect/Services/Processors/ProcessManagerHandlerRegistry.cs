using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed class ProcessManagerHandlerRegistry : IHandlerRegistry, IProcessManagerTypeRegistry
{
    // Built once at construction, never written to afterwards. FrozenDictionary gives
    // ~20–40% faster lookups than Dictionary for the per-message hot path.
    private readonly FrozenDictionary<Type, ProcessManagerDescriptor> _descriptors;

    // Snapshot of distinct saga data types built at construction. Exposed via
    // IProcessManagerTypeRegistry so persistence providers can pre-create per-saga
    // structures (e.g. Mongo unique CorrelationId indexes) at startup.
    private readonly IReadOnlyList<Type> _sagaDataTypes;

    internal ProcessManagerHandlerRegistry(
        IList<HandlerReference> handlerReferences,
        ILogger<ProcessManagerHandlerRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(handlerReferences);
        ArgumentNullException.ThrowIfNull(logger);

        var builder = new Dictionary<Type, ProcessManagerDescriptor>();
        foreach (var href in handlerReferences)
        {
            Type? processHandlerInterface = null;
            foreach (var interfaceType in href.HandlerType.GetInterfaces())
            {
                if (interfaceType.IsGenericType
                    && interfaceType.GetGenericTypeDefinition() == typeof(IProcessHandler<,>)
                    && interfaceType.GetGenericArguments()[1] == href.MessageType)
                {
                    processHandlerInterface = interfaceType;
                    break;
                }
            }

            if (processHandlerInterface == null)
            {
                continue;
            }

            var dataType = processHandlerInterface.GetGenericArguments()[0];
            var descriptor = BuildDescriptor(href.MessageType, dataType, processHandlerInterface);
            if (!builder.TryAdd(href.MessageType, descriptor))
            {
                throw new InvalidOperationException(
                    $"Duplicate process-manager handler registration for message type '{href.MessageType.FullName}'. Only one IProcessHandler<TData,TMessage> may be registered per message type.");
            }

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "Registered process-manager descriptor: message={MessageType}, data={DataType}, handler={HandlerType}",
                    href.MessageType.Name, dataType.Name, href.HandlerType.Name);
            }
        }

        _descriptors = builder.ToFrozenDictionary();
        _sagaDataTypes = [.. _descriptors.Values.Select(d => d.DataType).Distinct()];
    }

    internal bool TryGet(Type messageType, [NotNullWhen(true)] out ProcessManagerDescriptor? descriptor)
        => _descriptors.TryGetValue(messageType, out descriptor);

    /// <inheritdoc />
    public IEnumerable<Type> SagaDataTypes => _sagaDataTypes;

    internal static ProcessManagerDescriptor BuildDescriptor(
        Type messageType, Type dataType, Type processHandlerInterfaceType)
    {
        var persistenceInterfaceType = typeof(IPersistenceData<>).MakeGenericType(dataType);

        return new ProcessManagerDescriptor(
            MessageType: messageType,
            DataType: dataType,
            ProcessHandlerInterfaceType: processHandlerInterfaceType,
            CreateData: CompileCreateData(dataType),
            SetCorrelationId: CompileSetCorrelationId(),
            SetHandlerContext: CompileSetHandlerContext(processHandlerInterfaceType),
            ConfigureMapper: CompileConfigureMapper(processHandlerInterfaceType),
            FindData: CompileFindData(dataType),
            ExtractData: CompileExtractData(persistenceInterfaceType),
            UpdateData: CompileUpdateData(dataType, persistenceInterfaceType),
            InvokeHandleAsync: CompileInvokeHandleAsync(processHandlerInterfaceType, messageType, dataType));
    }

    private static Func<IProcessManagerData> CompileCreateData(Type dataType)
    {
        var newExpr = Expression.New(dataType);
        var cast = Expression.Convert(newExpr, typeof(IProcessManagerData));
        return Expression.Lambda<Func<IProcessManagerData>>(cast).Compile();
    }

    private static Action<IProcessManagerData, Guid> CompileSetCorrelationId()
    {
        var dataParam = Expression.Parameter(typeof(IProcessManagerData), "data");
        var guidParam = Expression.Parameter(typeof(Guid), "id");
        var prop = typeof(IProcessManagerData).GetProperty(nameof(IProcessManagerData.CorrelationId))!;
        var assign = Expression.Assign(Expression.Property(dataParam, prop), guidParam);
        return Expression.Lambda<Action<IProcessManagerData, Guid>>(assign, dataParam, guidParam).Compile();
    }

    private static Action<object, IConsumeContext> CompileSetHandlerContext(Type handlerInterface)
    {
        var handlerParam = Expression.Parameter(typeof(object), "handler");
        var ctxParam = Expression.Parameter(typeof(IConsumeContext), "ctx");
        var cast = Expression.Convert(handlerParam, handlerInterface);
        var prop = handlerInterface.GetProperty("Context")!;
        var assign = Expression.Assign(Expression.Property(cast, prop), ctxParam);
        return Expression.Lambda<Action<object, IConsumeContext>>(assign, handlerParam, ctxParam).Compile();
    }

    private static Action<object, IProcessManagerPropertyMapper> CompileConfigureMapper(Type handlerInterface)
    {
        var handlerParam = Expression.Parameter(typeof(object), "handler");
        var mapperParam = Expression.Parameter(typeof(IProcessManagerPropertyMapper), "mapper");
        var cast = Expression.Convert(handlerParam, handlerInterface);
        var method = handlerInterface.GetMethod("ConfigureMapper")!;
        var call = Expression.Call(cast, method, mapperParam);
        return Expression.Lambda<Action<object, IProcessManagerPropertyMapper>>(call, handlerParam, mapperParam).Compile();
    }

    private static Func<IProcessManagerFinder, IProcessManagerPropertyMapper, Message, CancellationToken, Task<object?>> CompileFindData(Type dataType)
    {
        var closedFindData = typeof(IProcessManagerFinder).GetMethod(nameof(IProcessManagerFinder.FindDataAsync))!
            .MakeGenericMethod(dataType);

        var finderParam = Expression.Parameter(typeof(IProcessManagerFinder), "finder");
        var mapperParam = Expression.Parameter(typeof(IProcessManagerPropertyMapper), "mapper");
        var messageParam = Expression.Parameter(typeof(Message), "message");
        var ctParam = Expression.Parameter(typeof(CancellationToken), "ct");

        var call = Expression.Call(finderParam, closedFindData, mapperParam, messageParam, ctParam);

        // The call returns Task<IPersistenceData<TData>?>. We bridge to Task<object?> via a generic helper
        // so the lambda's return type matches the delegate signature.
        var helper = typeof(ProcessManagerHandlerRegistry)
            .GetMethod(nameof(ToObjectTask), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(IPersistenceData<>).MakeGenericType(dataType));

        var wrapped = Expression.Call(helper, call);

        return Expression.Lambda<Func<IProcessManagerFinder, IProcessManagerPropertyMapper, Message, CancellationToken, Task<object?>>>(
            wrapped, finderParam, mapperParam, messageParam, ctParam).Compile();
    }

    private static async Task<object?> ToObjectTask<T>(Task<T?> task) where T : class
#pragma warning disable VSTHRD003 // Task is owned by the caller; this is a generic bridging helper.
        => await task.ConfigureAwait(false);
#pragma warning restore VSTHRD003

    private static Func<object, object> CompileExtractData(Type persistenceInterfaceType)
    {
        var persistenceParam = Expression.Parameter(typeof(object), "persistence");
        var cast = Expression.Convert(persistenceParam, persistenceInterfaceType);
        var dataProp = persistenceInterfaceType.GetProperty("Data")!;
        var access = Expression.Property(cast, dataProp);
        var toObject = Expression.Convert(access, typeof(object));
        return Expression.Lambda<Func<object, object>>(toObject, persistenceParam).Compile();
    }

    private static Func<IProcessManagerFinder, object, CancellationToken, Task> CompileUpdateData(
        Type dataType, Type persistenceInterfaceType)
    {
        var closedUpdate = typeof(IProcessManagerFinder).GetMethod(nameof(IProcessManagerFinder.UpdateDataAsync))!
            .MakeGenericMethod(dataType);

        var finderParam = Expression.Parameter(typeof(IProcessManagerFinder), "finder");
        var persistenceParam = Expression.Parameter(typeof(object), "persistence");
        var ctParam = Expression.Parameter(typeof(CancellationToken), "ct");

        var cast = Expression.Convert(persistenceParam, persistenceInterfaceType);
        var call = Expression.Call(finderParam, closedUpdate, cast, ctParam);

        return Expression.Lambda<Func<IProcessManagerFinder, object, CancellationToken, Task>>(
            call, finderParam, persistenceParam, ctParam).Compile();
    }

    private static Func<object, Message, object, CancellationToken, Task> CompileInvokeHandleAsync(
        Type handlerInterface, Type messageType, Type dataType)
    {
        var handlerParam = Expression.Parameter(typeof(object), "handler");
        var messageParam = Expression.Parameter(typeof(Message), "message");
        var dataParam = Expression.Parameter(typeof(object), "data");
        var ctParam = Expression.Parameter(typeof(CancellationToken), "ct");

        var handlerCast = Expression.Convert(handlerParam, handlerInterface);
        var messageCast = Expression.Convert(messageParam, messageType);
        var dataCast = Expression.Convert(dataParam, dataType);

        var method = handlerInterface.GetMethod(
            "HandleAsync",
            [messageType, dataType, typeof(CancellationToken)])!;
        var call = Expression.Call(handlerCast, method, messageCast, dataCast, ctParam);

        return Expression.Lambda<Func<object, Message, object, CancellationToken, Task>>(
            call, handlerParam, messageParam, dataParam, ctParam).Compile();
    }
}
