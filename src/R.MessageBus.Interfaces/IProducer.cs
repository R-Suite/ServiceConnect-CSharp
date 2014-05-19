using System.Collections.Generic;

namespace R.MessageBus.Interfaces
{
    public interface IProducer
    {
        void Publish<T>(T message, Dictionary<string, object> headers = null) where T : Message;
        void Send<T>(T message, Dictionary<string, object> headers = null) where T : Message;
        void Send<T>(string endPoint, T message, Dictionary<string, object> headers = null) where T : Message;
        void Disconnect();
        void Dispose();
    }
}