using System;
using R.MessageBus.Interfaces;

namespace RecipientList.Messages
{
    public class RecipientListMessage : Message
    {
        public RecipientListMessage(Guid correlationId) : base(correlationId) { }
        public bool SendReply { get; set; }
    }
}