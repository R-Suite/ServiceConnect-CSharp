using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

/// <summary>
/// Stores known message CLR types by their full and assembly-qualified names for dispatch-time lookup.
/// </summary>
public sealed class MessageTypeRegistry : IMessageTypeRegistry
{
    private readonly ConcurrentDictionary<string, Type> _registeredTypes = new(StringComparer.Ordinal);
    private FrozenDictionary<string, Type>? _types;
    // Monotonic version bumped on every Register. TryResolve captures the version before
    // snapshotting _registeredTypes; if a concurrent Register advances the version between
    // the snapshot and the CAS, the snapshot may be stale and must not be cached.
    private long _version;

    // TEST HOOK — fired between the snapshot of _registeredTypes and the CAS that publishes
    // it as the cached _types. Allows tests to deterministically simulate a racing Register
    // inside the snapshot-to-CAS window. Null in production.
    internal Action? _testHookBeforeCas;

    /// <inheritdoc />
    public bool TryResolve(string typeName, [MaybeNullWhen(false)] out Type type)
    {
        while (true)
        {
            var cached = Volatile.Read(ref _types);
            if (cached != null)
            {
                return cached.TryGetValue(typeName, out type);
            }

            var v0 = Volatile.Read(ref _version);
            var snapshot = _registeredTypes.ToFrozenDictionary();

            _testHookBeforeCas?.Invoke();

            if (Interlocked.CompareExchange(ref _types, snapshot, null) == null)
            {
                // Published. If a concurrent Register advanced the version after v0, the
                // snapshot we just cached may have missed that Register's addition — invalidate
                // so the next lookup takes a fresh snapshot. A Register that runs purely after
                // our CAS will itself Volatile.Write _types = null and we don't need to do it.
                if (Volatile.Read(ref _version) != v0)
                {
                    Volatile.Write(ref _types, null);
                }

                return snapshot.TryGetValue(typeName, out type);
            }
            // Lost the CAS race; loop and read the winning cache.
        }
    }

    /// <inheritdoc />
    public void Register(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        // Two types sharing a FullName (or, less commonly, an AssemblyQualifiedName)
        // must not silently overwrite each other — dispatch would then resolve to
        // whichever was registered last, which is load-order dependent. Reject the
        // collision with a clear signal. Re-registering the exact same Type is
        // idempotent.
        if (type.AssemblyQualifiedName is not null)
        {
            AddOrReject(type.AssemblyQualifiedName, type);
        }

        if (type.FullName is not null)
        {
            AddOrReject(type.FullName, type);
        }

        // Bump version first so a racing TryResolve that has already taken its snapshot
        // observes the advance and skips caching. Only then clear the cached snapshot.
        Interlocked.Increment(ref _version);
        Volatile.Write(ref _types, null);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> AllRegisteredTypeNames()
    {
        // _registeredTypes.Keys snapshots under ConcurrentDictionary's enumerator semantics,
        // which is point-in-time consistent. Materialise to a list so the returned collection
        // is fully detached from later Register calls — callers that pass this to a Mongo
        // $in filter rely on a stable count.
        return [.. _registeredTypes.Keys];
    }

    private void AddOrReject(string key, Type type)
    {
        var existing = _registeredTypes.GetOrAdd(key, type);
        if (existing != type)
        {
            throw new InvalidOperationException(
                $"Message type registration collision on key '{key}': already registered as '{existing.AssemblyQualifiedName}', cannot re-register as '{type.AssemblyQualifiedName}'.");
        }
    }
}
