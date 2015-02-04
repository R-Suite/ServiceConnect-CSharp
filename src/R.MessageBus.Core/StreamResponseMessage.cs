using System;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Core
{
    public class StreamResponseMessage : Message
    {
        public StreamResponseMessage(Guid correlationId)
            : base(correlationId)
        {
        }
    }
}