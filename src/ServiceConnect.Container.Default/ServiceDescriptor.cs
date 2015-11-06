using System;

namespace ServiceConnect.Container.Default
{
    public class ServiceDescriptor
    {
        public Type ServiceType { get; set; }
        public object Instance { get; set; }
    }
}
