using System;
using ServiceConnect.Interfaces;

namespace RecipientList.Messages
{
    public class RecipientListMessage : Message
    {
        public RecipientListMessage(Guid correlationId) : base(correlationId) { }
        public bool SendReply { get; set; }
        public bool Delay { get; set; }
    }
}