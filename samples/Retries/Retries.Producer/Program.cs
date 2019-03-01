using System;
using ServiceConnect;
using Retries.Messages;

namespace Retries.Producer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Producer ***********");
            var bus = Bus.Initialize(config =>
            {
                config.SetHost("localhost");
                config.AddQueueMapping(typeof (RetryMessage), "RetryTest");
            });

            while (true)
            {
                Console.WriteLine("Press enter to send message");
                Console.ReadLine();

                var id = Guid.NewGuid();
                bus.Send(new RetryMessage(id));

                Console.WriteLine("Sent message - {0}", id);
                Console.WriteLine("");
            }
        }
    }
}
