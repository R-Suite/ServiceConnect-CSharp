using System.Collections.Concurrent;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Singleton cache for compiled predicate delegates and MemoryData factories
/// used by <see cref="InMemoryProcessManagerFinder"/>.
/// Extracted from static fields to support proper DI lifetime management
/// and test isolation.
/// </summary>
internal sealed class ProcessManagerPredicateCache
{
    /// <summary>
    /// Cached predicates of shape (MemoryData&lt;T&gt;, object) -> bool, keyed by mapping shape.
    /// </summary>
    public ConcurrentDictionary<PredicateCacheKey, Delegate> CompiledPredicates { get; } = new();

    /// <summary>
    /// Cached factories that produce MemoryData&lt;TConcrete&gt; from IProcessManagerData, keyed by concrete type.
    /// </summary>
    public ConcurrentDictionary<Type, Func<IProcessManagerData, object>> MemoryDataFactories { get; } = new();

    internal readonly struct PredicateCacheKey : IEquatable<PredicateCacheKey>
    {
        public readonly Type T;
        public readonly IReadOnlyDictionary<string, Type> PropertiesHierarchy;
        public readonly Type PropertyType;

        public PredicateCacheKey(Type t, IReadOnlyDictionary<string, Type> propertiesHierarchy, Type propertyType)
        {
            ArgumentNullException.ThrowIfNull(t);
            ArgumentNullException.ThrowIfNull(propertiesHierarchy);
            ArgumentNullException.ThrowIfNull(propertyType);
            T = t;
            PropertiesHierarchy = propertiesHierarchy;
            PropertyType = propertyType;
        }

        public bool Equals(PredicateCacheKey other)
        {
            if (T != other.T || PropertyType != other.PropertyType) return false;
            if (PropertiesHierarchy.Count != other.PropertiesHierarchy.Count) return false;
            foreach (var kvp in PropertiesHierarchy)
            {
                if (!other.PropertiesHierarchy.TryGetValue(kvp.Key, out var otherType) || otherType != kvp.Value)
                    return false;
            }
            return true;
        }

        public override bool Equals(object? obj) => obj is PredicateCacheKey k && Equals(k);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(T);
            hash.Add(PropertyType);
            // XOR-combine per-entry hashes so the result is independent of the
            // dictionary's (undefined) iteration order. Otherwise Equals
            // could be true while GetHashCode disagreed, violating the contract.
            int entryHash = 0;
            foreach (var kvp in PropertiesHierarchy)
                entryHash ^= HashCode.Combine(kvp.Key, kvp.Value);
            hash.Add(entryHash);
            return hash.ToHashCode();
        }
    }
}
