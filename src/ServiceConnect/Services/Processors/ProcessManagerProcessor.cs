using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

public sealed class ProcessManagerProcessor(IServiceProvider serviceProvider, ILogger<ProcessManagerProcessor> logger) : IMessageProcessor
{
    public async Task<ProcessResult> ProcessAsync(
        byte[] messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope)
    {
        if (message == null) return ProcessResult.NotHandled;

        // 1. Find a HandlerReference whose HandlerType implements IProcessHandler<,> for this message type
        var handlerRefs = serviceProvider.GetService<IList<HandlerReference>>();
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
        var finder = serviceProvider.GetService<IProcessManagerFinder>();
        if (finder == null)
        {
            logger.LogWarning("IProcessManagerFinder not registered; cannot process process-manager message {MessageType}", messageType.Name);
            return ProcessResult.NotHandled;
        }

        // 4. Resolve the handler
        var handler = serviceProvider.GetService(processHandlerInterfaceType);
        if (handler == null)
            return ProcessResult.NotHandled;

        // 5. Configure mapper
        var mapper = new DefaultProcessManagerPropertyMapper();
        var configureMapperMethod = processHandlerInterfaceType.GetMethod("ConfigureMapper");
        configureMapperMethod?.Invoke(handler, [mapper]);

        // 6. FindData<TData>(mapper, message)
        var findDataMethod = typeof(IProcessManagerFinder).GetMethod("FindData")!.MakeGenericMethod(dataType);
        var persistenceData = findDataMethod.Invoke(finder, [mapper, message]);

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
            data = dataProp!.GetValue(persistenceData)
                ?? throw new InvalidOperationException($"Persisted data for type '{dataType.Name}' has null Data property.");
        }

        // 8. Set Context, invoke HandleAsync
        var bus = serviceProvider.GetRequiredService<IBus>();
        var context = new ConsumeContext(bus, headers);

        var contextProp = processHandlerInterfaceType.GetProperty("Context");
        contextProp?.SetValue(handler, context);

        var handleAsyncMethod = processHandlerInterfaceType.GetMethod("HandleAsync");
        var task = (Task?)handleAsyncMethod?.Invoke(handler, [message, data]);
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
            updateMethod.Invoke(finder, [persistenceData]);
        }

        return ProcessResult.Handled;
    }
}
