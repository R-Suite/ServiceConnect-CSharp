using System;
using R.MessageBus.Interfaces;

namespace Retries.Messages
{
    public class RetryMessage : Message
    {
        public RetryMessage(Guid correlationId) : base(correlationId) { }
    }
}
