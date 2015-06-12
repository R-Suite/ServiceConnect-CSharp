using System;

namespace R.MessageBus.Core.Container
{
    public class ServiceDescriptor
    {
        public Type ServiceType { get; set; }
        public object Instance { get; set; }
    }
}
