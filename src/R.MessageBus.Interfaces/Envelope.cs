using System.Collections.Generic;

namespace R.MessageBus.Interfaces
{
    public class Envelope
    {
        public IDictionary<string, object> Headers { get; set; }
        public byte[] Body { get; set; }
    }
}