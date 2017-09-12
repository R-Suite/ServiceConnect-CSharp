using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ServiceConnect.Interfaces;

namespace AsyncProcessManager.Messages
{
    public class StartMessage : Message
    {
        public StartMessage(Guid correlationId) : base(correlationId)
        {
        }
    }
}
