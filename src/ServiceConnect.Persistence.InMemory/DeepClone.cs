using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Round-trip BSON clone used by the in-memory persistors to isolate callers from
/// stored state. Without this, Insert stores the caller's reference and Get returns
/// the stored reference — any subsequent mutation on either side silently corrupts
/// the other.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why BSON, not System.Text.Json or Newtonsoft.Json.</b> The in-memory persistors
/// must round-trip arbitrary saga / aggregator types including ones with explicit-
/// interface auto-properties (<c>Guid IFoo.FooId { get; set; }</c>). Both
/// System.Text.Json and Newtonsoft.Json only serialise properties reachable on the
/// concrete type by short name and silently drop explicit-interface backing fields,
/// causing the in-memory store to produce <c>default(Guid)</c> for those properties
/// while the Mongo store (which uses <see cref="BsonClassMap"/>) preserves them —
/// a silent test-vs-prod divergence. BSON's <see cref="BsonClassMap"/> auto-discovers
/// explicit-interface auto-properties and closes that gap.
/// </para>
/// <para>
/// <b>Polymorphism.</b> Nested base/interface-typed properties holding derived
/// values round-trip correctly via the BSON <c>_t</c> discriminator (the same path
/// the Mongo persistors already use).
/// </para>
/// <para>
/// <b>Security boundary.</b> BSON deserialisation can construct CLR types via
/// <c>_t</c>, the same hazard class as Newtonsoft's <c>$type</c>. The output of
/// this clone NEVER leaves the AppDomain — input is always trusted in-process
/// state. Do not extend this helper to deserialise external input, configuration,
/// or network payloads.
/// </para>
/// </remarks>
internal static class DeepClone
{
    /// <summary>
    /// Round-trips <paramref name="value"/> through BSON to produce a deep clone.
    /// The BSON path captures all properties reachable via <see cref="BsonClassMap"/>
    /// — including explicit-interface auto-properties — and round-trips polymorphic
    /// nested values via the <c>_t</c> discriminator.
    /// </summary>
    public static T Clone<T>(T value) where T : notnull
    {
        var runtimeType = value.GetType();
        // Wrap the value in a synthetic single-field document. BSON refuses to write
        // a collection (arrays, List<>, Dictionary<,> serialised as KVP arrays) at
        // the document root — root must be {} not []. Header values legitimately
        // arrive as List<byte> / string[] etc., so we always wrap; non-collection
        // roots are unaffected. The serializer is looked up by runtime type so
        // explicit-interface auto-properties and nested polymorphism (via the
        // _t discriminator) round-trip correctly.
        var serializer = BsonSerializer.LookupSerializer(runtimeType);
        var wrapper = new BsonDocument();
        using (var writer = new BsonDocumentWriter(wrapper))
        {
            writer.WriteStartDocument();
            writer.WriteName("v");
            var ctx = BsonSerializationContext.CreateRoot(writer);
            serializer.Serialize(ctx, value);
            writer.WriteEndDocument();
        }
        object? clone;
        using (var reader = new BsonDocumentReader(wrapper))
        {
            reader.ReadStartDocument();
            reader.ReadName();
            var ctx = BsonDeserializationContext.CreateRoot(reader);
            clone = serializer.Deserialize(ctx);
            reader.ReadEndDocument();
        }
        if (clone is null)
        {
            throw new InvalidOperationException(
                $"Deep clone of {runtimeType.FullName} returned null.");
        }
        return (T)clone;
    }
}
