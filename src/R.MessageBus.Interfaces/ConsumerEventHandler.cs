using System.Collections.Generic;

namespace R.MessageBus.Interfaces
{
    public delegate ConsumeEventResult ConsumerEventHandler(byte[] message, string type, IDictionary<string, object> headers);
}