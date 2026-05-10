using System.Collections.Concurrent;
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

    // Per-correlation-id serialization. Two messages for the same saga that arrive
    // concurrently both observe FindData==null and would otherwise both run the user's
    // HandleAsync (with side effects: bus.Send, HTTP, etc.) and both call InsertDataAsync.
    // The unique CorrelationId index ensures only one insert wins, but the loser's
    // handler has already run its side effects against a saga state that is later
    // overwritten on redelivery. Acquiring this semaphore around find→handle→persist
    // turns the duplicate-creation race into a sequential second-find that observes
    // the just-committed saga and takes the update path instead.
    //
    // Cleanup: each entry holds a refcount of in-flight callers under a monitor lock.
    // The last caller to release decrements to zero, marks the entry removed, and
    // detaches it from the dictionary. Concurrent acquirers re-check the Removed flag
    // under the same lock and retry on a fresh entry, so an idle correlation id never
    // leaks a stale SemaphoreSlim.
    private readonly ConcurrentDictionary<Guid, CorrelationLock> _correlationLocks = new();

    private sealed class CorrelationLock
    {
        public readonly SemaphoreSlim Sem = new(1, 1);
        public int Outstanding;
        public bool Removed;
    }

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
        var msg = (Message)message;
        var entry = AcquireCorrelationLock(msg.CorrelationId);
        var semaphoreAcquired = false;
        try
        {
            await entry.Sem.WaitAsync(cancellationToken).ConfigureAwait(false);
            semaphoreAcquired = true;
            await RunPipelineOnceAsync(scope, finder, descriptor, mapper, handler, msg, messageType, headers, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Refcount must be released even if WaitAsync threw before we got the
            // semaphore, otherwise Outstanding leaks and the entry is never removed.
            if (semaphoreAcquired)
            {
                entry.Sem.Release();
            }
            ReleaseCorrelationLock(msg.CorrelationId, entry);
        }
        return ProcessResult.Handled;
    }

    private CorrelationLock AcquireCorrelationLock(Guid correlationId)
    {
        while (true)
        {
            var entry = _correlationLocks.GetOrAdd(correlationId, static _ => new CorrelationLock());
            lock (entry)
            {
                if (!entry.Removed)
                {
                    entry.Outstanding++;
                    return entry;
                }
                // The entry was removed between GetOrAdd and our lock; retry to either
                // observe a freshly-added one or create a new entry of our own.
            }
        }
    }

    private void ReleaseCorrelationLock(Guid correlationId, CorrelationLock entry)
    {
        lock (entry)
        {
            entry.Outstanding--;
            if (entry.Outstanding == 0)
            {
                // Last caller out: mark removed under the lock so any concurrent acquirer
                // observes Removed=true on its recheck and retries with a fresh entry.
                // TryRemove(KVP) only succeeds if the dict still maps to this exact entry,
                // so a fresh entry installed by another thread (extremely unlikely under
                // this lock, since GetOrAdd is atomic) is left untouched.
                entry.Removed = true;
                _correlationLocks.TryRemove(new KeyValuePair<Guid, CorrelationLock>(correlationId, entry));
            }
        }
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
        bool handlerThrew = false;
        try
        {
            try
            {
                using (_consumeContextAccessor.Push(context.Headers))
                {
                    await descriptor.InvokeHandleAsync(handler, message, data, context, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Cooperative shutdown: the dispatcher's cancellation token fired and the
                // handler honoured it. Don't try to persist on cancel — the cancellation
                // token would also abort the persist call, and the redelivery on resumption
                // will re-run the handler from the previously-persisted state.
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Process-manager handler threw for {MessageType}; persisting partial saga state before rethrow", messageType.Name);
                handlerThrew = true;
                try
                {
                    await PersistAsync(finder, descriptor, persistenceData, data, isNew, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Cancellation during the best-effort persist; suppress and let the
                    // original handler exception propagate. Mutation may not be durable;
                    // the redelivery path still recovers — it just re-runs from stale state.
                }
                catch (Exception persistEx)
                {
                    logger.LogError(persistEx,
                        "Best-effort persist after handler failure also failed for {MessageType}; original exception will be rethrown",
                        messageType.Name);
                }
                throw;
            }
        }
        finally
        {
            context.Release();
        }

        // Success path: handler returned cleanly. Persist normally; the catch above already
        // handled the failure path so this only runs when handlerThrew is false.
        if (!handlerThrew)
        {
            await PersistAsync(finder, descriptor, persistenceData, data, isNew, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task PersistAsync(
        IProcessManagerFinder finder,
        ProcessManagerDescriptor descriptor,
        object? persistenceData,
        object data,
        bool isNew,
        CancellationToken cancellationToken)
    {
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
