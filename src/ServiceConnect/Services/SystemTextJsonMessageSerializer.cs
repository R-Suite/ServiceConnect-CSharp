using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;

namespace ServiceConnect.Services;

/// <summary>
/// System.Text.Json implementation of <see cref="IMessageSerializer"/>. Wire format
/// is JSON-equivalent to the v7 Newtonsoft implementation under the matching settings
/// (relaxed Unicode escaping, ISO 8601 round-trip dates, MaxDepth = 32). Cross-version
/// behaviour is enforced by the SerializationCompatTests corpus.
/// </summary>
internal sealed class SystemTextJsonMessageSerializer : IMessageSerializer
{
    private readonly JsonSerializerOptions _options;

    /// <summary>
    /// Creates a serializer using optionally-customised STJ options.
    /// </summary>
    /// <param name="options">Optional base options to clone. All non-wire-compat settings
    /// (custom converters, type-info resolvers, WriteIndented, etc.) are preserved from
    /// the source. The wire-compat settings listed below are <em>always</em> overwritten
    /// by ServiceConnect's defaults so the cross-version corpus assertions hold even when
    /// callers pass a customised options instance.</param>
    public SystemTextJsonMessageSerializer(JsonSerializerOptions? options = null)
    {
        _options = new JsonSerializerOptions(options ?? new JsonSerializerOptions())
        {
            // Match HeaderDecoder.MaxDepth = 32 cap on inbound nesting. STJ has no direct
            // equivalent of Newtonsoft's ReferenceLoopHandling.Error: a reference cycle in
            // STJ surfaces as a JsonException once nesting exceeds MaxDepth (depth-cap
            // detection rather than identity-tracking). The observable behaviour at the
            // call site is the same — both wrap as SerializationException — but the
            // semantic shift is documented here for future maintainers.
            MaxDepth = 32,

            // Newtonsoft's default emits literal non-ASCII characters; STJ default
            // escapes them. UnsafeRelaxedJsonEscaping keeps the wire bytes byte-identical
            // for typical payloads, which is what the corpus tests assert.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

            // Newtonsoft.Json tolerates string-encoded numbers ("3" → int) by default.
            // Match that behaviour so a v7 producer's payload deserialises here.
            NumberHandling = JsonNumberHandling.AllowReadingFromString,

            // Newtonsoft default is case-sensitive matching; preserve.
            PropertyNameCaseInsensitive = false,

            // Newtonsoft serialises only properties (not fields) by default; preserve.
            IncludeFields = false,

            // Equivalent of NullValueHandling.Include — emit null fields on the wire.
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,

            // Newtonsoft used DateTimeZoneHandling.RoundtripKind to preserve DateTimeKind
            // across serialise/deserialise. STJ already preserves DateTimeKind for ISO 8601
            // round-trips by default — no equivalent setting needed, behaviour matches.
        };
    }

    /// <inheritdoc />
    public void Serialize<T>(T message, IBufferWriter<byte> output) where T : Message
    {
        ArgumentNullException.ThrowIfNull(output);
        if (message is null)
        {
            throw new SerializationException("Cannot serialize null message", typeof(T));
        }

        try
        {
            using var jsonWriter = new Utf8JsonWriter(output);
            JsonSerializer.Serialize(jsonWriter, message, message.GetType(), _options);
        }
        catch (JsonException ex)
        {
            throw new SerializationException($"Failed to serialize message of type {typeof(T).Name}", typeof(T), ex);
        }
    }

    /// <inheritdoc />
    public T Deserialize<T>(ReadOnlyMemory<byte> data) where T : Message
        => (T)Deserialize(data, typeof(T));

    /// <inheritdoc />
    public object Deserialize(ReadOnlyMemory<byte> data, Type type)
    {
        try
        {
            return JsonSerializer.Deserialize(data.Span, type, _options)
                ?? throw new SerializationException(
                    $"Deserialization returned null for type {type.Name}", type);
        }
        catch (JsonException ex)
        {
            throw new SerializationException(
                $"Failed to deserialize message of type {type.Name}", type, ex);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Overrides the interface default to read across segments via Utf8JsonReader without
    /// flattening into a byte[] first — the streaming path delivers messages as multi-segment
    /// sequences and the per-message copy was a real regression vs. v7's Newtonsoft impl.
    /// </remarks>
    public object Deserialize(in ReadOnlySequence<byte> data, Type type)
    {
        try
        {
            // The state-default ctor uses JsonReaderState's internal MaxDepth=64, NOT the
            // serializer's configured MaxDepth. A 35-deep payload would be rejected by the
            // span overload (which threads _options through JsonSerializer.Deserialize) but
            // accepted by this sequence overload — defeating the depth cap on the consume
            // hot path. Construct the state with MaxDepth from _options so both overloads
            // enforce the same boundary.
            var readerOptions = new JsonReaderOptions
            {
                MaxDepth = _options.MaxDepth,
                // CommentHandling and AllowTrailingCommas remain at their defaults — STJ's
                // JsonSerializerOptions does not expose them as a unified setting. The wire
                // format does not include comments or trailing commas (asserted by the
                // serialization-compat corpus), so this matches the span overload's behaviour.
            };
            var reader = new Utf8JsonReader(data, isFinalBlock: true, state: new JsonReaderState(readerOptions));
            return JsonSerializer.Deserialize(ref reader, type, _options)
                ?? throw new SerializationException(
                    $"Deserialization returned null for type {type.Name}", type);
        }
        catch (JsonException ex)
        {
            throw new SerializationException(
                $"Failed to deserialize message of type {type.Name}", type, ex);
        }
        catch (InvalidOperationException ex)
        {
            // JsonSerializer.Deserialize(ref Utf8JsonReader, ...) throws InvalidOperationException
            // on malformed reader state. The reader is freshly constructed here so this is
            // unreachable in practice; wrap for consistency with other deserialize paths.
            throw new SerializationException(
                $"Failed to deserialize message of type {type.Name}", type, ex);
        }
    }
}
