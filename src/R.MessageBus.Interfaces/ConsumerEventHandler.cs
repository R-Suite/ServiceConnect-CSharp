using System.Collections.Generic;

namespace R.MessageBus.Interfaces
{
    public delegate ConsumeEventResult ConsumerEventHandler(byte[] message, IDictionary<string, object> headers);
}