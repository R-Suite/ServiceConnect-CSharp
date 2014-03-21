using System;

namespace R.MessageBus.Interfaces
{
    public class VersionData<T> where T : IProcessManagerData
    {
        public Guid Id { get; set; }
        public int Version { get; set; }
        public T Data { get; set; }
    }
}
