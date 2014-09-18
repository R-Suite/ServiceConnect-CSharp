using System;
using Newtonsoft.Json;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Core
{
    /// <summary>
    /// Json message serializer.
    /// </summary>
    public class JsonMessageSerializer : IMessageSerializer
    {
        public object Deserialize(string typeName, string messageJson)
        {
            return JsonConvert.DeserializeObject(messageJson, Type.GetType(typeName));
        }

        public string Serialize<T>(T message)
        {
            return JsonConvert.SerializeObject(message);
        }
    }
}
