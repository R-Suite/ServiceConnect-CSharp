using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed class ProcessManagerProcessor(
    ProcessManagerHandlerRegistry registry,
    IServiceProvider serviceProvider,
    ILogger<ProcessManagerProcessor> logger) : IMessageProcessor
{
    public async Task<ProcessResult> ProcessAsync(
        byte[] messageBytes, Type messageType, object? message,
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

        var mapper = new DefaultProcessManagerPropertyMapper();
        descriptor.ConfigureMapper(handler, mapper);

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

        var bus = serviceProvider.GetRequiredService<IBus>();
        descriptor.SetHandlerContext(
            handler,
            new ConsumeContext(bus, headers) { CancellationToken = cancellationToken });

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
