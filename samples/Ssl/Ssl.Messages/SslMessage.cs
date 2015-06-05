using System;
using R.MessageBus.Interfaces;

namespace Ssl.Messages
{
    public class SslMessage : Message
    {
        public SslMessage(Guid correlationId) : base(correlationId)
        {
        }
    }
}