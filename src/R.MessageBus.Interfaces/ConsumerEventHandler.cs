using System.Collections.Generic;

namespace R.MessageBus.Interfaces
{
    public delegate ConsumeEventResult ConsumerEventHandler(string message, string type, IDictionary<string, object> headers);
}