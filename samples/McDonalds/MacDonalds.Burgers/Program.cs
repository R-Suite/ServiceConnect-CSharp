using System;
using R.MessageBus;

namespace MacDonalds.Burgers
{
    class Program
    {
        static void Main(string[] args)
        {
            var bus = Bus.Initialize(x =>
            {
                x.ScanForMesssageHandlers = true;
            });

            bus.StartConsuming();

            Console.ReadLine();
        }
    }
}
