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
public sealed class SystemTextJsonMessageSerializer : IMessageSerializer
{
    private readonly JsonSerializerOptions _options;

    /// <summary>
    /// Creates a serializer using optionally-customised STJ options.
    /// </summary>
    /// <param name="options">Optional source options to clone and apply. The cloned
    /// instance has ServiceConnect's wire-compat defaults applied unless the source
    /// already supplied a value.</param>
    public SystemTextJsonMessageSerializer(JsonSerializerOptions? options = null)
    {
        _options = new JsonSerializerOptions(options ?? new JsonSerializerOptions())
        {
            // Match HeaderDecoder.MaxDepth = 32 cap on inbound nesting.
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
        };
    }

    /// <inheritdoc />
    public byte[] Serialize<T>(T message) where T : Message
    {
        if (message is null)
        {
            throw new SerializationException("Cannot serialize null message", typeof(T));
        }

        try
        {
            var writer = new ArrayBufferWriter<byte>();
            using (var jsonWriter = new Utf8JsonWriter(writer))
            {
                JsonSerializer.Serialize(jsonWriter, message, message.GetType(), _options);
            }

            return writer.WrittenSpan.ToArray();
        }
        catch (JsonException ex)
        {
            throw new SerializationException($"Failed to serialize message of type {typeof(T).Name}", typeof(T), ex);
        }
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
    public T Deserialize<T>(byte[] data) where T : Message
        => (T)Deserialize(data, typeof(T));

    /// <inheritdoc />
    public T Deserialize<T>(ReadOnlySpan<byte> data) where T : Message
        => (T)Deserialize(data, typeof(T));

    /// <inheritdoc />
    public object Deserialize(byte[] data, Type type)
        => Deserialize((ReadOnlySpan<byte>)data, type);

    /// <inheritdoc />
    public object Deserialize(ReadOnlySpan<byte> data, Type type)
    {
        try
        {
            return JsonSerializer.Deserialize(data, type, _options)
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
    public object Deserialize(ReadOnlyMemory<byte> data, Type type)
        => Deserialize(data.Span, type);

    /// <inheritdoc />
    public T Deserialize<T>(ReadOnlyMemory<byte> data) where T : Message
        => (T)Deserialize(data.Span, typeof(T));

    /// <inheritdoc />
    public object Deserialize(in ReadOnlySequence<byte> data, Type type)
    {
        try
        {
            var reader = new Utf8JsonReader(data, isFinalBlock: true, state: default);
            return JsonSerializer.Deserialize(ref reader, type, _options)
                ?? throw new SerializationException(
                    $"Deserialization returned null for type {type.Name}", type);
        }
        catch (JsonException ex)
        {
            throw new SerializationException(
                $"Failed to deserialize message of type {type.Name}", type, ex);
        }
    }
}
