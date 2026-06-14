using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Round-trip JSON clone used by the in-memory persistors to isolate callers from
/// stored state. Without this, Insert stores the caller's reference and Get returns
/// the stored reference — any subsequent mutation on either side silently corrupts
/// the other.
/// </summary>
/// <remarks>
/// <para>
/// <b>System.Text.Json, not BSON.</b> The previous implementation round-tripped through
/// MongoDB.Bson so that explicit-interface auto-properties were preserved. That coupling
/// dragged the BSON serialization package into every consumer of
/// <c>ServiceConnect.Persistence.InMemory</c> — surprising for a package whose tagline is
/// "in-memory, no broker, no database." STJ is part of the .NET BCL on every supported
/// target framework, costs no extra package reference, and round-trips every regular
/// public property in a saga or aggregator's data shape. Sagas whose data uses
/// explicit-interface auto-properties with backing fields are not supported by the
/// in-memory persistor — use the MongoDB persistor for that edge case, or expose the
/// backing field via a public property.
/// </para>
/// <para>
/// <b>Polymorphism.</b> Serializing against <c>value.GetType()</c> instead of the
/// declared <c>T</c> makes STJ emit the runtime type's properties rather than just the
/// base's. Nested polymorphic values still need <c>[JsonDerivedType]</c> attributes to
/// round-trip; the in-memory persistors document this in their xmldoc.
/// </para>
/// <para>
/// <b>Security boundary.</b> The output of this clone NEVER leaves the AppDomain —
/// input is always trusted in-process state. Do not extend this helper to deserialise
/// external input, configuration, or network payloads.
/// </para>
/// </remarks>
internal static class DeepClone
{
    private static readonly JsonSerializerOptions Options = new()
    {
        // Cover public fields too, not just auto-properties. Some saga data shapes use
        // public readonly fields for invariants set in the constructor; without this
        // the field's value would be silently dropped through the round-trip.
        IncludeFields = true,
        // Keep enums as numeric values — matches the on-the-wire behaviour of the bus's
        // own SystemTextJsonMessageSerializer; switching to JsonStringEnumConverter would
        // make the in-memory store's "stored shape" diverge from what Mongo persists.
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Round-trips <paramref name="value"/> through System.Text.Json to produce a deep
    /// clone. Serializes against the runtime type so derived-class properties survive
    /// when the caller's static type is a base.
    /// </summary>
    public static T Clone<T>(T value) where T : notnull
    {
        var runtimeType = value.GetType();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, runtimeType, Options);
        var clone = JsonSerializer.Deserialize(bytes, runtimeType, Options)
            ?? throw new InvalidOperationException(
                $"Deep clone of {runtimeType.FullName} returned null.");
        return (T)clone;
    }
}
