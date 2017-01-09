using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ServiceConnect.Interfaces
{
    public class TimeoutMessage : Message
    {
        public TimeoutMessage(Guid correlationId) : base(correlationId)
        {
        }
    }
}
