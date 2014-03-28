using System;

namespace R.MessageBus.Interfaces
{
    public class HandlerReference
    {
        public Type MessageType { get; set; }
        public Type HandlerType { get; set; }
    }
}
