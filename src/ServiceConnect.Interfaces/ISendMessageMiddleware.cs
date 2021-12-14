using System;
using System.Collections.Generic;

namespace ServiceConnect.Interfaces
{
    public delegate void SendMessageDelegate(Type typeObject, byte[] messageBytes, Dictionary<string, string> headers = null, string endPoint = null);

    public interface ISendMessageMiddleware
    {
        SendMessageDelegate Next { get; set; }

        void Process(Type typeObject, byte[] messageBytes, Dictionary<string, string> headers = null, string endPoint = null);
    }
}
