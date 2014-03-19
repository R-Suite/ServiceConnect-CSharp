using System.IO;
using System.Runtime.Serialization.Formatters.Binary;

namespace R.MessageBus.Client.RabbitMQ
{
    public static class SerializationHelper
    {
        public static byte[] ToByteArray<T>(this T t)
        {
            using (var memoryStream = new MemoryStream())
            {
                var formatter = new BinaryFormatter();
                formatter.Serialize(memoryStream, t);
                return memoryStream.ToArray();
            }
        }

        public static object FromByteArray(byte[] data)
        {
            using (var stream = new MemoryStream())
            {
                stream.Write(data, 0, data.Length);
                stream.Position = 0;
                var formatter = new BinaryFormatter();
                return formatter.Deserialize(stream);
            }
        }
    }
}