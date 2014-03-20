using System;
using System.Collections.Generic;

namespace R.MessageBus.Interfaces
{
    public interface IBusContainer
    {
        IEnumerable<HandlerReference> GetHandlerTypes();
        IEnumerable<HandlerReference> GetHandlerTypes(Type messageHandler);
        object GetHandlerInstance(Type handlerType);
    }
}