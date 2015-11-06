using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ServiceConnect.Interfaces;

namespace ScatterGather.Messages
{
    public class Response : Message
    {
        public Response(Guid correlationId) : base(correlationId)
        {
        }

        public string Endpoint { get; set; }
    }
}
