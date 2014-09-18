using System.Collections.Generic;

namespace R.MessageBus.Interfaces
{
    public delegate ConsumeEventResult ConsumerEventHandler(object message, IDictionary<string, object> headers);
}