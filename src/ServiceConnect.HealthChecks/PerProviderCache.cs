using System.Runtime.CompilerServices;

namespace ServiceConnect.HealthChecks;

/// <summary>
/// Caches one instance of <typeparamref name="T"/> per <see cref="IServiceProvider"/>.
/// Backing store is a <see cref="ConditionalWeakTable{TKey,TValue}"/> keyed by SP, so
/// when a host rebuilds its provider the old SP becomes unreachable and the cached
/// instance is GC-eligible — the next probe against the new SP allocates a fresh one.
/// </summary>
/// <remarks>
/// This composes the recovery-grace window (state lives on the check instance) with the
/// rebuilt-IServiceProvider contract (a rebuilt SP gets a fresh check). A literal "fresh
/// wrapper per probe" implementation would defeat the grace window because each probe
/// would allocate a new check with a zero-initialised <c>_lastHealthyTicks</c>. Caching
/// per-SP gives the check stable state across probes while still allowing rebuilt SPs
/// to allocate a new check on first use.
/// </remarks>
internal sealed class PerProviderCache<T>(Func<IServiceProvider, T> factory) where T : class
{
    private readonly ConditionalWeakTable<IServiceProvider, T> _cache = [];
    private readonly Func<IServiceProvider, T> _factory = factory;

    public T Resolve(IServiceProvider sp) =>
        _cache.GetValue(sp, key => _factory(key));
}
