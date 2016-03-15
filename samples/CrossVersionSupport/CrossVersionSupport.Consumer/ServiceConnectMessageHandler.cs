using System;
using CrossVersionSupport.Messages;
using ServiceConnect.Interfaces;

namespace CrossVersionSupport.Consumer
{
    public class ServiceConnectMessageHandler : IMessageHandler<ServiceConnectMessage>
    {
        public void Execute(ServiceConnectMessage message)
        {
            Console.WriteLine("ServiceConnectMessageHandler received message - {0}", message.Test);
        }

        public IConsumeContext Context { get; set; }
    }
}
