using System;
using ServiceConnect.Interfaces;

namespace BusDisposeTest.Messages
{
    public class TestMsg : Message
    {
        public TestMsg(Guid correlationId) : base(correlationId)
        {
        }
    }
}
