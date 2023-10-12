using ServiceConnect.Interfaces;
using System;
using System.Collections.Generic;

namespace ServiceConnect.Core
{
    public class SendMessagePipeline : ISendMessagePipeline
    {
        private readonly IConfiguration _configuration;
        private readonly IProducer _producer;
        private readonly IBusContainer _container;

        public SendMessagePipeline(IConfiguration configuration)
        {
            _configuration = configuration;
            _producer = _configuration.GetProducer();
            _container = configuration.GetContainer();
        }

        public void ExecuteSendMessagePipeline(Type typeObject, byte[] messageBytes, Dictionary<string, string> headers = null, string endPoint = null)
        {
            ExecuteMessagePipeline(SendMessage, typeObject, messageBytes, headers, endPoint);
        }

        public void ExecutePublishMessagePipeline(Type typeObject, byte[] messageBytes, Dictionary<string, string> headers = null, string endPoint = null)
        {
            ExecuteMessagePipeline(PublishMessage, typeObject, messageBytes, headers, endPoint);
        }

        private void ExecuteMessagePipeline(SendMessageDelegate del, Type typeObject, byte[] messageBytes, Dictionary<string, string> headers = null, string endPoint = null)
        {
            SendMessageDelegate current = del;
            for (int i = _configuration.SendMessageMiddleware.Count; i > 0; i--)
            {
                ISendMessageMiddleware middleware = (ISendMessageMiddleware)_container.GetInstance(_configuration.SendMessageMiddleware[i - 1]);
                middleware.Next = current;
                current = middleware.Process;
            }
            current(typeObject, messageBytes, headers, endPoint);
        }

        private void SendMessage(Type typeObject, byte[] messageBytes, Dictionary<string, string> headers = null, string endPoint = null)
        {
            if (endPoint == null)
            {
                _producer.Send(typeObject, messageBytes, headers);
            }
            else
            {
                _producer.Send(endPoint, typeObject, messageBytes, headers);
            }
        }

        private void PublishMessage(Type typeObject, byte[] messageBytes, Dictionary<string, string> headers = null, string endPoint = null)
        {
            _producer.Publish(typeObject, messageBytes, headers);
        }

        public void Dispose()
        {
            _producer.Dispose();
        }
    }
}
