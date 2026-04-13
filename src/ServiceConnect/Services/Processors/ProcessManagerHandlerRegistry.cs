using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

public sealed class ProcessManagerHandlerRegistry
{
    private readonly Dictionary<Type, ProcessManagerDescriptor> _descriptors = new();

    public ProcessManagerHandlerRegistry(
        IList<HandlerReference> handlerReferences,
        ILogger<ProcessManagerHandlerRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(handlerReferences);
        ArgumentNullException.ThrowIfNull(logger);

        foreach (var href in handlerReferences)
        {
            var processHandlerInterface = href.HandlerType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType
                    && i.GetGenericTypeDefinition() == typeof(IProcessHandler<,>)
                    && i.GetGenericArguments()[1] == href.MessageType);

            if (processHandlerInterface == null)
                continue;

            if (_descriptors.ContainsKey(href.MessageType))
            {
                throw new InvalidOperationException(
                    $"Duplicate process-manager handler registration for message type '{href.MessageType.FullName}'. Only one IProcessHandler<TData,TMessage> may be registered per message type.");
            }

            var dataType = processHandlerInterface.GetGenericArguments()[0];
            _descriptors[href.MessageType] = BuildDescriptor(href.MessageType, dataType, processHandlerInterface);

            logger.LogDebug(
                "Registered process-manager descriptor: message={MessageType}, data={DataType}, handler={HandlerType}",
                href.MessageType.Name, dataType.Name, href.HandlerType.Name);
        }
    }

    public bool TryGet(Type messageType, out ProcessManagerDescriptor? descriptor)
        => _descriptors.TryGetValue(messageType, out descriptor);

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
            GetPersistenceDataData: CompileGetPersistenceDataData(persistenceInterfaceType),
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
        => await task.ConfigureAwait(false);

    private static Func<object, object> CompileGetPersistenceDataData(Type persistenceInterfaceType)
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

    private static Func<object, Message, object, Task> CompileInvokeHandleAsync(
        Type handlerInterface, Type messageType, Type dataType)
    {
        var handlerParam = Expression.Parameter(typeof(object), "handler");
        var messageParam = Expression.Parameter(typeof(Message), "message");
        var dataParam = Expression.Parameter(typeof(object), "data");

        var handlerCast = Expression.Convert(handlerParam, handlerInterface);
        var messageCast = Expression.Convert(messageParam, messageType);
        var dataCast = Expression.Convert(dataParam, dataType);

        var method = handlerInterface.GetMethod("HandleAsync")!;
        var call = Expression.Call(handlerCast, method, messageCast, dataCast);

        return Expression.Lambda<Func<object, Message, object, Task>>(
            call, handlerParam, messageParam, dataParam).Compile();
    }
}
