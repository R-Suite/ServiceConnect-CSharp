using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ServiceConnect.Interfaces
{
    public delegate Task SendMessageDelegate(Type typeObject, byte[] messageBytes, Dictionary<string, string> headers = null, string endPoint = null);

    public interface ISendMessageMiddleware
    {
        SendMessageDelegate Next { get; set; }

        Task Process(Type typeObject, byte[] messageBytes, Dictionary<string, string> headers = null, string endPoint = null);
    }
}
