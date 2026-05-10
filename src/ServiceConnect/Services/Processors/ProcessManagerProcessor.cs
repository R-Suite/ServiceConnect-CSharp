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

    // Per-saga-key serialization. Two messages targeting the same saga that arrive
    // concurrently must serialize through find→handle→persist or both observe
    // FindData==null, both run the user's HandleAsync (with side effects: bus.Send,
    // HTTP, etc.), and both call InsertDataAsync. The unique correlation index ensures
    // only one insert wins, but the loser's handler has already run its side effects
    // against a saga state that is later overwritten on redelivery.
    //
    // The lock key is the mapped property value the user's IProcessManagerPropertyMapper
    // uses to find the saga (e.g. m => m.OrderId), NOT msg.CorrelationId — two messages
    // can target the same saga with different message-CorrelationIds (the typical case
    // when each outbound message has its own MessageId), so locking by msg.CorrelationId
    // would not actually serialise the dispatch path. Composing the saga's data type into
    // the key (alongside the mapped value) prevents distinct saga types that happen to
    // use overlapping key spaces from blocking each other.
    //
    // Cleanup: each entry holds a refcount of in-flight callers under a monitor lock.
    // The last caller to release decrements to zero, marks the entry removed, and
    // detaches it from the dictionary. Concurrent acquirers re-check the Removed flag
    // under the same lock and retry on a fresh entry, so an idle key never leaks a
    // stale SemaphoreSlim.
    private readonly ConcurrentDictionary<SagaLockKey, CorrelationLock> _correlationLocks = new();

    private readonly record struct SagaLockKey(Type DataType, object KeyValue)
    {
        public bool Equals(SagaLockKey other)
            => DataType == other.DataType && Equals(KeyValue, other.KeyValue);
        public override int GetHashCode()
            => HashCode.Combine(DataType, KeyValue);
    }

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
        var lockKey = BuildLockKey(descriptor, mapper, msg, messageType);
        var entry = AcquireCorrelationLock(lockKey);
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
            ReleaseCorrelationLock(lockKey, entry);
        }
        return ProcessResult.Handled;
    }

    // Builds the saga-scope lock key from the user's mapper. Picks the mapping for the
    // exact message type, falling back to the base Message wildcard if present (mirrors
    // the persistor's match order). When no mapping resolves a usable value (mapping
    // misconfigured, MessageProp throws, or the value is null), fall back to msg.CorrelationId
    // — the find/persist path will surface the misconfiguration as its own typed exception
    // shortly afterwards, and per-delivery serialisation against ANY stable key is better
    // than no serialisation at all.
    private static SagaLockKey BuildLockKey(
        ProcessManagerDescriptor descriptor,
        DefaultProcessManagerPropertyMapper mapper,
        Message msg,
        Type messageType)
    {
        ProcessManagerToMessageMap? mapping = null;
        ProcessManagerToMessageMap? fallback = null;
        foreach (var m in mapper.Mappings)
        {
            if (m.MessageType == messageType) { mapping = m; break; }
            if (fallback == null && m.MessageType == typeof(Message))
            {
                fallback = m;
            }
        }
        mapping ??= fallback;

        if (mapping is not null)
        {
            try
            {
                var value = mapping.MessageProp.Invoke(msg);
                if (value is not null)
                {
                    return new SagaLockKey(descriptor.DataType, value);
                }
            }
            catch
            {
                // Fall through to CorrelationId fallback; FindData will rethrow with a typed wrapper.
            }
        }

        return new SagaLockKey(descriptor.DataType, msg.CorrelationId);
    }

    private CorrelationLock AcquireCorrelationLock(SagaLockKey key)
    {
        while (true)
        {
            var entry = _correlationLocks.GetOrAdd(key, static _ => new CorrelationLock());
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

    private void ReleaseCorrelationLock(SagaLockKey key, CorrelationLock entry)
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
                _correlationLocks.TryRemove(new KeyValuePair<SagaLockKey, CorrelationLock>(key, entry));
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
                catch (ConcurrencyException)
                {
                    // The new-saga cross-process race lost: another writer inserted first.
                    // The work is recoverable as Find→Update on the now-existing row, so try
                    // once before giving up. If the second find still misses (extremely
                    // unlikely — the winning insert is durable by the time we got the
                    // ConcurrencyException), or the update also throws, fall through to the
                    // generic catch below and let the original handler exception propagate.
                    try
                    {
                        var freshFind = await descriptor.FindData(finder, mapper, message, cancellationToken).ConfigureAwait(false);
                        if (freshFind is not null)
                        {
                            await descriptor.UpdateData(finder, freshFind, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            logger.LogWarning(
                                "Best-effort persist for {MessageType}: race winner's row not visible on re-find; partial saga state was not persisted",
                                messageType.Name);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Cancellation cuts the recovery path; redelivery will re-run from stale state.
                    }
                    catch (Exception recoverEx)
                    {
                        logger.LogError(recoverEx,
                            "Best-effort persist Find→Update recovery after concurrency loss also failed for {MessageType}; original exception will be rethrown",
                            messageType.Name);
                    }
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
