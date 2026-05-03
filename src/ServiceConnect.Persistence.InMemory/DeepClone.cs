using Newtonsoft.Json;

namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Round-trip JSON clone used by the in-memory persistors to isolate callers from
/// stored state. Without this, Insert stores the caller's reference and Get returns
/// the stored reference — any subsequent mutation on either side silently corrupts
/// the other. Serializer-based clone is the only approach that handles nested
/// collections and records without per-type plumbing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Security boundary.</b> This implementation uses
/// <see cref="Newtonsoft.Json.TypeNameHandling.Auto"/> to round-trip polymorphic
/// CLR types (e.g. <c>List&lt;Animal&gt;</c> carrying <c>Dog</c> instances,
/// or <c>Dictionary&lt;string, object&gt;</c> headers). <c>TypeNameHandling.Auto</c>
/// is a known deserialization-gadget surface — feeding untrusted JSON through it
/// could load arbitrary types via the <c>$type</c> field.
/// </para>
/// <para>
/// <b>Use only for in-process trusted data.</b> Do not extend this helper to
/// deserialise external input, configuration, network payloads, or any value
/// originating outside the current process. The current uses (saga state,
/// aggregator entries, timeout headers) all originate from in-process callers
/// and never leave the AppDomain between serialise and deserialise.
/// </para>
/// <para>
/// <b>Explicit-interface auto-properties.</b> Newtonsoft.Json only serialises
/// properties reachable on the concrete type by short name. An auto-property
/// declared as an explicit-interface implementation
/// (e.g. <c>Guid IFoo.FooId { get; set; }</c>) is invisible to the serialiser
/// and will round-trip as <c>default</c>. Saga and aggregator types that need
/// explicit-interface properties to survive cloning must back them with a
/// public property (e.g. <c>public Guid FooIdValue { get; set; }</c> with
/// <c>Guid IFoo.FooId =&gt; FooIdValue;</c>).
/// </para>
/// </remarks>
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
