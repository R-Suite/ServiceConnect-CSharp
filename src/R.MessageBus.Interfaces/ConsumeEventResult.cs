using System;

namespace R.MessageBus.Interfaces
{
    public class ConsumeEventResult
    {
        public bool Success { get; set; }
        public Exception Exception { get; set; }
    }
}