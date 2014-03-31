using System;
using R.MessageBus.Interfaces;

namespace PublishSubscribe.Messages
{
    public class PublishSubscribeMessage : Message
    {
        public PublishSubscribeMessage(Guid correlationId) : base(correlationId)
        {
        }
    }
}