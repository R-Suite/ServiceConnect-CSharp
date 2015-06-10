using System;
using R.MessageBus.Interfaces;

namespace ProcessManager.Messages
{
    public class Filter1 : IFilter
    {
        public bool Process(Envelope envelope)
        {
            Console.WriteLine("Filter1");
            return true;
        }
    }

    public class Filter2 : IFilter
    {
        public bool Process(Envelope envelope)
        {
            Console.WriteLine("Filter2");
            return true;
        }
    }
}