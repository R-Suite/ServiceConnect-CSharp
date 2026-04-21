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
        // TypeNameHandling.None avoids the Newtonsoft deserialization gadget surface.
        TypeNameHandling = TypeNameHandling.None,
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
