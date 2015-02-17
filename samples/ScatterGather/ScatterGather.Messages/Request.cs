using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using R.MessageBus.Interfaces;

namespace ScatterGather.Messages
{
    public class Request : Message
    {
        public Request(Guid correlationId) : base(correlationId)
        {
        }

        public bool Delay { get; set; }
    }
}
