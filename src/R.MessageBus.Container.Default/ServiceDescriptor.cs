using System;

namespace R.MessageBus.Container.Default
{
    public class ServiceDescriptor
    {
        public Type ServiceType { get; set; }
        public object Instance { get; set; }
    }
}
