using System;
using System.Collections.Generic;

namespace R.MessageBus.Interfaces
{
    public interface IProducer : IDisposable
    {
        void Publish<T>(T message, Dictionary<string, string> headers = null) where T : Message;
        void Send<T>(T message, Dictionary<string, string> headers = null) where T : Message;
        void Send<T>(string endPoint, T message, Dictionary<string, string> headers = null) where T : Message;
        void Disconnect();
        string Type { get;}
        long MaximumMessageSize { get; }
        void SendBytes(string endPoint, byte[] packet, Dictionary<string, string> headers);
    }
}