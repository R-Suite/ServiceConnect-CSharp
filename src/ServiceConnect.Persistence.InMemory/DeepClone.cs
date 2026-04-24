using Newtonsoft.Json;

namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Round-trip JSON clone used by the in-memory persistors to isolate callers from
/// stored state. Without this, Insert stores the caller's reference and Get returns
/// the stored reference — any subsequent mutation on either side silently corrupts
/// the other. Serializer-based clone is the only approach that handles nested
/// collections and records without per-type plumbing.
/// </summary>
internal static class DeepClone
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        // TypeNameHandling.Auto writes $type metadata only when the runtime type differs
        // from the declared type — necessary so polymorphic payloads inside collections
        // (List<Animal> containing Dog, base-typed reference fields) round-trip with
        // subclass data intact. Deserialization-gadget concerns don't apply here: JSON
        // produced by this clone never leaves the process; serializer and deserializer
        // run in the same AppDomain, same moment.
        TypeNameHandling = TypeNameHandling.Auto,
        TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
        // Preserve DateTimeKind across round-trip so timestamps don't drift when an
        // in-memory saga is stored/retrieved across time zones in tests.
        DateTimeZoneHandling = DateTimeZoneHandling.RoundtripKind,
    };

    public static T Clone<T>(T value) where T : notnull
    {
        var runtimeType = value.GetType();
        var json = JsonConvert.SerializeObject(value, runtimeType, Settings);
        var clone = JsonConvert.DeserializeObject(json, runtimeType, Settings)
            ?? throw new InvalidOperationException(
                $"Deep clone of {runtimeType.FullName} returned null.");
        return (T)clone;
    }
}
