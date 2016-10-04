using System;
using ServiceConnect.Interfaces;

namespace ProcessManager.Messages
{
    public class Filter1 : IFilter
    {
        public bool Process(Envelope envelope)
        {
            Console.WriteLine("Filter1");
            return true;
        }

        public IBus Bus { get; set; }
    }

    public class Filter2 : IFilter
    {
        public bool Process(Envelope envelope)
        {
            Console.WriteLine("Filter2");
            return true;
        }

        public IBus Bus { get; set; }
    }
}