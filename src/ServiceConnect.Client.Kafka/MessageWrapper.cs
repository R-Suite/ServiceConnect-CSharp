using System;
using System.Collections.Generic;
using System.Text;

namespace ServiceConnect.Client.Kafka
{
    public class MessageWrapper
    {
        public Dictionary<string, object> Headers { get; set; }
        public byte[] Message { get; set; }
    }
}
