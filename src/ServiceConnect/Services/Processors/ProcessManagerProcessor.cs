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
    IConsumeScopeAccessor scopeAccessor,
    Lazy<IBus> bus,
    ILogger<ProcessManagerProcessor> logger,
    IBusConfiguration busConfig,
    IQueueConfiguration queueConfig,
    ConsumeContextPool contextPool,
    IConsumeContextAccessor consumeContextAccessor,
    IReplyStatusRequestReplyManager? replyStatusRequestReplyManager = null) : IMessageProcessor
{
    private readonly IConsumeContextAccessor _consumeContextAccessor = consumeContextAccessor;
    private readonly ConsumeContextPool _contextPool = contextPool;

    // Verdict is a function of the value's runtime type only; cache per-Type.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> _keyTypeValidationCache = new();

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
        // the freshly-resolved handler each delivery rather than memoising one mapper. Wrap
        // any user exception in a typed PersistenceException — without the wrap, a raw
        // user-thrown exception would surface as the dispatch failure with no context, and
        // the broker retry loop would keep redelivering the same poison message indefinitely.
        var mapper = new DefaultProcessManagerPropertyMapper();
        try
        {
            descriptor.ConfigureMapper(handler, mapper);
        }
        catch (Exception ex)
        {
            throw new PersistenceException(
                $"ConfigureMapper threw while building the property mapper for {descriptor.ProcessHandlerInterfaceType.Name} (message type '{messageType.FullName}'). " +
                "ConfigureMapper must not throw — it is invoked once per delivery to build the saga-to-message property mapping. " +
                "See the inner exception for the user-thrown failure.",
                ex);
        }

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
                    var valueType = value.GetType();
                    var ok = _keyTypeValidationCache.GetOrAdd(valueType, IsValueEqualType);
                    if (!ok)
                    {
                        throw new InvalidOperationException(
                            $"Process-manager saga lock key for ({descriptor.DataType.Name}, {messageType.Name}) " +
                            $"resolves to type '{valueType.Name}', which compares by reference equality. The per-saga " +
                            "concurrency lock requires a value-equal key type — use string, Guid, a primitive, decimal, " +
                            "or a custom type that overrides Equals(object).");
                    }
                    return new SagaLockKey(descriptor.DataType, value);
                }
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                // Fall through to fallback key; FindData will rethrow with a typed wrapper.
            }
        }

        // Fallback key when the user's mapping doesn't resolve. msg.CorrelationId is the
        // only stable per-message identifier the framework can rely on from this surface
        // (the wire MessageId lives in headers and isn't reachable here). When it's
        // Guid.Empty — usually because the producer forgot to stamp CorrelationId — every
        // empty-correlation message of the same saga DataType would otherwise key against
        // (DataType, Guid.Empty), creating a global pseudo-lock that serialises unrelated
        // sagas. Use a fresh Guid per-message in that case: the lock becomes effectively
        // exclusive to this delivery, so unrelated empty-correlation messages run in
        // parallel. The trade-off is that two redeliveries of the SAME empty-correlation
        // message no longer share a lock — but the persistor's correlation-keyed find
        // wouldn't have matched them anyway, so the lock was already meaningless.
        var fallbackKey = msg.CorrelationId != Guid.Empty ? msg.CorrelationId : Guid.NewGuid();
        return new SagaLockKey(descriptor.DataType, fallbackKey);
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
                logger.LogError(ex, "Process-manager handler threw for {MessageType}; attempting best-effort persist before rethrow", messageType.Name);
                handlerThrew = true;
                try
                {
                    await PersistAsync(finder, descriptor, mapper, message, persistenceData, data, isNew, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Cancellation during the best-effort persist; suppress and let the
                    // original handler exception propagate. Mutation may not be durable;
                    // the redelivery path still recovers — it just re-runs from stale state.
                }
                catch (ConcurrencyException raceEx)
                {
                    // Two distinct shapes reach this catch:
                    //   (a) isNew=true: a peer worker won the insert race for a never-before-seen
                    //       correlation id. Our handler ran against fresh CreateData() state; the
                    //       winner's row is durable; our local mutations are not.
                    //   (b) isNew=false: the row exists and a peer updated it to a newer version
                    //       between our FindData and our UpdateData. Our handler ran against
                    //       version N; the store now holds version N+1 (or later).
                    // Both shapes resolve identically — redelivery re-finds the durable state and
                    // re-runs the handler from it, idempotency invariants on the handler permitting.
                    // Log the actual shape so operators don't chase the wrong race.
                    if (isNew)
                    {
                        logger.LogWarning(raceEx,
                            "Best-effort persist for {MessageType}: cross-process new-saga insert race lost. The peer's insert won; our handler mutations are not durable. Redelivery will re-run from the durable state.",
                            messageType.Name);
                    }
                    else
                    {
                        logger.LogWarning(raceEx,
                            "Best-effort persist for {MessageType}: optimistic-concurrency conflict on update — a peer updated the saga to a newer version mid-handler. Our handler mutations are not durable. Redelivery will re-find the current version and re-run the handler.",
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
            await PersistAsync(finder, descriptor, mapper, message, persistenceData, data, isNew, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns true when <paramref name="t"/> participates in <c>object.Equals</c> by value
    /// rather than reference. Value types, strings, and reference types that override
    /// <c>Equals(object)</c> qualify. Arrays and plain reference types do not.
    /// </summary>
    internal static bool IsValueEqualType(Type t)
    {
        if (t.IsValueType) { return true; }            // structs, primitives, Guid, decimal, enums, DateTime
        if (t == typeof(string)) { return true; }      // string overrides Equals
        if (t.IsArray) { return false; }               // arrays use reference equality
        var current = t;
        while (current != null && current != typeof(object))
        {
            var m = current.GetMethod(
                nameof(Equals),
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly,
                binder: null,
                types: [typeof(object)],
                modifiers: null);
            if (m != null && !m.IsAbstract) { return true; }
            current = current.BaseType;
        }
        return false;
    }

    private static async Task PersistAsync(
        IProcessManagerFinder finder,
        ProcessManagerDescriptor descriptor,
        IProcessManagerPropertyMapper mapper,
        Message message,
        object? persistenceData,
        object data,
        bool isNew,
        CancellationToken cancellationToken)
    {
        if (isNew)
        {
            await finder.InsertDataAsync((IProcessManagerData)data, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Handler-driven physical completion: the documented saga-completion pattern
        // (IProcessManagerFinder.DeleteDataAsync xmldoc) lets a handler resolve the
        // finder from DI and delete the saga's row mid-handler. If the handler did
        // so and then returned cleanly, the row is gone — UpdateData against the
        // captured persistenceData would throw ConcurrencyException, message goes to
        // retry, redelivery sees a missing saga and resurrects it via CreateData().
        // Re-find here so a handler that completed the saga sees its decision respected:
        //   - re-find returns null: handler deleted; skip the update; saga stays completed.
        //   - re-find returns non-null AND Version moved: handler called UpdateData itself.
        //     Re-running PersistAsync's UpdateData against the captured (now-stale) Version
        //     would race-fail as ConcurrencyException → broker NACK → handler re-runs with
        //     all side effects replayed. Skip; the handler's own UpdateData call already
        //     committed the intended state.
        //   - re-find returns non-null AND Version unchanged: handler did not persist;
        //     proceed with UpdateData, which still uses the original captured Version so
        //     concurrent peer updates race-fail as ConcurrencyException (the intended
        //     optimistic-concurrency path).
        var freshFind = await descriptor.FindData(finder, mapper, message, cancellationToken).ConfigureAwait(false);
        if (freshFind is null)
        {
            return;
        }

        // Cast both wrappers to IVersioned for the comparison. The persistor returns
        // typed MemoryData<T> / MongoDbData<T>; both implement IVersioned per
        // IProcessManagerFinder's contract. A version mismatch indicates handler-driven
        // persistence happened during dispatch — the framework's optimistic-concurrency
        // update would now race-fail; skip to preserve handler side-effect idempotence.
        if (persistenceData is IVersioned originalVersioned
            && freshFind is IVersioned currentVersioned
            && originalVersioned.Version != currentVersioned.Version)
        {
            return;
        }

        await descriptor.UpdateData(finder, persistenceData!, cancellationToken).ConfigureAwait(false);
    }
}
