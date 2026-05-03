using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;

namespace ServiceConnect.Services.Processors;

internal sealed class ProcessManagerProcessor(
    ProcessManagerHandlerRegistry registry,
    ConsumeScopeAccessor scopeAccessor,
    Lazy<IBus> bus,
    ILogger<ProcessManagerProcessor> logger,
    IBusConfiguration busConfig,
    IQueueConfiguration queueConfig,
    ConsumeContextPool contextPool,
    ConsumeContextAccessor consumeContextAccessor,
    IReplyStatusRequestReplyManager? replyStatusRequestReplyManager = null) : IMessageProcessor
{
    private readonly ConsumeContextAccessor _consumeContextAccessor = consumeContextAccessor;
    private readonly ConsumeContextPool _contextPool = contextPool;

    public async Task<ProcessResult> ProcessAsync(
        ReadOnlyMemory<byte> messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (message == null)
        {
            return ProcessResult.NotHandled;
        }

        if (!registry.TryGet(messageType, out var descriptor))
        {
            return ProcessResult.NotHandled;
        }

        var scope = scopeAccessor.Current;

        var finder = scope.GetService<IProcessManagerFinder>();
        if (finder == null)
        {
            logger.LogWarning(
                "IProcessManagerFinder not registered; cannot process process-manager message {MessageType}",
                messageType.Name);
            return ProcessResult.NotHandled;
        }

        var handler = scope.GetService(descriptor.ProcessHandlerInterfaceType);
        if (handler == null)
        {
            logger.LogWarning(
                "Process-manager handler not registered in DI for interface {HandlerInterface}; cannot process message {MessageType}",
                descriptor.ProcessHandlerInterfaceType.Name, messageType.Name);
            return ProcessResult.NotHandled;
        }

        // ConfigureMapper may read per-instance handler state, so build the mapper against
        // the freshly-resolved handler each delivery rather than memoising one mapper.
        var mapper = new DefaultProcessManagerPropertyMapper();
        descriptor.ConfigureMapper(handler, mapper);

        // Run the find→invoke→update cycle exactly once per delivery. A previous version
        // looped on ConcurrencyException, but every retry re-invoked the user's handler —
        // any HTTP call, bus.Send, or other side-effect inside HandleAsync fired again.
        // Letting the ConcurrencyException propagate hands the decision to the configured
        // transport-level retry policy instead, which users can size against their tolerance
        // for side-effect replay.
        await RunPipelineOnceAsync(scope, finder, descriptor, mapper, handler, (Message)message, messageType, headers, cancellationToken).ConfigureAwait(false);
        return ProcessResult.Handled;
    }

    private async Task RunPipelineOnceAsync(
        IServiceProvider scope,
        IProcessManagerFinder finder,
        ProcessManagerDescriptor descriptor,
        IProcessManagerPropertyMapper mapper,
        object handler,
        Message message,
        Type messageType,
        IDictionary<string, object> headers,
        CancellationToken cancellationToken)
    {
        var persistenceData = await descriptor.FindData(finder, mapper, message, cancellationToken).ConfigureAwait(false);

        bool isNew = persistenceData == null;
        object data;
        if (isNew)
        {
            var newData = descriptor.CreateData();
            descriptor.SetCorrelationId(newData, message.CorrelationId);
            data = newData;
        }
        else
        {
            data = descriptor.ExtractData(persistenceData!);
        }

        var trustQuery = replyStatusRequestReplyManager
            ?? scope.GetService<IReplyStatusRequestReplyManager>()
            ?? scope.GetService<IRequestReplyManager>() as IReplyStatusRequestReplyManager;

        var context = _contextPool.Rent(
            bus.Value,
            headers,
            queueConfig,
            busConfig,
            trustQuery,
            cancellationToken);
        try
        {
            using (_consumeContextAccessor.Push(context.Headers))
            {
                await descriptor.InvokeHandleAsync(handler, message, data, context, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Process-manager handler threw for {MessageType}; persistence skipped", messageType.Name);
            throw;
        }
        finally
        {
            context.Release();
        }

        if (isNew)
        {
            await finder.InsertDataAsync((IProcessManagerData)data, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await descriptor.UpdateData(finder, persistenceData!, cancellationToken).ConfigureAwait(false);
        }
    }
}
