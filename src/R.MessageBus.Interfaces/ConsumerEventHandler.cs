using System.Collections.Generic;

namespace R.MessageBus.Interfaces
{
    public delegate bool ConsumerEventHandler(byte[] message, IDictionary<string, object> headers);
}