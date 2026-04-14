using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Services.Processors;

internal sealed class ProcessManagerProcessor(
    ProcessManagerHandlerRegistry registry,
    IServiceProvider serviceProvider,
    Lazy<IBus> bus,
    ILogger<ProcessManagerProcessor> logger,
    IBusConfiguration busConfig,
    IQueueConfiguration queueConfig) : IMessageProcessor
{
    // Cached mapper per handler interface type. ConfigureMapper compiles expression lambdas
    // that are identical for a given handler type, so we only pay the cost once (P-005/R-034).
    private static readonly ConcurrentDictionary<Type, IProcessManagerPropertyMapper> MapperCache = new();

    public async Task<ProcessResult> ProcessAsync(
        ReadOnlyMemory<byte> messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (message == null) return ProcessResult.NotHandled;

        if (!registry.TryGet(messageType, out var descriptor))
            return ProcessResult.NotHandled;

        var finder = serviceProvider.GetService<IProcessManagerFinder>();
        if (finder == null)
        {
            logger.LogWarning(
                "IProcessManagerFinder not registered; cannot process process-manager message {MessageType}",
                messageType.Name);
            return ProcessResult.NotHandled;
        }

        var handler = serviceProvider.GetService(descriptor.ProcessHandlerInterfaceType);
        if (handler == null)
        {
            logger.LogWarning(
                "Process-manager handler not registered in DI for interface {HandlerInterface}; cannot process message {MessageType}",
                descriptor.ProcessHandlerInterfaceType.Name, messageType.Name);
            return ProcessResult.NotHandled;
        }

        var mapper = MapperCache.GetOrAdd(descriptor.ProcessHandlerInterfaceType, _ =>
        {
            var m = new DefaultProcessManagerPropertyMapper();
            descriptor.ConfigureMapper(handler, m);
            return m;
        });

        var persistenceData = await descriptor.FindData(finder, mapper, (Message)message, cancellationToken).ConfigureAwait(false);

        bool isNew = persistenceData == null;
        object data;
        if (isNew)
        {
            var newData = descriptor.CreateData();
            descriptor.SetCorrelationId(newData, ((Message)message).CorrelationId);
            data = newData;
        }
        else
        {
            data = descriptor.ExtractData(persistenceData!);
        }

        descriptor.SetHandlerContext(
            handler,
            new ConsumeContext(bus.Value, headers, queueConfig, busConfig) { CancellationToken = cancellationToken });

        try
        {
            await descriptor.InvokeHandleAsync(handler, (Message)message, data).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Process-manager handler threw for {MessageType}; persistence skipped", messageType.Name);
            throw;
        }

        // Only persist if the handler succeeded — keeps business side-effects and persistence atomic.
        if (isNew)
        {
            await finder.InsertDataAsync((IProcessManagerData)data, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await descriptor.UpdateData(finder, persistenceData!, cancellationToken).ConfigureAwait(false);
        }

        return ProcessResult.Handled;
    }
}
