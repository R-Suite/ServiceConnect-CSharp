using System;
using ServiceConnect.Interfaces;

namespace CrossVersionSupport.Messages
{
    public class ServiceConnectMessage : Message
    {
        public ServiceConnectMessage(Guid correlationId) : base(correlationId)
        {
        }

        public dynamic Test { get; set; }
    }
}
