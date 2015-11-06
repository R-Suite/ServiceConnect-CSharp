using System;
using ServiceConnect.Interfaces;

namespace PublishSubscribe.Messages
{
    public class PublishSubscribeMessage : Message
    {
        public PublishSubscribeMessage(Guid correlationId) : base(correlationId){}
    }
}