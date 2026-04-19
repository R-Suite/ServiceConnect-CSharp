using System.Text;
using Newtonsoft.Json;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

public sealed class NewtonsoftJsonMessageSerializer : IMessageSerializer
{
    private readonly JsonSerializerSettings _settings;
    private readonly JsonSerializer _serializer;

    public NewtonsoftJsonMessageSerializer(JsonSerializerSettings? settings = null)
    {
        // Clone settings before mutating to avoid side-effects on the caller's instance.
        var cloned = new JsonSerializerSettings
        {
            // Copy properties that callers commonly set
            NullValueHandling     = settings?.NullValueHandling     ?? NullValueHandling.Include,
            DefaultValueHandling  = settings?.DefaultValueHandling  ?? DefaultValueHandling.Include,
            ReferenceLoopHandling = settings?.ReferenceLoopHandling ?? ReferenceLoopHandling.Error,
            DateFormatHandling    = settings?.DateFormatHandling    ?? DateFormatHandling.IsoDateFormat,
            // RoundtripKind preserves DateTimeKind (Utc/Local/Unspecified) across
            // serialize → deserialize, so timestamps don't silently drift when a
            // message crosses a timezone boundary. Local caused cross-host drift.
            DateTimeZoneHandling  = settings?.DateTimeZoneHandling  ?? DateTimeZoneHandling.RoundtripKind,
            Formatting            = settings?.Formatting            ?? Formatting.None,
            ContractResolver      = settings?.ContractResolver,
            Converters            = settings?.Converters != null
                                        ? new System.Collections.Generic.List<JsonConverter>(settings.Converters)
                                        : new System.Collections.Generic.List<JsonConverter>(),
            // Prevent deserialization gadget attacks via $type metadata
            TypeNameHandling      = TypeNameHandling.None,
        };

        _settings   = cloned;
        _serializer = JsonSerializer.Create(cloned);
    }

    public byte[] Serialize<T>(T message) where T : Message
    {
        if (message is null)
            throw new Interfaces.Exceptions.SerializationException(
                "Cannot serialize null message", typeof(T));

        try
        {
            using var ms = new MemoryStream();
            using (var sw = new StreamWriter(ms, Encoding.UTF8, bufferSize: 1024, leaveOpen: true))
            using (var jw = new JsonTextWriter(sw))
            {
                _serializer.Serialize(jw, message);
            }
            return ms.ToArray();
        }
        catch (JsonException ex)
        {
            throw new Interfaces.Exceptions.SerializationException(
                $"Failed to serialize message of type {typeof(T).Name}", typeof(T), ex);
        }
    }

    public void Serialize<T>(T message, System.Buffers.IBufferWriter<byte> output) where T : Message
    {
        ArgumentNullException.ThrowIfNull(output);

        var bytes = Serialize(message);
        var span = output.GetSpan(bytes.Length);
        bytes.CopyTo(span);
        output.Advance(bytes.Length);
    }

    public T Deserialize<T>(byte[] data) where T : Message
    {
        return (T)Deserialize(data, typeof(T));
    }

    public T Deserialize<T>(ReadOnlySpan<byte> data) where T : Message
    {
        return (T)Deserialize(data, typeof(T));
    }

    public object Deserialize(byte[] data, Type type)
    {
        return Deserialize((ReadOnlyMemory<byte>)data.AsMemory(), type);
    }

    public object Deserialize(ReadOnlySpan<byte> data, Type type)
    {
        using var ms = new MemoryStream(data.ToArray(), writable: false);
        return DeserializeFromStream(ms, type);
    }

    public object Deserialize(ReadOnlyMemory<byte> data, Type type)
    {
        using var stream = new IO.ReadOnlyMemoryStream(data);
        return DeserializeFromStream(stream, type);
    }

    public T Deserialize<T>(ReadOnlyMemory<byte> data) where T : Message
    {
        return (T)Deserialize(data, typeof(T));
    }

    public object Deserialize(in System.Buffers.ReadOnlySequence<byte> data, Type type)
    {
        using var stream = new IO.ReadOnlySequenceStream(data);
        return DeserializeFromStream(stream, type);
    }

    private object DeserializeFromStream(Stream stream, Type type)
    {
        try
        {
            using var sr = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: false);
            using var jr = new JsonTextReader(sr);
            return _serializer.Deserialize(jr, type)
                ?? throw new Interfaces.Exceptions.SerializationException(
                    $"Deserialization returned null for type {type.Name}", type);
        }
        catch (JsonException ex)
        {
            throw new Interfaces.Exceptions.SerializationException(
                $"Failed to deserialize message of type {type.Name}", type, ex);
        }
    }
}
