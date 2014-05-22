using System;
using System.Collections.Generic;

namespace R.MessageBus.Interfaces
{
    public interface IBusContainer
    {
        IEnumerable<HandlerReference> GetHandlerTypes();
        IEnumerable<HandlerReference> GetHandlerTypes(Type messageHandler);
        object GetInstance(Type handlerType);
        T GetInstance<T>(IDictionary<string, object> arguments);
        T GetInstance<T>();
        void ScanForHandlers();
        void Initialize();
        void AddBus(IBus bus);
        void AddHandler<T>(Type handlerType, T handler);
    }
}