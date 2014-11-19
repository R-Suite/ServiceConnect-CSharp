using System;
using R.MessageBus;

namespace PublishSubscribe.Consumer1
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer 1 ***********");
            var bus = Bus.Initialize(x =>
            {
                x.ScanForMesssageHandlers = true;
                x.SetHost("lonappdev04");
            });

            bus.StartConsuming();

            Console.ReadLine();
        }
    }
}
