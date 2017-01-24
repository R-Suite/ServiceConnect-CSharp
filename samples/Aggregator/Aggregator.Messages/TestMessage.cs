using System;
using ServiceConnect.Interfaces;

namespace Aggregator.Messages
{
    public class TestMessage : Message
    {
        public TestMessage(Guid correlationId) : base(correlationId)
        {
        }
        public int Num { get; set; }
    }
}
