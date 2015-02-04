using System;
using System.Security.Cryptography.X509Certificates;
using R.MessageBus.Interfaces;

namespace Streaming.Messages
{
    public class StartStreamMessage : Message
    {
        public StartStreamMessage(Guid correlationId) : base(correlationId)
        {
            
        }
        public string Path { get; set; }
    }
}
