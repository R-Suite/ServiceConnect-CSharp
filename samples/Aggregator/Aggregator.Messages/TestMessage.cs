using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using R.MessageBus.Interfaces;

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
