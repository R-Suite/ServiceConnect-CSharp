using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Services;

namespace ServiceConnect.Services.Processors;

internal sealed class ProcessManagerProcessor(
    ProcessManagerHandlerRegistry registry,
    IServiceProvider serviceProvider,
    Lazy<IBus> bus,
    ILogger<ProcessManagerProcessor> logger,
    IBusConfiguration busConfig,
    IQueueConfiguration queueConfig,
    ConsumeContextPool contextPool,
    ConsumeContextAccessor consumeContextAccessor,
    IReplyStatusRequestReplyManager? replyStatusRequestReplyManager = null) : IMessageProcessor
{
    // Cached mapper per handler interface type. ConfigureMapper compiles expression lambdas
    // that are identical for a given handler type, so we only pay the cost once.
    private static readonly ConcurrentDictionary<Type, IProcessManagerPropertyMapper> MapperCache = new();
    private readonly ConsumeContextAccessor _consumeContextAccessor = consumeContextAccessor;
    private readonly ConsumeContextPool _contextPool = contextPool;

    // Bounded optimistic-concurrency retry schedule for find→invoke→update. Keep it small
    // because the handler side-effects are re-run on each attempt — if contention is high
    // enough to burn the budget, the message should be requeued at the transport layer.
    private static readonly TimeSpan[] ConcurrencyBackoff =
    [
        TimeSpan.FromMilliseconds(10),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(200)
    ];

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

        // Retry the find→invoke→update cycle on ConcurrencyException so two concurrent messages
        // for the same saga converge instead of losing one to a lost-update. Side-effects inside
        // the handler are re-run on each attempt — callers who can't tolerate that should
        // externalise their side-effects or reduce saga concurrency at the transport layer.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await RunPipelineOnceAsync(finder, descriptor, mapper, handler, (Message)message, messageType, headers, cancellationToken).ConfigureAwait(false);
                return ProcessResult.Handled;
            }
            catch (ConcurrencyException) when (attempt < ConcurrencyBackoff.Length)
            {
                var delay = ConcurrencyBackoff[attempt];
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)Math.Max(1, delay.TotalMilliseconds / 2)));
                logger.LogDebug(
                    "ConcurrencyException on attempt {Attempt} for {MessageType}; retrying after {Delay}",
                    attempt + 1, messageType.Name, delay + jitter);
                await Task.Delay(delay + jitter, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunPipelineOnceAsync(
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
            ?? serviceProvider.GetService<IReplyStatusRequestReplyManager>()
            ?? serviceProvider.GetService<IRequestReplyManager>() as IReplyStatusRequestReplyManager;

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
                descriptor.SetHandlerContext(handler, context);
                await descriptor.InvokeHandleAsync(handler, message, data).ConfigureAwait(false);
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
