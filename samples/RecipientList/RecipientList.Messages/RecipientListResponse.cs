using System;
using R.MessageBus.Interfaces;

namespace RecipientList.Messages
{
    public class RecipientListResponse : Message
    {
        public RecipientListResponse(Guid correlationId) : base(correlationId) { }

        public string Endpoint { get; set; }
    }
}