using System;
using R.MessageBus;

namespace RequestResponse.Responder
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Responder ***********");

            var bus = Bus.Initialize(x =>
            {
                x.ScanForMesssageHandlers = true;
                x.SetQueueName("Responder");
            });

            bus.StartConsuming();

            Console.ReadLine();
        }
    }
}
