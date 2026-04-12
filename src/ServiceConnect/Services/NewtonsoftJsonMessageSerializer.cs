using System.Text;
using Newtonsoft.Json;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

public sealed class NewtonsoftJsonMessageSerializer : IMessageSerializer
{
    private readonly JsonSerializerSettings _settings;

    public NewtonsoftJsonMessageSerializer(JsonSerializerSettings? settings = null)
    {
        _settings = settings ?? new JsonSerializerSettings();
        // Prevent deserialization gadget attacks via $type metadata
        _settings.TypeNameHandling = TypeNameHandling.None;
    }

    public byte[] Serialize<T>(T message) where T : Message
    {
        if (message is null)
            throw new Interfaces.Exceptions.SerializationException(
                "Cannot serialize null message", typeof(T));

        try
        {
            string json = JsonConvert.SerializeObject(message, _settings);
            return Encoding.UTF8.GetBytes(json);
        }
        catch (JsonException ex)
        {
            throw new Interfaces.Exceptions.SerializationException(
                $"Failed to serialize message of type {typeof(T).Name}", typeof(T), ex);
        }
    }

    public T Deserialize<T>(byte[] data) where T : Message
    {
        return (T)Deserialize(data, typeof(T));
    }

    public object Deserialize(byte[] data, Type type)
    {
        try
        {
            string json = Encoding.UTF8.GetString(data);
            return JsonConvert.DeserializeObject(json, type, _settings)
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
