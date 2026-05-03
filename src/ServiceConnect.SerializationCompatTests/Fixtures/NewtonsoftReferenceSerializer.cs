using Newtonsoft.Json;
using ServiceConnect.Interfaces;
using System.Text;

namespace ServiceConnect.SerializationCompatTests.Fixtures;

/// <summary>
/// Reference v7 wire-format serializer — Newtonsoft.Json with the exact settings the
/// production NewtonsoftJsonMessageSerializer used pre-Phase-A.2. Decoupled from
/// <see cref="IMessageSerializer"/> by design: the production interface reduces in
/// Phase A.2 Task 6, and this fixture must keep representing v7 behaviour after that.
/// </summary>
internal static class NewtonsoftReferenceSerializer
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        NullValueHandling = NullValueHandling.Include,
        DefaultValueHandling = DefaultValueHandling.Include,
        ReferenceLoopHandling = ReferenceLoopHandling.Error,
        DateFormatHandling = DateFormatHandling.IsoDateFormat,
        DateTimeZoneHandling = DateTimeZoneHandling.RoundtripKind,
        Formatting = Formatting.None,
        TypeNameHandling = TypeNameHandling.None,
    };

    private static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);

    public static byte[] Serialize<T>(T message) where T : Message
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        using var ms = new MemoryStream();
        using (var sw = new StreamWriter(ms, Encoding.UTF8, bufferSize: 1024, leaveOpen: true))
        using (var jw = new JsonTextWriter(sw))
        {
            Serializer.Serialize(jw, message);
        }
        return ms.ToArray();
    }

    public static object Deserialize(byte[] data, Type type)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var sr = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: false);
        using var jr = new JsonTextReader(sr);
        return Serializer.Deserialize(jr, type)
            ?? throw new InvalidOperationException($"Newtonsoft reference deserialised to null for {type.Name}");
    }

    public static T Deserialize<T>(byte[] data) where T : Message
        => (T)Deserialize(data, typeof(T));
}
