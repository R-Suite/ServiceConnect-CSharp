using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

public class ProcessManagerProcessor : IMessageProcessor
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ProcessManagerProcessor> _logger;

    public ProcessManagerProcessor(IServiceProvider serviceProvider, ILogger<ProcessManagerProcessor> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<ProcessResult> ProcessAsync(
        byte[] messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope)
    {
        if (message == null) return ProcessResult.NotHandled;

        // 1. Find a HandlerReference whose HandlerType implements IProcessHandler<,> for this message type
        var handlerRefs = _serviceProvider.GetService<IList<HandlerReference>>();
        if (handlerRefs == null || handlerRefs.Count == 0)
            return ProcessResult.NotHandled;

        Type? processHandlerInterfaceType = null;
        Type? dataType = null;

        foreach (var href in handlerRefs)
        {
            var iface = href.HandlerType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType
                    && i.GetGenericTypeDefinition() == typeof(IProcessHandler<,>)
                    && i.GetGenericArguments()[1] == messageType);

            if (iface != null)
            {
                processHandlerInterfaceType = iface;
                dataType = iface.GetGenericArguments()[0];
                break;
            }
        }

        if (processHandlerInterfaceType == null || dataType == null)
            return ProcessResult.NotHandled;

        // 3. Resolve IProcessManagerFinder
        var finder = _serviceProvider.GetService<IProcessManagerFinder>();
        if (finder == null)
        {
            _logger.LogWarning("IProcessManagerFinder not registered; cannot process process-manager message {MessageType}", messageType.Name);
            return ProcessResult.NotHandled;
        }

        // 4. Resolve the handler
        var handler = _serviceProvider.GetService(processHandlerInterfaceType);
        if (handler == null)
            return ProcessResult.NotHandled;

        // 5. Configure mapper
        var mapper = new DefaultProcessManagerPropertyMapper();
        var configureMapperMethod = processHandlerInterfaceType.GetMethod("ConfigureMapper");
        configureMapperMethod?.Invoke(handler, new object[] { mapper });

        // 6. FindData<TData>(mapper, message)
        var findDataMethod = typeof(IProcessManagerFinder).GetMethod("FindData")!.MakeGenericMethod(dataType);
        var persistenceData = findDataMethod.Invoke(finder, new object[] { mapper, message });

        // 7. If null, create new TData and set CorrelationId
        bool isNew = persistenceData == null;
        object data;

        if (isNew)
        {
            data = Activator.CreateInstance(dataType)!;
            var correlationIdProp = dataType.GetProperty("CorrelationId");
            var msgCorrelationId = ((Message)message).CorrelationId;
            correlationIdProp?.SetValue(data, msgCorrelationId);
        }
        else
        {
            // Get Data property from IPersistenceData<T>
            var dataProp = persistenceData!.GetType().GetProperty("Data");
            data = dataProp!.GetValue(persistenceData)!;
        }

        // 8. Set Context, invoke HandleAsync
        var bus = _serviceProvider.GetRequiredService<IBus>();
        var context = new ConsumeContext(bus, headers);

        var contextProp = processHandlerInterfaceType.GetProperty("Context");
        contextProp?.SetValue(handler, context);

        var handleAsyncMethod = processHandlerInterfaceType.GetMethod("HandleAsync");
        var task = (Task?)handleAsyncMethod?.Invoke(handler, new[] { message, data });
        if (task != null)
            await task;

        // 9. Insert or Update
        if (isNew)
        {
            finder.InsertData((IProcessManagerData)data);
        }
        else
        {
            var updateMethod = typeof(IProcessManagerFinder).GetMethod("UpdateData")!.MakeGenericMethod(dataType);
            updateMethod.Invoke(finder, new[] { persistenceData });
        }

        return ProcessResult.Handled;
    }
}

file class DefaultProcessManagerPropertyMapper : IProcessManagerPropertyMapper
{
    public List<ProcessManagerToMessageMap> Mappings { get; set; } = new();

    public void ConfigureMapping<TProcessManagerData, TMessage>(
        System.Linq.Expressions.Expression<Func<TProcessManagerData, object>> processManagerProperty,
        System.Linq.Expressions.Expression<Func<TMessage, object>> messageExpression)
        where TProcessManagerData : IProcessManagerData
    {
        var map = new ProcessManagerToMessageMap
        {
            MessageType = typeof(TMessage),
            PropertiesHierarchy = new Dictionary<string, Type>(),
            MessageProp = BuildMessageFunc(messageExpression)
        };

        var body = processManagerProperty.Body;
        if (body is System.Linq.Expressions.UnaryExpression unary) body = unary.Operand;
        if (body is System.Linq.Expressions.MemberExpression member)
        {
            var propInfo = (PropertyInfo)member.Member;
            map.PropertiesHierarchy[propInfo.Name] = propInfo.PropertyType;
        }

        Mappings.Add(map);
    }

    private static Func<object, object> BuildMessageFunc<TMessage>(
        System.Linq.Expressions.Expression<Func<TMessage, object>> messageExpression)
    {
        var compiled = messageExpression.Compile();
        return obj => compiled((TMessage)obj);
    }
}
