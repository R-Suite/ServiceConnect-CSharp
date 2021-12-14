using System;
using System.Collections.Generic;

namespace ServiceConnect.Interfaces
{
    public interface ISendMessagePipeline
    {
        void ExecutePublishMessagePipeline(Type typeObject, byte[] messageBytes, Dictionary<string, string> headers = null, string endPoint = null);
        void ExecuteSendMessagePipeline(Type typeObject, byte[] messageBytes, Dictionary<string, string> headers = null, string endPoint = null);
    }
}