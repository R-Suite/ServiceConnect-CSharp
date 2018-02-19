using System;
using ServiceConnect.Interfaces;

namespace RequestRepsonse.Messages
{
    public class ResponseMessage : Message
    {
        public ResponseMessage(Guid correlationId) : base(correlationId)
        {
        }
    }
}
