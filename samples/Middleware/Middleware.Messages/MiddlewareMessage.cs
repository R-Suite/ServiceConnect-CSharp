using System;
using ServiceConnect.Interfaces;

namespace Middleware.Messages
{
    public class MiddlewareMessage : Message
    {
        public MiddlewareMessage(Guid correlationId) : base(correlationId)
        {
        }

        public string Value { get; set; }
    }
}
