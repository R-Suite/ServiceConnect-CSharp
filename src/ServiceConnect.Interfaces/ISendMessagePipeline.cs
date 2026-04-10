using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ServiceConnect.Interfaces
{
    public interface ISendMessagePipeline : IDisposable
    {
        Task ExecutePublishMessagePipelineAsync(Type typeObject, byte[] messageBytes, Dictionary<string, string>? headers = null, string? endPoint = null);
        Task ExecuteSendMessagePipelineAsync(Type typeObject, byte[] messageBytes, Dictionary<string, string>? headers = null, string? endPoint = null);
    }
}