using System.Runtime.CompilerServices;

namespace ServiceConnect.HealthChecks;

/// <summary>
/// Caches one instance of <typeparamref name="T"/> per <see cref="IServiceProvider"/>.
/// Backing store is a <see cref="ConditionalWeakTable{TKey,TValue}"/> keyed by SP, so
/// when a host rebuilds its provider the old SP becomes unreachable and the cached
/// instance is GC-eligible — the next probe against the new SP allocates a fresh one.
/// </summary>
/// <remarks>
/// This composes M4 (recovery-grace window state lives on the check instance) with M6
/// (rebuilt IServiceProvider gets a fresh check). M6's literal "fresh wrapper per probe"
/// implementation defeated M4's grace window because each probe allocated a new check
/// with a zero-initialised <c>_lastHealthyTicks</c>. Caching per-SP gives M4 stable
/// state across probes while still honouring M6's rebuild contract.
/// </remarks>
internal sealed class PerProviderCache<T>(Func<IServiceProvider, T> factory) where T : class
{
    private readonly ConditionalWeakTable<IServiceProvider, T> _cache = [];
    private readonly Func<IServiceProvider, T> _factory = factory;

    public T Resolve(IServiceProvider sp) =>
        _cache.GetValue(sp, key => _factory(key));
}
