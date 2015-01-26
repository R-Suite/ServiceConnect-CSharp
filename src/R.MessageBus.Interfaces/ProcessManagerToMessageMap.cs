using System;
using System.Collections.Generic;

namespace R.MessageBus.Interfaces
{
    public class ProcessManagerToMessageMap
    {
        public Func<object, object> MessageProp;
        public Type MessageType;
        public Dictionary<string, Type> PropertiesHierarchy;
    }
}