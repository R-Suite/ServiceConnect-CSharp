using System;
using PointToPoint.Messages;
using R.MessageBus;

namespace PointToPoint.Producer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Producer ***********");
            var bus = Bus.Initialize(config =>
            {
                config.AddQueueMapping(typeof(PointToPointMessage), "rmessagebus.stomp.pointtopoint.consumer");
                config.SetThreads(10);
                config.SetHost("lonappdev04");
            });

            while (true)
            {
                Console.WriteLine("Press enter to send message");
                Console.ReadLine();

                Console.WriteLine("Start: {0}", DateTime.Now);
                var id = Guid.NewGuid();
                for (int i = 0; i < 100000; i++)
                {
                    bus.Send(new PointToPointMessage(id));
                    //Console.ReadLine();
                }

                Console.WriteLine("Sent message - {0}", id);
                Console.WriteLine("");
            }
        }
    }
}
